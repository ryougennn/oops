using oopshell.Native;
using System.Net.Sockets;
using System.Text;

namespace oopshell;

/// <summary>
/// A single-instance command channel, so compositor key bindings and scripts can reach a running shell:
/// <code>
/// // in the shell
/// using var ipc = IpcServer.Listen(app, "myshell", args => args[0] switch { "toggle" => Toggle(), _ => "unknown" });
/// // in `myshell toggle` (another process)
/// var reply = IpcServer.Send("myshell", "toggle");
/// if (reply.Status == IpcStatus.NotRunning) …            // reply.Text: the answer, or the error message
/// </code>
/// The socket is <c>$XDG_RUNTIME_DIR/&lt;name&gt;.sock</c> (or the name itself if it is an absolute
/// path). Requests are served on the UI thread from the event loop, without threads or blocking reads.
/// </summary>
public enum IpcStatus
{
    /// <summary>Nothing is serving the name.</summary>
    NotRunning,
    /// <summary>The handler ran; <see cref="IpcReply.Text"/> is its reply ("" for none).</summary>
    Ok,
    /// <summary>The handler threw; <see cref="IpcReply.Text"/> is the exception's message.</summary>
    Failed,
}

/// <summary>The outcome of <see cref="IpcServer.Send"/>.</summary>
public readonly record struct IpcReply(IpcStatus Status, string Text)
{
    public bool IsOk => Status == IpcStatus.Ok;
}

public sealed class IpcServer : IDisposable
{
    const int MaxRequest = 64 * 1024;
    const int MaxReply = 1024 * 1024;
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

    /// <summary>
    /// <c>$XDG_RUNTIME_DIR/&lt;name&gt;.sock</c>. Without XDG_RUNTIME_DIR, a private 0700 directory of this user
    /// in /tmp (never a shared path another user could take first).
    /// </summary>
    public static string PathFor(string name) =>
        Path.IsPathRooted(name) ? name : Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } dir ? dir : PrivateDir(), name + ".sock");

    static string PrivateDir()
    {
        string dir = $"/tmp/oopshell-{LibC.geteuid()}";
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var info = new DirectoryInfo(dir);
        if (info.LinkTarget is not null || info.UnixFileMode != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
            throw new InvalidOperationException($"{dir} is not a private directory; set XDG_RUNTIME_DIR.");
        return dir;
    }

    /// <summary>The uid of the process at the other end of a connected Unix socket (SO_PEERCRED).</summary>
    static uint PeerUid(Socket socket)
    {
        Span<byte> cred = stackalloc byte[12]; // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
        socket.GetRawSocketOption(1 /* SOL_SOCKET */, 17 /* SO_PEERCRED */, cred);
        return BitConverter.ToUInt32(cred[4..8]);
    }

    /// <summary>
    /// Starts serving <paramref name="name"/>. <paramref name="handler"/> gets the arguments a client sent and
    /// returns the reply (null for none); to fail a request, throw (the client gets <see cref="IpcStatus.Failed"/>
    /// and the message). Throws <see cref="InvalidOperationException"/> if another instance is
    /// already serving it; a stale socket file left by a crash is replaced.
    /// </summary>
    public static IpcServer Listen(OopsApp app, string name, Func<IReadOnlyList<string>, string?> handler)
    {
        string path = PathFor(name);
        if (File.Exists(path))
        {
            // Only a socket nobody listens on is stale; one that accepts (even if busy or hung) is another instance.
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                probe.Connect(new UnixDomainSocketEndPoint(path));
                throw new InvalidOperationException($"Another instance is already serving {path}.");
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable)
            {
                File.Delete(path);
            }
        }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
        listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(16);
        return new IpcServer(app, listener, path, handler);
    }

    /// <summary>True if an instance is serving <paramref name="name"/>.</summary>
    public static bool IsRunning(string name) => Send(name).Status != IpcStatus.NotRunning;

    /// <summary>
    /// Sends <paramref name="args"/> to the instance serving <paramref name="name"/>: its reply, the error its
    /// handler threw, or <see cref="IpcStatus.NotRunning"/>. Blocks for at most two seconds. With no
    /// arguments it only checks that the instance answers.
    /// </summary>
    public static IpcReply Send(string name, params string[] args)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                SendTimeout = (int)ClientTimeout.TotalMilliseconds,
                ReceiveTimeout = (int)ClientTimeout.TotalMilliseconds,
            };
            socket.Connect(new UnixDomainSocketEndPoint(PathFor(name)));
            if (PeerUid(socket) != LibC.geteuid())
                return new IpcReply(IpcStatus.NotRunning, ""); // not ours: send it nothing
            socket.Send(Encoding.UTF8.GetBytes(string.Join('\0', args)));
            socket.Shutdown(SocketShutdown.Send);
            using var reply = new MemoryStream();
            var buffer = new byte[4096];
            int n;
            while ((n = socket.Receive(buffer)) > 0)
            {
                reply.Write(buffer, 0, n);
                if (reply.Length > MaxReply)
                    return new IpcReply(IpcStatus.Failed, "The reply was too long.");
            }
            // The first byte is the status: '+' answered, '!' the handler failed.
            var bytes = reply.GetBuffer().AsSpan(0, (int)reply.Length);
            if (bytes.Length == 0)
                return new IpcReply(IpcStatus.NotRunning, ""); // accepted, then closed without an answer: not a live instance
            var status = bytes[0] == (byte)'!' ? IpcStatus.Failed : IpcStatus.Ok;
            return new IpcReply(status, Encoding.UTF8.GetString(bytes[1..]));
        }
        catch (SocketException)
        {
            return new IpcReply(IpcStatus.NotRunning, "");
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
            if (PeerUid(socket) != LibC.geteuid())
            {
                socket.Dispose(); // another user: the runtime directory should have kept them out already
                continue;
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
        string reply = "+";
        if (text.Length > 0)
        {
            try
            {
                reply = "+" + handler(text.Split('\0'));
            }
            catch (Exception e)
            {
                reply = "!" + e.Message;
            }
        }
        try
        {
            client.Socket.Blocking = true;
            client.Socket.SendTimeout = (int)ClientTimeout.TotalMilliseconds;
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
        try
        {
            File.Delete(SocketPath);
        }
        catch (IOException)
        {
            // The runtime directory is already gone (the session ended first).
        }
    }
}
