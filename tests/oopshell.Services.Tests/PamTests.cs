using oopshell.Services.Pam;

namespace oopshell.Services.Tests;

/// <summary>
/// Runs real PAM against services in a private config directory (pam_start_confdir): pam_exec checks the
/// password it gets through our conversation, so nothing depends on the user's real password.
/// </summary>
public sealed class PamTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oopspam.").FullName;
    private readonly string _user = Environment.UserName;

    public PamTests()
    {
        string check = Path.Combine(_dir, "check.sh");
        File.WriteAllText(check, "#!/bin/sh\npw=$(tr -d '\\000')\n[ \"$pw\" = \"s3cret pässword\" ]\n");
        File.SetUnixFileMode(check, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(_dir, "oopstest"),
            // pam_exec reports a failed check as a system error; like pam_unix, a wrong password should be an auth error.
            $"auth required pam_echo.so Touch the sensor\nauth [success=1 default=ignore] pam_exec.so expose_authtok quiet {check}\nauth requisite pam_deny.so\nauth required pam_permit.so\naccount required pam_permit.so\n");
        File.WriteAllText(Path.Combine(_dir, "oopslocked"),
            $"auth required pam_exec.so expose_authtok quiet {check}\naccount required pam_deny.so\n");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task The_right_password_succeeds_and_a_wrong_one_fails()
    {
        Assert.Equal(PamResult.Success, await PamAuthenticator.AuthenticateAsync(_user, "s3cret pässword", "oopstest", _dir));
        Assert.Equal(PamResult.AuthenticationFailed, await PamAuthenticator.AuthenticateAsync(_user, "wrong", "oopstest", _dir));
    }

    [Fact]
    public async Task The_conversation_sees_information_and_prompts()
    {
        var seen = new List<PamMessage>();
        var result = await PamAuthenticator.AuthenticateAsync(_user, m =>
        {
            seen.Add(m);
            return m.IsPrompt ? "s3cret pässword" : null;
        }, "oopstest", _dir);
        Assert.Equal(PamResult.Success, result);
        Assert.Contains(seen, m => m.Style == PamMessageStyle.TextInfo && m.Text == "Touch the sensor");
        Assert.Contains(seen, m => m.Style == PamMessageStyle.PromptEchoOff);
    }

    [Fact]
    public async Task Refused_accounts_aborts_and_missing_services_are_told_apart()
    {
        Assert.Equal(PamResult.AccountUnavailable, await PamAuthenticator.AuthenticateAsync(_user, "s3cret pässword", "oopslocked", _dir));
        Assert.Equal(PamResult.Aborted, await PamAuthenticator.AuthenticateAsync(_user, m => m.IsPrompt ? null : "", "oopstest", _dir));
        Assert.NotEqual(PamResult.Success, await PamAuthenticator.AuthenticateAsync(_user, "s3cret pässword", "no-such-service", _dir));
    }
}
