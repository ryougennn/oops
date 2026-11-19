using System.Net.Sockets;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class IpcTests(SwayFixture sway)
{
    string Name => Path.Combine(sway.RuntimeDir, $"ipc-{Guid.NewGuid():N}.sock");

    [Fact]
    public void Commands_reach_the_running_instance_and_replies_come_back()
    {
        using var app = sway.Connect();
        string name = Name;
        var received = new List<IReadOnlyList<string>>();
        using var server = IpcServer.Listen(app, name, args =>
        {
            received.Add(args);
            return args[0] == "echo" ? string.Join("|", args.Skip(1)) : null;
        });

        Assert.Equal(new IpcReply(IpcStatus.Ok, "a b|ü日本|"), app.Complete(Task.Run(() => IpcServer.Send(name, "echo", "a b", "ü日本", ""))));
        Assert.Equal(new IpcReply(IpcStatus.Ok, ""), app.Complete(Task.Run(() => IpcServer.Send(name, "toggle"))));
        Assert.Equal(["toggle"], received[1]);

        // A ping (no arguments) answers without reaching the handler.
        Assert.True(app.Complete(Task.Run(() => IpcServer.IsRunning(name))));
        Assert.Equal(2, received.Count);

        // One instance per name.
        var second = Assert.Throws<InvalidOperationException>(() => app.Complete(Task.Run(() => IpcServer.Listen(app, name, _ => null))));
        Assert.Contains("already", second.Message);
    }

    [Fact]
    public void Stale_sockets_are_replaced_and_missing_instances_report_not_running()
    {
        using var app = sway.Connect();
        string name = Name;
        Assert.Equal(IpcStatus.NotRunning, IpcServer.Send(name, "anything").Status);

        // A socket file nobody listens on, as a crashed shell leaves behind (bound, never listening).
        using var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        dead.Bind(new UnixDomainSocketEndPoint(name));
        Assert.True(File.Exists(name));
        Assert.Equal(IpcStatus.NotRunning, IpcServer.Send(name, "anything").Status);
        using (var server = IpcServer.Listen(app, name, _ => "alive"))
            Assert.Equal("alive", app.Complete(Task.Run(() => IpcServer.Send(name, "x"))).Text);
        Assert.False(File.Exists(name)); // disposed: the socket file is gone
    }

    [Fact]
    public void A_hung_instance_is_not_mistaken_for_a_stale_socket()
    {
        using var app = sway.Connect();
        string name = Name;
        // Listening but never answering, like an instance stuck in a long handler.
        using var hung = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        hung.Bind(new UnixDomainSocketEndPoint(name));
        hung.Listen(1);
        var e = Assert.Throws<InvalidOperationException>(() => IpcServer.Listen(app, name, _ => null));
        Assert.Contains("already", e.Message);
        Assert.True(File.Exists(name));
    }

    [Fact]
    public void Disposing_after_the_runtime_directory_is_gone_does_not_throw()
    {
        // A session ending can remove $XDG_RUNTIME_DIR before the shell has shut down.
        using var app = sway.Connect();
        string dir = Path.Combine(sway.RuntimeDir, $"gone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var server = IpcServer.Listen(app, Path.Combine(dir, "shell.sock"), _ => null);
        Directory.Delete(dir, recursive: true);
        server.Dispose();
    }

    [Fact]
    public void Handler_errors_are_replied_and_slow_clients_do_not_block_the_loop()
    {
        using var app = sway.Connect();
        string name = Name;
        using var server = IpcServer.Listen(app, name, args => args[0] switch
        {
            "fail" => throw new InvalidOperationException("nope"),
            "fake" => "error: just text",
            _ => "ok",
        });
        // A failure is distinguishable from a reply that happens to look like one.
        Assert.Equal(new IpcReply(IpcStatus.Failed, "nope"), app.Complete(Task.Run(() => IpcServer.Send(name, "fail"))));
        Assert.Equal(new IpcReply(IpcStatus.Ok, "error: just text"), app.Complete(Task.Run(() => IpcServer.Send(name, "fake"))));

        // A client that connects and never finishes its request: others are still served meanwhile.
        using var idle = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        idle.Connect(new UnixDomainSocketEndPoint(name));
        idle.Send("half a request"u8.ToArray());
        Assert.Equal(new IpcReply(IpcStatus.Ok, "ok"), app.Complete(Task.Run(() => IpcServer.Send(name, "go"))));
    }
}
