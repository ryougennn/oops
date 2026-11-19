using System.Diagnostics;
using oopshell.Services.DBus;
using oopshell.Services.Polkit;
using Tmds.DBus.Protocol;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class PolkitTests(SwayFixture sway)
{
    /// <summary>polkitd's Authority: records which agents register.</summary>
    sealed class FakeAuthority(OopsApp app) : ExportedObject(app, PolkitAgent.AuthorityPath)
    {
        public readonly List<(string Kind, string Path)> Registered = new();
        public int Unregistered;

        protected override void HandleMethod(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            switch (context.Request.MemberAsString)
            {
                case "RegisterAuthenticationAgent":
                    reader.AlignStruct();
                    string kind = reader.ReadString();
                    reader.ReadDictionaryOfStringToVariantValue();
                    reader.ReadString(); // locale
                    Registered.Add((kind, reader.ReadString()));
                    ReplyEmpty(context);
                    break;
                case "UnregisterAuthenticationAgent":
                    Unregistered++;
                    ReplyEmpty(context);
                    break;
            }
        }
    }

    /// <summary>Stands in for the setuid helper: one password prompt; "correct-horse" with the right cookie succeeds.</summary>
    static string FakeHelper(string dir)
    {
        string path = Path.Combine(dir, "fake-polkit-helper");
        File.WriteAllText(path, """
            #!/bin/sh
            read cookie
            echo "PAM_TEXT_INFO Hello\040$1"
            echo "PAM_PROMPT_ECHO_OFF Password: "
            read answer
            if [ "$cookie" = "cookie-1" ] && [ "$answer" = "correct-horse" ]; then echo SUCCESS; else echo "PAM_ERROR_MSG Wrong\040password"; echo FAILURE; fi
            """);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void Requests_run_the_conversation_and_answer_polkit()
    {
        using var app = sway.Connect();
        using var bus = new DBusDaemon();
        var polkitd = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        var authority = new FakeAuthority(app);
        polkitd.AddMethodHandler(authority);
        app.Complete(polkitd.RequestNameOrThrowAsync(PolkitAgent.AuthorityName).ContinueWith(t => t.Exception is null));

        var shellBus = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        using var dir = new TempDir();
        using var agent = app.Complete(PolkitAgent.StartAsync(app, shellBus, new PolkitAgentOptions { Process = Environment.ProcessId, HelperPath = FakeHelper(dir.Path) }));
        Assert.Equal([("unix-process", "/org/oopshell/PolicyKit1/AuthenticationAgent")], authority.Registered);

        var requests = new List<PolkitRequest>();
        agent.AuthenticationRequested += requests.Add;
        var begin = Begin(polkitd, shellBus.UniqueName!, "cookie-1", CurrentUid());
        app.RunUntil(() => requests.Count == 1, what: "request");
        var request = requests[0];
        Assert.Equal("org.example.reboot", request.ActionId);
        Assert.Equal("Authentication is required to reboot.", request.Message);
        Assert.Equal("/usr/bin/true", request.Details["command_line"]);
        Assert.Equal(Environment.UserName, request.Users[0].Name);

        // A wrong password: the conversation's messages come through (unescaped), and another try is allowed.
        var prompts = new List<PolkitPrompt>();
        bool ok = app.Complete(request.AuthenticateAsync(request.Users[0], prompt =>
        {
            prompts.Add(prompt);
            return prompt.NeedsAnswer ? "wrong" : null;
        }));
        Assert.False(ok);
        Assert.Equal([
            new PolkitPrompt(PolkitPromptKind.Info, $"Hello {Environment.UserName}"),
            new PolkitPrompt(PolkitPromptKind.Secret, "Password: "),
            new PolkitPrompt(PolkitPromptKind.Error, "Wrong password"),
        ], prompts);
        Assert.False(begin.IsCompleted);

        Assert.True(app.Complete(request.AuthenticateAsync(request.Users[0], "correct-horse")));
        Assert.True(request.Succeeded);
        Assert.True(app.Complete(begin.ContinueWith(t => t.Exception is null)));
        Assert.Empty(agent.Pending);

        // polkit cancelling (the application gave up): the request finishes, the call returns.
        var second = Begin(polkitd, shellBus.UniqueName!, "cookie-2", CurrentUid());
        app.RunUntil(() => requests.Count == 2, what: "second request");
        bool finished = false;
        requests[1].Finished += () => finished = true;
        app.Complete(polkitd.CallAsync(shellBus.UniqueName!, "/org/oopshell/PolicyKit1/AuthenticationAgent", PolkitAgent.AgentInterface,
            "CancelAuthentication", (ref MessageWriter w) => w.WriteString("cookie-2"), "s").ContinueWith(t => t.Exception is null));
        Assert.True(finished);
        Assert.False(requests[1].Succeeded);
        Assert.IsType<DBusErrorReplyException>(app.Complete(second.ContinueWith(t => t.Exception?.InnerException)));

        agent.Dispose();
        app.RunUntil(() => authority.Unregistered == 1, what: "unregistered");
    }

    static uint CurrentUid() => uint.Parse(File.ReadAllLines("/proc/self/status").First(l => l.StartsWith("Uid:")).Split('\t', StringSplitOptions.RemoveEmptyEntries)[1]);

    /// <summary>What polkitd sends: BeginAuthentication with one unix-user identity.</summary>
    static Task Begin(DBusConnection polkitd, string agentName, string cookie, uint uid) =>
        polkitd.CallAsync(agentName, "/org/oopshell/PolicyKit1/AuthenticationAgent", PolkitAgent.AgentInterface, "BeginAuthentication", (ref MessageWriter w) =>
        {
            w.WriteString("org.example.reboot");
            w.WriteString("Authentication is required to reboot.");
            w.WriteString("");
            var details = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString("command_line");
            w.WriteString("/usr/bin/true");
            w.WriteDictionaryEnd(details);
            w.WriteString(cookie);
            var identities = w.WriteArrayStart(DBusType.Struct);
            w.WriteStructureStart();
            w.WriteString("unix-user");
            w.WriteDictionary(new Dictionary<string, VariantValue> { ["uid"] = uid });
            w.WriteArrayEnd(identities);
        }, "sssa{ss}sa(sa{sv})");

    [Fact]
    public void Real_polkit_asks_the_agent_for_a_process()
    {
        // Against the system's polkitd: an agent for one process only (as pkttyagent --process), so the desktop's
        // own agent is untouched. Nothing is authenticated: the request is cancelled.
        Assert.SkipUnless(File.Exists("/usr/bin/pkcheck") && Directory.Exists("/run/dbus"), "polkit is not installed.");
        using var app = sway.Connect();
        DBusConnection system;
        try
        {
            system = app.Complete(OopsDBus.ConnectAsync(app, DBusAddress.System));
        }
        catch (Exception e) when (e is DBusConnectionException or IOException)
        {
            Assert.Skip("No system bus.");
            return;
        }
        using var target = Process.Start(new ProcessStartInfo("sleep", ["30"]))!;
        try
        {
            using var agent = app.Complete(PolkitAgent.StartAsync(app, system, new PolkitAgentOptions { Process = target.Id }));
            var requests = new List<PolkitRequest>();
            agent.AuthenticationRequested += requests.Add;
            using var check = Process.Start(new ProcessStartInfo("pkcheck",
                ["--action-id", "org.freedesktop.policykit.exec", "--process", target.Id.ToString(), "--allow-user-interaction"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            app.RunUntil(() => requests.Count == 1 || check.HasExited, 10000, "polkit request");
            Assert.Single(requests);
            Assert.Equal("org.freedesktop.policykit.exec", requests[0].ActionId);
            Assert.NotEmpty(requests[0].Users);
            requests[0].Cancel();
            app.RunUntil(() => check.HasExited, 10000, "pkcheck finished");
            Assert.NotEqual(0, check.ExitCode); // not authorized
        }
        finally
        {
            target.Kill();
        }
    }
}

sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("oopstest.").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
