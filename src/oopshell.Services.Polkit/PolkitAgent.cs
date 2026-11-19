using System.Diagnostics;
using System.Text;
using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.Services.Polkit;

public sealed record PolkitAgentOptions
{
    /// <summary>
    /// Serve only this process's requests (a polkit <c>unix-process</c> subject, as <c>pkttyagent --process</c>
    /// does). Default: the whole login session (<c>$XDG_SESSION_ID</c>, else logind's session of this process).
    /// </summary>
    public int? Process { get; init; }

    /// <summary>polkit's setuid helper that checks the password (default: found in the usual places).</summary>
    public string? HelperPath { get; init; }

    /// <summary>Where the agent object is exported.</summary>
    public string ObjectPath { get; init; } = "/org/oopshell/PolicyKit1/AuthenticationAgent";
}

/// <summary>
/// A polkit authentication agent: when an application asks for privileges ("Authentication is required to
/// change the system time"), <see cref="AuthenticationRequested"/> fires on the UI thread and the shell shows
/// a dialog. Only one agent can serve a session, so this fails if the desktop already runs one.
/// <code>
/// var bus = await OopsDBus.ConnectAsync(app, DBusAddress.System);
/// using var agent = await PolkitAgent.StartAsync(app, bus);
/// agent.AuthenticationRequested += request =>
///     dialog.Show(request.Message, request.Users, password => request.AuthenticateAsync(request.Users[0], password));
/// </code>
/// </summary>
public sealed class PolkitAgent : ExportedObject, IDisposable
{
    public const string AuthorityName = "org.freedesktop.PolicyKit1";
    public const string AuthorityPath = "/org/freedesktop/PolicyKit1/Authority";
    public const string AuthorityInterface = "org.freedesktop.PolicyKit1.Authority";
    public const string AgentInterface = "org.freedesktop.PolicyKit1.AuthenticationAgent";

    static readonly string[] HelperPaths =
    [
        "/usr/lib/polkit-1/polkit-agent-helper-1", "/usr/libexec/polkit-agent-helper-1",
        "/usr/lib/polkit-agent-helper-1", "/usr/libexec/polkit-1/polkit-agent-helper-1",
    ];

    readonly DBusConnection connection;
    readonly PolkitAgentOptions options;
    readonly Dictionary<string, PolkitRequest> requests = new(); // by cookie
    (string Kind, Dictionary<string, VariantValue> Details) subject;
    string? polkitd;
    bool disposed;

    PolkitAgent(OopsApp app, DBusConnection connection, PolkitAgentOptions options) : base(app, options.ObjectPath)
    {
        this.connection = connection;
        this.options = options;
    }

    /// <summary>
    /// Registers with polkit on <paramref name="systemBus"/>. Throws <see cref="DBusErrorReplyException"/> if
    /// another agent already serves the subject (on a full desktop, its own agent usually does).
    /// </summary>
    public static async Task<PolkitAgent> StartAsync(OopsApp app, DBusConnection systemBus, PolkitAgentOptions? options = null)
    {
        var agent = new PolkitAgent(app, systemBus, options ?? new PolkitAgentOptions());
        agent.subject = await agent.SubjectAsync();
        agent.polkitd = await systemBus.GetNameOwnerAsync(AuthorityName);
        systemBus.AddMethodHandler(agent);
        try
        {
            string locale = Environment.GetEnvironmentVariable("LC_ALL") ?? Environment.GetEnvironmentVariable("LANG") ?? "C";
            await systemBus.CallAsync(AuthorityName, AuthorityPath, AuthorityInterface, "RegisterAuthenticationAgent", (ref MessageWriter w) =>
            {
                agent.WriteSubject(ref w);
                w.WriteString(locale);
                w.WriteString(agent.Path); // polkit takes the path as a string
            }, "(sa{sv})ss");
        }
        catch
        {
            systemBus.RemoveMethodHandler(agent.Path);
            throw;
        }
        return agent;
    }

    /// <summary>An application needs the user to authenticate (UI thread).</summary>
    public event Action<PolkitRequest>? AuthenticationRequested;

    /// <summary>Requests not finished yet.</summary>
    public IReadOnlyCollection<PolkitRequest> Pending => requests.Values;

    internal string HelperPath =>
        options.HelperPath ?? HelperPaths.FirstOrDefault(File.Exists)
        ?? throw new InvalidOperationException("polkit-agent-helper-1 was not found (is polkit installed?).");

    async Task<(string, Dictionary<string, VariantValue>)> SubjectAsync()
    {
        if (options.Process is int pid)
        {
            // start-time guards against the pid being reused: field 22 of /proc/pid/stat, after the ")" of the name.
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            ulong start = ulong.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19]);
            return ("unix-process", new() { ["pid"] = (uint)pid, ["start-time"] = (VariantValue)start });
        }
        string? session = Environment.GetEnvironmentVariable("XDG_SESSION_ID");
        if (string.IsNullOrEmpty(session))
        {
            string path = await connection.CallAsync("org.freedesktop.login1", "/org/freedesktop/login1", "org.freedesktop.login1.Manager",
                "GetSessionByPID", (m, _) => m.GetBodyReader().ReadObjectPathAsString(), (ref MessageWriter w) => w.WriteUInt32((uint)Environment.ProcessId), "u");
            session = (await connection.GetPropertyAsync("org.freedesktop.login1", path, "org.freedesktop.login1.Session", "Id")).GetString();
        }
        return ("unix-session", new() { ["session-id"] = session });
    }

    void WriteSubject(ref MessageWriter w)
    {
        w.WriteStructureStart();
        w.WriteString(subject.Kind);
        w.WriteDictionary(subject.Details);
    }

    protected override void HandleMethod(MethodContext context)
    {
        if (context.Request.InterfaceAsString != AgentInterface || RefuseUnlessFrom(context, polkitd))
            return;
        switch (context.Request.MemberAsString)
        {
            case "BeginAuthentication":
                Begin(context);
                break;
            case "CancelAuthentication":
                string cookie = context.Request.GetBodyReader().ReadString();
                if (requests.TryGetValue(cookie, out var request))
                    request.Finish(cancelledByPolkit: true);
                ReplyEmpty(context);
                break;
        }
    }

    void Begin(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        string actionId = reader.ReadString(), message = reader.ReadString(), icon = reader.ReadString();
        var details = new Dictionary<string, string>();
        var detailsEnd = reader.ReadDictionaryStart();
        while (reader.HasNext(detailsEnd))
            details[reader.ReadString()] = reader.ReadString();
        string cookie = reader.ReadString();
        var users = new List<PolkitUser>();
        var identitiesEnd = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(identitiesEnd))
        {
            reader.AlignStruct();
            string kind = reader.ReadString();
            var properties = reader.ReadDictionaryOfStringToVariantValue();
            if (kind == "unix-user" && properties.TryGetValue("uid", out var uid) && Accounts.ByUid(uid.GetUInt32()) is { } user)
                users.Add(user);
            else if (kind == "unix-group" && properties.TryGetValue("gid", out var gid))
                users.AddRange(Accounts.Members(gid.GetUInt32()));
        }
        // The person at the keyboard first, if they may answer.
        users = users.DistinctBy(u => u.Uid).OrderBy(u => u.Name == Environment.UserName ? 0 : 1).ToList();

        Defer(context);
        if (requests.TryGetValue(cookie, out var previous))
            previous.Finish(cancelledByPolkit: true); // polkitd started over with the same cookie: answer the old call
        var request = new PolkitRequest(this, context, actionId, message, icon, details, cookie, users);
        requests[cookie] = request;
        if (users.Count == 0)
        {
            request.Finish(cancelledByPolkit: false, error: "No user may authenticate this action.");
            return;
        }
        AuthenticationRequested?.Invoke(request);
    }

    internal void Finished(PolkitRequest request, MethodContext context, string? error)
    {
        requests.Remove(request.Cookie);
        if (error is null)
            ReplyDeferred(context);
        else
            ReplyDeferredError(context, "org.freedesktop.PolicyKit1.Error.Cancelled", error);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        foreach (var request in requests.Values.ToList())
            request.Cancel();
        connection.RemoveMethodHandler(Path);
        // Best effort: polkit also forgets the agent when this connection closes.
        _ = connection.CallAsync(AuthorityName, AuthorityPath, AuthorityInterface, "UnregisterAuthenticationAgent", (ref MessageWriter w) =>
        {
            WriteSubject(ref w);
            w.WriteString(Path);
        }, "(sa{sv})s").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }
}

public enum PolkitPromptKind
{
    /// <summary>A secret (the password): don't show what is typed.</summary>
    Secret,
    /// <summary>A visible answer (e.g. a user name).</summary>
    Text,
    /// <summary>An error to show; no answer.</summary>
    Error,
    /// <summary>Information to show (e.g. "Place your finger on the reader"); no answer.</summary>
    Info,
}

/// <summary>One message of the authentication conversation (PAM's, relayed by polkit's helper).</summary>
public sealed record PolkitPrompt(PolkitPromptKind Kind, string Text)
{
    public bool NeedsAnswer => Kind is PolkitPromptKind.Secret or PolkitPromptKind.Text;
}

/// <summary>
/// One authentication request. Show <see cref="Message"/>, let the user pick one of <see cref="Users"/> and
/// answer; call <see cref="AuthenticateAsync(PolkitUser, string)"/> (again after a wrong password), or
/// <see cref="Cancel"/> when the user gives up. <see cref="Finished"/> fires either way, including when the
/// application stops waiting.
/// </summary>
public sealed class PolkitRequest
{
    readonly PolkitAgent agent;
    readonly MethodContext context;
    Process? helper;

    internal PolkitRequest(PolkitAgent agent, MethodContext context, string actionId, string message, string iconName,
        IReadOnlyDictionary<string, string> details, string cookie, IReadOnlyList<PolkitUser> users)
    {
        this.agent = agent;
        this.context = context;
        ActionId = actionId;
        Message = message;
        IconName = iconName;
        Details = details;
        Cookie = cookie;
        Users = users;
    }

    /// <summary>The polkit action, e.g. <c>org.freedesktop.timedate1.set-time</c>.</summary>
    public string ActionId { get; }
    /// <summary>What to tell the user ("Authentication is required to …"), already translated.</summary>
    public string Message { get; }
    /// <summary>An icon name for the dialog; often empty.</summary>
    public string IconName { get; }
    /// <summary>Extra information from the application (e.g. <c>polkit.subject-pid</c>, <c>command_line</c>).</summary>
    public IReadOnlyDictionary<string, string> Details { get; }
    /// <summary>Who may authenticate, the current user first when allowed (groups are expanded to their members).</summary>
    public IReadOnlyList<PolkitUser> Users { get; }
    internal string Cookie { get; }

    public bool IsFinished { get; private set; }

    /// <summary>True when finished because the user authenticated.</summary>
    public bool Succeeded { get; private set; }

    /// <summary>The request ended: authenticated, cancelled, or abandoned by the application (UI thread).</summary>
    public event Action? Finished;

    /// <summary>Checks <paramref name="password"/> for <paramref name="user"/>. True finishes the request; false allows another try.</summary>
    public async Task<bool> AuthenticateAsync(PolkitUser user, string password)
    {
        bool answered = false;
        return await AuthenticateAsync(user, prompt =>
        {
            if (prompt.Kind != PolkitPromptKind.Secret || answered)
                return prompt.NeedsAnswer ? "" : null;
            answered = true;
            return password;
        });
    }

    /// <summary>
    /// Runs the whole conversation (password, OTP, fingerprint messages…): <paramref name="respond"/> is called on
    /// the UI thread for each prompt and returns the answer (or null for messages). True finishes the request.
    /// </summary>
    public async Task<bool> AuthenticateAsync(PolkitUser user, Func<PolkitPrompt, string?> respond)
    {
        if (IsFinished)
            throw new InvalidOperationException("The request has finished.");
        if (helper is not null)
            throw new InvalidOperationException("An authentication attempt is already running.");
        var start = new ProcessStartInfo(agent.HelperPath, [user.Name])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start polkit's helper.");
        helper = process;
        try
        {
            await process.StandardInput.WriteLineAsync(Cookie);
            await process.StandardInput.FlushAsync();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line == "SUCCESS")
                {
                    Finish(cancelledByPolkit: false, succeeded: true);
                    return true;
                }
                if (line.StartsWith("FAILURE", StringComparison.Ordinal))
                    return false;
                if (Parse(line) is not { } prompt)
                    continue;
                string? answer = respond(prompt);
                if (prompt.NeedsAnswer)
                {
                    await process.StandardInput.WriteLineAsync(answer ?? "");
                    await process.StandardInput.FlushAsync();
                }
            }
            return false; // the helper ended without a verdict
        }
        catch (IOException)
        {
            return false; // the helper went away (e.g. cancelled)
        }
        finally
        {
            helper = null;
            if (!process.HasExited)
                process.Kill();
        }
    }

    static PolkitPrompt? Parse(string line)
    {
        (string Prefix, PolkitPromptKind Kind)[] kinds =
        [
            ("PAM_PROMPT_ECHO_OFF ", PolkitPromptKind.Secret), ("PAM_PROMPT_ECHO_ON ", PolkitPromptKind.Text),
            ("PAM_ERROR_MSG ", PolkitPromptKind.Error), ("PAM_TEXT_INFO ", PolkitPromptKind.Info),
        ];
        foreach (var (prefix, kind) in kinds)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return new PolkitPrompt(kind, Unescape(line[prefix.Length..]));
        }
        return null;
    }

    /// <summary>The helper escapes messages C-style (g_strescape): \n, \t, \\, \", octal.</summary>
    internal static string Unescape(string s)
    {
        if (!s.Contains('\\'))
            return s;
        var bytes = new List<byte>();
        var utf8 = Encoding.UTF8.GetBytes(s);
        for (int i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] != '\\' || i + 1 >= utf8.Length)
            {
                bytes.Add(utf8[i]);
                continue;
            }
            byte c = utf8[++i];
            if (c is >= (byte)'0' and <= (byte)'7')
            {
                int value = 0, digits = 0;
                for (; digits < 3 && i < utf8.Length && utf8[i] is >= (byte)'0' and <= (byte)'7'; digits++, i++)
                    value = value * 8 + (utf8[i] - '0');
                i--;
                bytes.Add((byte)value);
                continue;
            }
            bytes.Add(c switch { (byte)'n' => (byte)'\n', (byte)'t' => (byte)'\t', (byte)'r' => (byte)'\r', (byte)'b' => (byte)'\b', (byte)'f' => (byte)'\f', (byte)'v' => (byte)'\v', _ => c });
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>The user gave up: polkit tells the application it was not authorized.</summary>
    public void Cancel() => Finish(cancelledByPolkit: false, error: "Cancelled by the user.");

    internal void Finish(bool cancelledByPolkit, bool succeeded = false, string? error = null)
    {
        if (IsFinished)
            return;
        IsFinished = true;
        Succeeded = succeeded;
        if (helper is { HasExited: false } running)
            running.Kill();
        agent.Finished(this, context, succeeded ? null : error ?? (cancelledByPolkit ? "Cancelled by polkit." : "Not authenticated."));
        Finished?.Invoke();
    }
}
