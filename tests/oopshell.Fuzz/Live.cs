using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using oopshell.Services.DBus;
using oopshell.Services.Greetd;
using oopshell.Services.Media;
using oopshell.Services.Notifications;
using oopshell.Services.Tray;
using Tmds.DBus.Protocol;

namespace oopshell.Fuzz;

/// <summary>Targets that need a running oopshell app: a private headless sway and a private D-Bus daemon.</summary>
internal static class Live
{
    /// <summary>The app's event loop died: what would have killed a shell.</summary>
    public sealed class FatalException(Exception inner) : Exception("The app crashed.", inner);

    private static readonly List<Process> s_processes = new();
    private static string? s_runtimeDir, s_waylandSocket, s_busAddress;

    private static void Start()
    {
        if (s_runtimeDir is not null)
            return;
        s_runtimeDir = Directory.CreateTempSubdirectory("oopsfz.").FullName;
        File.WriteAllText(Path.Combine(s_runtimeDir, "sway.conf"), "output HEADLESS-1 resolution 800x600\n");
        Spawn("sway", ["-c", Path.Combine(s_runtimeDir, "sway.conf")], new() { ["WLR_BACKENDS"] = "headless", ["WLR_LIBINPUT_NO_DEVICES"] = "1", ["WLR_RENDERER"] = "pixman" });
        for (int i = 0; i < 100 && s_waylandSocket is null; i++, Thread.Sleep(50))
            s_waylandSocket = Directory.GetFiles(s_runtimeDir, "wayland-*").FirstOrDefault(f => !f.EndsWith(".lock"));
        var bus = Spawn("dbus-daemon", ["--session", "--nofork", "--print-address=1"], new(), readOutput: false);
        s_busAddress = bus.StandardOutput.ReadLine();
        if (s_waylandSocket is null || s_busAddress is null)
            throw new InvalidOperationException("Could not start headless sway or dbus-daemon.");
    }

    private static Process Spawn(string program, string[] args, Dictionary<string, string> env, bool readOutput = true)
    {
        var start = new ProcessStartInfo(program, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        start.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME");
        start.Environment["XDG_RUNTIME_DIR"] = s_runtimeDir;
        foreach (var (k, v) in env)
            start.Environment[k] = v;
        var p = Process.Start(start)!;
        if (readOutput)
        {
            p.OutputDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
        }
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line && Environment.GetEnvironmentVariable("OOPSFUZZ_LOG") is { } log)
                File.AppendAllText(log, $"{program}: {line}\n");
        };
        p.BeginErrorReadLine();
        s_processes.Add(p);
        return p;
    }

    public static void Shutdown()
    {
        foreach (var p in Enumerable.Reverse(s_processes))
        {
            if (!p.HasExited)
                p.Kill();
        }
        if (s_runtimeDir is not null)
            Directory.Delete(s_runtimeDir, recursive: true);
    }

    private static OopsApp NewApp()
    {
        Start();
        return OopsApp.Connect(new OopsOptions { Display = s_waylandSocket });
    }

    /// <summary>Runs the app's loop until <paramref name="done"/> or the timeout; an exception out of the loop is a crash.</summary>
    private static void Pump(OopsApp app, Func<bool> done, int timeoutMs = 300)
    {
        var deadline = Stopwatch.StartNew();
        while (!done() && deadline.ElapsedMilliseconds < timeoutMs)
        {
            using var tick = app.SetTimer(TimeSpan.FromMilliseconds(5), null, () => app.Quit());
            try
            {
                app.Run();
            }
            catch (Exception e)
            {
                throw new FatalException(e);
            }
            if (app.ConnectionError is { } error)
                throw new FatalException(error);
        }
    }

    private static T Await<T>(OopsApp app, Task<T> task, int timeoutMs = 2000)
    {
        Pump(app, () => task.IsCompleted, timeoutMs);
        return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException("No answer.");
    }

    // ---- random D-Bus values --------------------------------------------------------------------

    /// <summary>A mutated string a peer could really send: the bus refuses NUL and invalid UTF-8 (and disconnects the sender).</summary>
    private static string Wire(string s) => new(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s.Replace('\0', ' ')))
        .Where(c => c is not ('\uFFFE' or '\uFFFF') && c is not (>= '\uFDD0' and <= '\uFDEF')).ToArray()); // libdbus also refuses noncharacters

    private static VariantValue RandomVariant(Mutator m, int depth = 0)
    {
        switch (m.Random.Next(depth > 1 ? 8 : 12))
        {
            case 0: return (byte)m.Random.Next(256);
            case 1: return m.Random.Next(2) == 0;
            case 2: return m.Random.Next(int.MinValue, int.MaxValue);
            case 3: return (uint)m.Random.Next();
            case 4: return (ulong)m.Random.NextInt64();
            case 5: return Wire(m.Mutate("text"));
            case 6: return new ObjectPath("/a/b" + m.Random.Next(9));
            case 7: return new Array<byte>(Enumerable.Range(0, m.Random.Next(64)).Select(_ => (byte)m.Random.Next(256)));
            case 8: return new Array<string>(Enumerable.Range(0, m.Random.Next(4)).Select(_ => Wire(m.Mutate("s"))));
            case 9: return new Dict<string, VariantValue>(Enumerable.Range(0, m.Random.Next(4)).ToDictionary(i => "k" + i, _ => RandomVariant(m, depth + 1)));
            case 10:
                // Shaped like notification image-data (iiibiiay) with sizes that may not match the data.
                int w = m.Random.Next(-2, 70000), h = m.Random.Next(-2, 70000);
                return new Struct<int, int, int, bool, int, int, Array<byte>>(w, h, m.Random.Next(-8, 300000), m.Random.Next(2) == 0, 8, 4,
                    new Array<byte>(new byte[m.Random.Next(0, 4096)]));
            default: return new Array<int>(Enumerable.Range(0, m.Random.Next(6)).Select(_ => m.Random.Next()));
        }
    }

    /// <summary>A peer's object that answers Properties.Get/GetAll with whatever the fuzzer decided.</summary>
    private sealed class FakeObject(OopsApp app, string path) : ExportedObject(app, path)
    {
        public Dictionary<string, VariantValue> Properties = new();

        protected override void HandleMethod(MethodContext context)
        {
            if (context.Request.MemberAsString == "GetAll")
            {
                using var w = context.CreateReplyWriter("a{sv}");
                w.WriteDictionary(Properties);
                context.Reply(w.CreateMessage());
            }
            else if (context.Request.MemberAsString == "Get")
            {
                var r = context.Request.GetBodyReader();
                r.ReadString();
                string name = r.ReadString();
                using var w = context.CreateReplyWriter("v");
                w.WriteVariant(Properties.TryGetValue(name, out var v) ? v : (VariantValue)0);
                context.Reply(w.CreateMessage());
            }
            else
                ReplyEmpty(context);
        }
    }

    // ---- targets --------------------------------------------------------------------------------

    public static IEnumerable<(string, Action<Mutator>)> Targets(Findings findings, string work, string[] tokens)
    {
        yield return ("greetd", m =>
        {
            // A greetd that answers with mutated JSON (and sometimes a lying length).
            string sock = Path.Combine(work, "g.sock");
            File.Delete(sock);
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(sock));
            listener.Listen(1);
            string[] replies = ["{\"type\":\"auth_message\",\"auth_message_type\":\"secret\",\"auth_message\":\"Password:\"}", "{\"type\":\"success\"}",
                "{\"type\":\"error\",\"error_type\":\"auth_error\",\"description\":\"x\"}"];
            var payloads = Enumerable.Range(0, 3).Select(i => Encoding.UTF8.GetBytes(m.Mutate(replies[m.Random.Next(replies.Length)]))).ToList();
            findings.Input = string.Join("\n", payloads.Select(Encoding.UTF8.GetString));
            var server = Task.Run(() =>
            {
                using var c = listener.Accept();
                c.ReceiveTimeout = 500;
                foreach (var payload in payloads)
                {
                    var header = new byte[4];
                    if (c.Receive(header) < 4)
                        return;
                    var body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
                    c.Receive(body);
                    var frame = new byte[4 + payload.Length];
                    BinaryPrimitives.WriteUInt32LittleEndian(frame, m.Random.Next(8) == 0 ? (uint)m.Random.Next() : (uint)payload.Length);
                    payload.CopyTo(frame, 4);
                    c.Send(frame);
                }
            });
            using var client = GreetdClient.ConnectAsync(sock).GetAwaiter().GetResult();
            var login = client.LoginAsync("u", "p");
            // A misbehaving greetd may make LoginAsync fail (it is trusted, root); it must not hang or crash.
            if (!((Task)login).ContinueWith(_ => { }).Wait(1500))
                throw new TimeoutException("LoginAsync did not finish.");
            try
            {
                login.GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or IOException or KeyNotFoundException or InvalidOperationException)
            {
            }
            server.Wait(500);
        }
        );

        OopsApp? ipcApp = null;
        IpcServer? ipc = null;
        yield return ("ipc", m =>
        {
            ipcApp ??= NewApp();
            string name = Path.Combine(s_runtimeDir!, "fuzz.sock");
            ipc ??= IpcServer.Listen(ipcApp, name, a => a.Count > 0 && a[0] == "boom" ? throw new InvalidOperationException("handler") : string.Join("|", a));
            var bytes = m.MutateBytes(Encoding.UTF8.GetBytes("toggle\0arg\0" + Wire(m.Mutate("x"))));
            findings.Input = Convert.ToHexString(bytes);
            var send = Task.Run(() =>
            {
                using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { ReceiveTimeout = 2000 };
                s.Connect(new UnixDomainSocketEndPoint(name));
                s.Send(bytes);
                if (m.Random.Next(4) != 0)
                    s.Shutdown(SocketShutdown.Send);
                else
                    return; // hang up without finishing the request
                var buffer = new byte[65536];
                while (s.Receive(buffer) > 0)
                {
                }
            });
            Pump(ipcApp, () => send.IsCompleted, 3000);
            if (!Await(ipcApp, Task.Run(() => IpcServer.IsRunning(name))))
                throw new InvalidOperationException("The IPC server stopped answering.");
        }
        );

        OopsApp? nApp = null;
        DBusConnection? nClient = null;
        NotificationServer? notifications = null;
        yield return ("notifications", m =>
        {
            if (nApp is null)
            {
                nApp = NewApp();
                var serverBus = Await(nApp, OopsDBus.ConnectAsync(nApp, s_busAddress));
                notifications = Await(nApp, NotificationServer.StartAsync(nApp, serverBus));
                nClient = Await(nApp, OopsDBus.ConnectAsync(nApp, s_busAddress));
            }
            var hints = new Dictionary<string, VariantValue>();
            for (int i = m.Random.Next(6); i > 0; i--)
                hints[new[] { "urgency", "image-data", "image_data", "icon_data", "image-path", "desktop-entry", "category", "resident", "transient", "x", "y", "sound-file", "value" }[m.Random.Next(13)] + (m.Random.Next(8) == 0 ? "z" : "")] = RandomVariant(m);
            string[] actions = Enumerable.Range(0, m.Random.Next(5)).Select(_ => Wire(m.Mutate("default"))).ToArray();
            findings.Input = string.Join("; ", hints.Select(h => $"{h.Key}={h.Value}")) + " actions=" + string.Join(",", actions);
            var call = nClient!.CallAsync("org.freedesktop.Notifications", "/org/freedesktop/Notifications", "org.freedesktop.Notifications", "Notify",
                (msg, _) => msg.GetBodyReader().ReadUInt32(), (ref MessageWriter w) =>
                {
                    w.WriteString(Wire(m.Mutate("app")));
                    w.WriteUInt32((uint)m.Random.Next(4));
                    w.WriteString(Wire(m.Mutate("icon")));
                    w.WriteString(Wire(m.Mutate("<b>summary</b>")));
                    w.WriteString(Wire(m.Mutate("body &amp; <a href=\"x\">link</a>")));
                    w.WriteArray(actions);
                    w.WriteDictionary(hints);
                    w.WriteInt32(m.Random.Next(-5, 100000));
                }, "susssasa{sv}i");
            try
            {
                Await(nApp, call);
            }
            catch (DBusErrorReplyException)
            {
                // Refusing bad input with an error is fine; crashing or hanging is not.
            }
            foreach (var n in notifications!.Notifications.ToList())
            {
                if (m.Random.Next(3) == 0)
                    notifications.Close(n);
            }
        }
        );

        OopsApp? mApp = null;
        FakeObject? player = null;
        DBusConnection? playerBus = null;
        MediaService? media = null;
        yield return ("media", m =>
        {
            if (mApp is null)
            {
                mApp = NewApp();
                playerBus = Await(mApp, OopsDBus.ConnectAsync(mApp, s_busAddress));
                player = new FakeObject(mApp, "/org/mpris/MediaPlayer2");
                playerBus.AddMethodHandler(player);
                Await(mApp, playerBus.RequestNameOrThrowAsync("org.mpris.MediaPlayer2.fuzz").ContinueWith(_ => true));
                media = Await(mApp, MediaService.StartAsync(mApp, Await(mApp, OopsDBus.ConnectAsync(mApp, s_busAddress))));
            }
            var props = new Dictionary<string, VariantValue>();
            foreach (var key in new[] { "Metadata", "PlaybackStatus", "CanPlay", "CanPause", "CanGoNext", "CanGoPrevious", "Identity", "DesktopEntry", "Position", "LoopStatus" })
            {
                if (m.Random.Next(3) != 0)
                    props[key] = key == "Metadata" && m.Random.Next(2) == 0
                        ? new Dict<string, VariantValue>(new Dictionary<string, VariantValue>
                        {
                            ["xesam:title"] = RandomVariant(m),
                            ["xesam:artist"] = RandomVariant(m),
                            ["mpris:artUrl"] = RandomVariant(m),
                            ["mpris:length"] = RandomVariant(m),
                            ["xesam:album"] = RandomVariant(m),
                        })
                        : RandomVariant(m);
            }
            player!.Properties = props;
            findings.Input = string.Join("; ", props.Select(p => $"{p.Key}={p.Value}"));
            playerBus!.EmitSignal("/org/mpris/MediaPlayer2", "org.freedesktop.DBus.Properties", "PropertiesChanged", "sa{sv}as", (ref MessageWriter w) =>
            {
                w.WriteString("org.mpris.MediaPlayer2.Player");
                w.WriteDictionary(props);
                w.WriteArray(Array.Empty<string>());
            });
            Pump(mApp, () => false, 30);
            _ = media!.Active?.Title;
        }
        );

        OopsApp? tApp = null;
        FakeObject? item = null;
        DBusConnection? itemBus = null;
        TrayHost? tray = null;
        yield return ("tray", m =>
        {
            if (tApp is null)
            {
                tApp = NewApp();
                tray = Await(tApp, TrayHost.StartAsync(tApp, Await(tApp, OopsDBus.ConnectAsync(tApp, s_busAddress))));
                itemBus = Await(tApp, OopsDBus.ConnectAsync(tApp, s_busAddress));
                item = new FakeObject(tApp, "/StatusNotifierItem");
                itemBus.AddMethodHandler(item);
            }
            var props = new Dictionary<string, VariantValue>();
            foreach (var key in new[] { "Id", "Title", "Status", "IconName", "IconPixmap", "AttentionIconPixmap", "ToolTip", "Menu", "ItemIsMenu", "Category", "IconThemePath" })
            {
                if (m.Random.Next(3) != 0)
                    props[key] = key.EndsWith("Pixmap") && m.Random.Next(2) == 0
                        ? new Array<Struct<int, int, Array<byte>>>([new Struct<int, int, Array<byte>>(m.Random.Next(-4, 70000), m.Random.Next(-4, 70000), new Array<byte>(new byte[m.Random.Next(0, 4096)]))])
                        : RandomVariant(m);
            }
            item!.Properties = props;
            findings.Input = string.Join("; ", props.Select(p => $"{p.Key}={p.Value}"));
            var register = itemBus!.CallAsync(TrayHost.WatcherName, TrayHost.WatcherPath, "org.kde.StatusNotifierWatcher", "RegisterStatusNotifierItem",
                (ref MessageWriter w) => w.WriteString("/StatusNotifierItem"), "s");
            try
            {
                Await(tApp, register.ContinueWith(_ => true));
            }
            catch (TimeoutException)
            {
            }
            itemBus.EmitSignal("/StatusNotifierItem", "org.kde.StatusNotifierItem", new[] { "NewIcon", "NewTitle", "NewStatus", "NewToolTip" }[m.Random.Next(4)]);
            Pump(tApp, () => false, 30);
            foreach (var i in tray!.Items)
                _ = i.Title;
        }
        );
    }
}
