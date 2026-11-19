using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace oopshell.Services.Greetd;

public enum GreetdPromptKind
{
    /// <summary>Ask for something to show while typing (e.g. a user name or OTP).</summary>
    Visible,
    /// <summary>Ask for a secret (the password).</summary>
    Secret,
    /// <summary>Show information; no answer.</summary>
    Info,
    /// <summary>Show an error; no answer.</summary>
    Error,
}

/// <summary>A message of the login conversation (from PAM, through greetd).</summary>
public sealed record GreetdPrompt(GreetdPromptKind Kind, string Text)
{
    public bool NeedsAnswer => Kind is GreetdPromptKind.Visible or GreetdPromptKind.Secret;
}

public enum GreetdLoginStatus
{
    /// <summary>Authenticated: call <see cref="GreetdClient.StartSessionAsync"/>.</summary>
    Success,
    /// <summary>Wrong credentials (or the user cancelled); try again.</summary>
    AuthenticationFailed,
    /// <summary>Anything else greetd refused (unknown user, PAM misconfiguration…).</summary>
    Error,
}

public sealed record GreetdLoginResult(GreetdLoginStatus Status, string? Description = null)
{
    public bool Succeeded => Status == GreetdLoginStatus.Success;
}

/// <summary>
/// A greetd client, for a greeter (a login screen) run by greetd. Log in, then start the session; greetd
/// starts it once the greeter exits.
/// <code>
/// using var greetd = await GreetdClient.ConnectAsync();
/// var result = await greetd.LoginAsync("alice", prompt => prompt.Kind == GreetdPromptKind.Secret ? password : null);
/// if (result.Succeeded) { await greetd.StartSessionAsync(["niri-session"]); app.Quit(); }
/// </code>
/// Call it from the UI thread; the conversation callback then runs there too.
/// </summary>
public sealed class GreetdClient : IDisposable
{
    private readonly Socket _socket;
    private bool _sessionOpen;

    private GreetdClient(Socket socket) => _socket = socket;

    /// <summary>Connects to greetd at <paramref name="path"/> (default <c>$GREETD_SOCK</c>, which greetd sets for its greeter).</summary>
    public static async Task<GreetdClient> ConnectAsync(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("GREETD_SOCK")
            ?? throw new InvalidOperationException("GREETD_SOCK is not set: not running as a greetd greeter.");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new GreetdClient(socket);
    }

    /// <summary>
    /// Authenticates <paramref name="user"/>: <paramref name="respond"/> answers each prompt (null for messages).
    /// After a failure the attempt is cancelled, so the next call starts fresh.
    /// </summary>
    public async Task<GreetdLoginResult> LoginAsync(string user, Func<GreetdPrompt, string?> respond)
    {
        if (_sessionOpen)
            await CancelAsync();
        var reply = await RequestAsync(w =>
        {
            w.WriteString("type", "create_session");
            w.WriteString("username", user);
        });
        _sessionOpen = true;
        while (true)
        {
            string type, text;
            bool authError;
            GreetdPromptKind kind;
            using (reply)
            {
                var root = reply.RootElement;
                type = root.GetProperty("type").GetString() ?? "";
                text = root.TryGetProperty("description", out var d) ? d.GetString() ?? ""
                    : root.TryGetProperty("auth_message", out var m) ? m.GetString() ?? "" : root.GetRawText();
                authError = root.TryGetProperty("error_type", out var t) && t.GetString() == "auth_error";
                kind = (root.TryGetProperty("auth_message_type", out var k) ? k.GetString() : null) switch
                {
                    "visible" => GreetdPromptKind.Visible,
                    "secret" => GreetdPromptKind.Secret,
                    "error" => GreetdPromptKind.Error,
                    _ => GreetdPromptKind.Info,
                };
            }
            switch (type)
            {
                case "success":
                    return new GreetdLoginResult(GreetdLoginStatus.Success);
                case "error":
                    await CancelAsync();
                    return new GreetdLoginResult(authError ? GreetdLoginStatus.AuthenticationFailed : GreetdLoginStatus.Error, text);
                case "auth_message":
                    var prompt = new GreetdPrompt(kind, text);
                    string? answer = respond(prompt);
                    reply = await RequestAsync(w =>
                    {
                        w.WriteString("type", "post_auth_message_response");
                        if (prompt.NeedsAnswer)
                            w.WriteString("response", answer ?? "");
                        else
                            w.WriteNull("response");
                    });
                    continue;
                default:
                    throw new InvalidDataException($"Unexpected greetd reply: {text}");
            }
        }
    }

    /// <summary>Password-only convenience over <see cref="LoginAsync(string, Func{GreetdPrompt, string?})"/>.</summary>
    public Task<GreetdLoginResult> LoginAsync(string user, string password) =>
        LoginAsync(user, prompt => prompt.Kind == GreetdPromptKind.Secret ? password : prompt.NeedsAnswer ? "" : null);

    /// <summary>
    /// After a successful login: the session to start (e.g. <c>["niri-session"]</c>) and extra environment
    /// (<c>NAME=value</c>). greetd starts it when this greeter exits, so quit next.
    /// </summary>
    public async Task StartSessionAsync(IReadOnlyList<string> command, IReadOnlyList<string>? environment = null)
    {
        using var reply = await RequestAsync(w =>
        {
            w.WriteString("type", "start_session");
            w.WriteStartArray("cmd");
            foreach (var part in command)
                w.WriteStringValue(part);
            w.WriteEndArray();
            w.WriteStartArray("env");
            foreach (var variable in environment ?? [])
                w.WriteStringValue(variable);
            w.WriteEndArray();
        });
        var root = reply.RootElement;
        if (root.GetProperty("type").GetString() != "success")
            throw new InvalidOperationException($"greetd refused to start the session: {(root.TryGetProperty("description", out var d) ? d.GetString() : root.GetRawText())}");
        _sessionOpen = false;
    }

    /// <summary>Abandons the login in progress (e.g. the user picked someone else).</summary>
    public async Task CancelAsync()
    {
        if (!_sessionOpen)
            return;
        _sessionOpen = false;
        using var _ = await RequestAsync(w => w.WriteString("type", "cancel_session"));
    }

    private const int MaxReply = 1 << 20;

    /// <summary>One request and its reply: a native-endian u32 length, then JSON, each way.</summary>
    private async Task<JsonDocument> RequestAsync(Action<Utf8JsonWriter> write)
    {
        using var body = new MemoryStream();
        using (var json = new Utf8JsonWriter(body))
        {
            json.WriteStartObject();
            write(json);
            json.WriteEndObject();
        }
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length); // greetd: native endianness; x86/ARM Linux are little-endian
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(frame.AsSpan(4));
        await _socket.SendAsync(frame);
        var header = new byte[4];
        await ReceiveExactlyAsync(header);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxReply)
            throw new InvalidDataException($"greetd sent a {length}-byte reply; at most {MaxReply} are accepted.");
        var payload = new byte[length];
        await ReceiveExactlyAsync(payload);
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("greetd sent a malformed reply.", e);
        }
    }

    private async Task ReceiveExactlyAsync(byte[] buffer)
    {
        for (int read = 0; read < buffer.Length;)
        {
            int n = await _socket.ReceiveAsync(buffer.AsMemory(read));
            if (n == 0)
                throw new IOException("greetd closed the connection.");
            read += n;
        }
    }

    public void Dispose() => _socket.Dispose();
}
