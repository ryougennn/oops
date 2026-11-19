using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using oopshell.Services.Greetd;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class GreetdTests(SwayFixture sway)
{
    /// <summary>greetd's side: answers like greetd with PAM asking for a password ("correct-horse" is right).</summary>
    private sealed class FakeGreetd : IDisposable
    {
        private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly Thread _thread;
        public readonly List<string> Received = new();
        public string Path { get; }

        public FakeGreetd(string dir)
        {
            Path = System.IO.Path.Combine(dir, "greetd.sock");
            _listener.Bind(new UnixDomainSocketEndPoint(Path));
            _listener.Listen(1);
            _thread = new Thread(Serve) { IsBackground = true };
            _thread.Start();
        }

        private void Serve()
        {
            try
            {
                using var client = _listener.Accept();
                string? user = null;
                while (Read(client) is { } request)
                {
                    lock (Received)
                        Received.Add(request.RootElement.GetRawText());
                    var root = request.RootElement;
                    string type = root.GetProperty("type").GetString()!;
                    string reply = type switch
                    {
                        "create_session" => (user = root.GetProperty("username").GetString()) == "nobody"
                            ? """{"type":"error","error_type":"error","description":"unknown user"}"""
                            : """{"type":"auth_message","auth_message_type":"info","auth_message":"Welcome"}""",
                        "post_auth_message_response" when root.GetProperty("response").ValueKind == JsonValueKind.Null =>
                            """{"type":"auth_message","auth_message_type":"secret","auth_message":"Password: "}""",
                        "post_auth_message_response" => root.GetProperty("response").GetString() == "correct-horse"
                            ? """{"type":"success"}"""
                            : """{"type":"error","error_type":"auth_error","description":"Authentication failure"}""",
                        _ => """{"type":"success"}""",
                    };
                    Write(client, reply);
                }
            }
            catch (SocketException)
            {
            }
        }

        private static JsonDocument? Read(Socket s)
        {
            var header = new byte[4];
            if (!Fill(s, header))
                return null;
            var body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
            return Fill(s, body) ? JsonDocument.Parse(body) : null;
        }

        private static bool Fill(Socket s, byte[] buffer)
        {
            for (int read = 0; read < buffer.Length;)
            {
                int n = s.Receive(buffer, read, buffer.Length - read, SocketFlags.None);
                if (n == 0)
                    return false;
                read += n;
            }
            return true;
        }

        private static void Write(Socket s, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[4 + body.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
            body.CopyTo(frame, 4);
            s.Send(frame);
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public void A_greeter_logs_in_retries_and_starts_the_session()
    {
        using var app = sway.Connect();
        using var dir = new TempDir();
        using var greetd = new FakeGreetd(dir.Path);
        using var client = app.Complete(GreetdClient.ConnectAsync(greetd.Path));

        var prompts = new List<GreetdPrompt>();
        var wrong = app.Complete(client.LoginAsync("alice", p =>
        {
            prompts.Add(p);
            return p.NeedsAnswer ? "wrong" : null;
        }));
        Assert.Equal(new GreetdLoginResult(GreetdLoginStatus.AuthenticationFailed, "Authentication failure"), wrong);
        Assert.Equal([new GreetdPrompt(GreetdPromptKind.Info, "Welcome"), new GreetdPrompt(GreetdPromptKind.Secret, "Password: ")], prompts);

        Assert.True(app.Complete(client.LoginAsync("alice", "correct-horse")).Succeeded);
        app.Complete(client.StartSessionAsync(["niri-session"], ["XDG_SESSION_TYPE=wayland"]).ContinueWith(t => t.Exception is null));

        var unknown = app.Complete(client.LoginAsync("nobody", "x"));
        Assert.Equal(GreetdLoginStatus.Error, unknown.Status);

        string[] sent;
        lock (greetd.Received)
            sent = greetd.Received.ToArray();
        Assert.Equal([
            """{"type":"create_session","username":"alice"}""",
            """{"type":"post_auth_message_response","response":null}""",
            """{"type":"post_auth_message_response","response":"wrong"}""",
            """{"type":"cancel_session"}""", // a failed attempt is cancelled so the next starts fresh
            """{"type":"create_session","username":"alice"}""",
            """{"type":"post_auth_message_response","response":null}""",
            """{"type":"post_auth_message_response","response":"correct-horse"}""",
            """{"type":"start_session","cmd":["niri-session"],"env":["XDG_SESSION_TYPE=wayland"]}""",
            """{"type":"create_session","username":"nobody"}""",
            """{"type":"cancel_session"}""",
        ], sent);
    }
}
