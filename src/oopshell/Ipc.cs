using System.Net.Sockets;
using System.Text;

namespace oopshell;

/// <summary>
/// A single-instance command channel, so compositor key bindings and scripts can reach a running shell:
/// <code>
/// // in the shell
/// using var ipc = IpcServer.Listen(app, "myshell", args => args[0] switch { "toggle" => Toggle(), _ => "unknown" });
/// // in `myshell toggle` (another process)
/// string? reply = IpcServer.Send("myshell", "toggle");   // null: not running
/// </code>
/// The socket is <c>$XDG_RUNTIME_DIR/&lt;name&gt;.sock</c> (or the name itself if it is an absolute
/// path). Requests are served on the UI thread from the event loop, without threads or blocking reads.
/// </summary>
public sealed class IpcServer : IDisposable
{
    const int MaxRequest = 64 * 1024;
    static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(2);

    readonly OopsApp app;
    readonly Socket listener;
    readonly IDisposable watch;
    readonly Func<IReadOnlyList<string>, string?> handler;
    readonly HashSet<Client> clients = new();
    bool disposed;

    sealed class Client(Socket socket)
    {
        public readonly Socket Socket = socket;
        public readonly MemoryStream Request = new();
        public IDisposable? Watch;
    }

    IpcServer(OopsApp app, Socket listener, string path, Func<IReadOnlyList<string>, string?> handler)
    {
        this.app = app;
        this.listener = listener;
        this.handler = handler;
        SocketPath = path;
        watch = app.WatchFd((int)listener.Handle, FdEvents.Readable, _ => Accept());
    }

    /// <summary>The socket file.</summary>
    public string SocketPath { get; }

    public static string PathFor(string name) =>
        Path.IsPathRooted(name) ? name : Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp", name + ".sock");

    /// <summary>
    /// Starts serving <paramref name="name"/>. <paramref name="handler"/> gets the arguments a client sent and
    /// returns the reply (null for none). Throws <see cref="InvalidOperationException"/> if another instance is
    /// already serving it; a stale socket file left by a crash is replaced.
    /// </summary>
    public static IpcServer Listen(OopsApp app, string name, Func<IReadOnlyList<string>, string?> handler)
    {
        string path = PathFor(name);
        if (File.Exists(path))
        {
            if (Send(name) is not null)
                throw new InvalidOperationException($"Another instance is already serving {path}.");
            File.Delete(path);
        }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(16);
        return new IpcServer(app, listener, path, handler);
    }

    /// <summary>True if an instance is serving <paramref name="name"/>.</summary>
    public static bool IsRunning(string name) => Send(name) is not null;

    /// <summary>
    /// Sends <paramref name="args"/> to the instance serving <paramref name="name"/> and returns its reply
    /// ("" if it had none), or null if nothing is serving it. Blocks for at most two seconds.
    /// With no arguments it only checks that the instance answers.
    /// </summary>
    public static string? Send(string name, params string[] args)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                SendTimeout = (int)ClientTimeout.TotalMilliseconds,
                ReceiveTimeout = (int)ClientTimeout.TotalMilliseconds,
            };
            socket.Connect(new UnixDomainSocketEndPoint(PathFor(name)));
            socket.Send(Encoding.UTF8.GetBytes(string.Join('\0', args)));
            socket.Shutdown(SocketShutdown.Send);
            using var reply = new MemoryStream();
            var buffer = new byte[4096];
            int n;
            while ((n = socket.Receive(buffer)) > 0)
                reply.Write(buffer, 0, n);
            return Encoding.UTF8.GetString(reply.GetBuffer(), 0, (int)reply.Length);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    void Accept()
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = listener.Accept();
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
            {
                return;
            }
            socket.Blocking = false;
            var client = new Client(socket);
            clients.Add(client);
            client.Watch = app.WatchFd((int)socket.Handle, FdEvents.Readable, _ => Read(client));
            // A client that never finishes its request is dropped.
            app.SetTimer(ClientTimeout, null, () => Drop(client));
        }
    }

    void Read(Client client)
    {
        var buffer = new byte[4096];
        while (true)
        {
            int n;
            try
            {
                n = client.Socket.Receive(buffer);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
            {
                return; // more later
            }
            catch (SocketException)
            {
                Drop(client);
                return;
            }
            if (n == 0)
                break; // the client finished its request
            client.Request.Write(buffer, 0, n);
            if (client.Request.Length > MaxRequest)
            {
                Drop(client);
                return;
            }
        }
        client.Watch?.Dispose();
        client.Watch = null;
        string text = Encoding.UTF8.GetString(client.Request.GetBuffer(), 0, (int)client.Request.Length);
        string? reply = null;
        if (text.Length > 0)
        {
            try
            {
                reply = handler(text.Split('\0'));
            }
            catch (Exception e)
            {
                reply = $"error: {e.Message}";
            }
        }
        try
        {
            client.Socket.Blocking = true;
            client.Socket.SendTimeout = (int)ClientTimeout.TotalMilliseconds;
            if (reply is { Length: > 0 })
                client.Socket.Send(Encoding.UTF8.GetBytes(reply));
        }
        catch (SocketException)
        {
            // The client went away; nothing to answer.
        }
        Drop(client);
    }

    void Drop(Client client)
    {
        if (!clients.Remove(client))
            return;
        client.Watch?.Dispose();
        client.Socket.Dispose();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        watch.Dispose();
        foreach (var client in clients.ToList())
            Drop(client);
        listener.Dispose();
        File.Delete(SocketPath);
    }
}
