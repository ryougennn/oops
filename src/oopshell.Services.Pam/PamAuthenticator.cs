using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace oopshell.Services.Pam;

public enum PamMessageStyle
{
    /// <summary>A secret prompt (a password): answer it, it is not echoed.</summary>
    PromptEchoOff = 1,
    /// <summary>A visible prompt (a user name, a one-time code): answer it.</summary>
    PromptEchoOn = 2,
    /// <summary>An error to show; the answer is ignored.</summary>
    ErrorMessage = 3,
    /// <summary>Information to show ("Place your finger…"); the answer is ignored.</summary>
    TextInfo = 4,
}

public readonly record struct PamMessage(PamMessageStyle Style, string Text)
{
    public bool IsPrompt => Style is PamMessageStyle.PromptEchoOff or PamMessageStyle.PromptEchoOn;
}

public enum PamResult
{
    Success,
    /// <summary>Wrong password, unknown user, or too many tries.</summary>
    AuthenticationFailed,
    /// <summary>Authenticated, but the account may not log in (expired, locked, password change required).</summary>
    AccountUnavailable,
    /// <summary>The conversation returned null for a prompt.</summary>
    Aborted,
    /// <summary>PAM itself failed (missing service, module error).</summary>
    Error,
}

/// <summary>
/// Authentication through PAM (libpam), for lock screens:
/// <code>
/// var result = await PamAuthenticator.AuthenticateAsync(Environment.UserName, password);
/// </code>
/// Runs on a worker thread: PAM may block for seconds (the delay after a wrong password, fingerprint readers).
/// Both <c>pam_authenticate</c> and <c>pam_acct_mgmt</c> must succeed.
/// </summary>
public static unsafe partial class PamAuthenticator
{
    const string Lib = "libpam.so.0";
    const int PAM_SUCCESS = 0, PAM_BUF_ERR = 5, PAM_PERM_DENIED = 6, PAM_AUTH_ERR = 7, PAM_USER_UNKNOWN = 10,
        PAM_MAXTRIES = 11, PAM_NEW_AUTHTOK_REQD = 12, PAM_ACCT_EXPIRED = 13, PAM_CONV_ERR = 19;

    [StructLayout(LayoutKind.Sequential)]
    struct Message
    {
        public int Style;
        public byte* Text;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Response
    {
        public byte* Text;
        public int ReturnCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Conversation
    {
        public delegate* unmanaged[Cdecl]<int, Message**, Response**, void*, int> Function;
        public void* AppData;
    }

    [LibraryImport(Lib)] private static partial int pam_start(byte* service, byte* user, Conversation* conv, nint* handle);
    [LibraryImport(Lib)] private static partial int pam_start_confdir(byte* service, byte* user, Conversation* conv, byte* confdir, nint* handle);
    [LibraryImport(Lib)] private static partial int pam_authenticate(nint handle, int flags);
    [LibraryImport(Lib)] private static partial int pam_acct_mgmt(nint handle, int flags);
    [LibraryImport(Lib)] private static partial int pam_end(nint handle, int status);

    /// <summary>The usual lock screen check: every prompt is answered with <paramref name="password"/>.</summary>
    /// <param name="user">The account to check, normally <see cref="Environment.UserName"/>.</param>
    /// <param name="password">The typed password.</param>
    /// <param name="service">The PAM service (a file in /etc/pam.d), e.g. "login" or your shell's own.</param>
    /// <param name="configDirectory">Read the service from this directory instead of /etc/pam.d (Linux-PAM 1.4+).</param>
    public static Task<PamResult> AuthenticateAsync(string user, string password, string service = "login", string? configDirectory = null) =>
        AuthenticateAsync(user, m => m.IsPrompt ? password : "", service, configDirectory);

    /// <summary>
    /// The full conversation: <paramref name="converse"/> sees every message PAM sends (prompts, errors,
    /// information) on the worker thread and returns the answer to prompts; null aborts.
    /// </summary>
    public static Task<PamResult> AuthenticateAsync(string user, Func<PamMessage, string?> converse, string service = "login", string? configDirectory = null) =>
        Task.Run(() => Authenticate(user, converse, service, configDirectory));

    sealed class State(Func<PamMessage, string?> converse)
    {
        public readonly Func<PamMessage, string?> Converse = converse;
        public bool Aborted;
    }

    static PamResult Authenticate(string user, Func<PamMessage, string?> converse, string service, string? configDirectory)
    {
        var state = new State(converse);
        var handle = GCHandle.Alloc(state);
        byte* serviceName = Utf8(service), userName = Utf8(user), confdir = configDirectory is null ? null : Utf8(configDirectory);
        try
        {
            var conv = new Conversation { Function = &Converse, AppData = (void*)GCHandle.ToIntPtr(handle) };
            nint pam;
            int result = confdir is null ? pam_start(serviceName, userName, &conv, &pam) : pam_start_confdir(serviceName, userName, &conv, confdir, &pam);
            if (result != PAM_SUCCESS)
                return PamResult.Error;
            result = pam_authenticate(pam, 0);
            bool authenticated = result == PAM_SUCCESS;
            if (authenticated)
                result = pam_acct_mgmt(pam, 0);
            pam_end(pam, result);
            if (state.Aborted)
                return PamResult.Aborted;
            return result switch
            {
                PAM_SUCCESS => PamResult.Success,
                _ when authenticated => PamResult.AccountUnavailable,
                PAM_AUTH_ERR or PAM_USER_UNKNOWN or PAM_MAXTRIES or PAM_PERM_DENIED => PamResult.AuthenticationFailed,
                PAM_ACCT_EXPIRED or PAM_NEW_AUTHTOK_REQD => PamResult.AccountUnavailable,
                _ => PamResult.Error,
            };
        }
        finally
        {
            handle.Free();
            NativeMemory.Free(serviceName);
            NativeMemory.Free(userName);
            NativeMemory.Free(confdir);
        }
    }

    /// <summary>PAM's conversation callback. PAM frees the answers (and the array) with free().</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Converse(int count, Message** messages, Response** responses, void* appData)
    {
        if (count <= 0 || GCHandle.FromIntPtr((nint)appData).Target is not State state)
            return PAM_CONV_ERR;
        var replies = (Response*)NativeMemory.AllocZeroed((nuint)count, (nuint)sizeof(Response));
        if (replies == null)
            return PAM_BUF_ERR;
        for (int i = 0; i < count; i++)
        {
            // Linux-PAM passes an array of pointers to messages.
            var message = messages[i];
            string text = Marshal.PtrToStringUTF8((nint)message->Text) ?? "";
            string? answer;
            try
            {
                answer = state.Converse(new PamMessage((PamMessageStyle)message->Style, text));
            }
            catch (Exception)
            {
                answer = null;
            }
            bool prompt = message->Style is (int)PamMessageStyle.PromptEchoOff or (int)PamMessageStyle.PromptEchoOn;
            if (answer is null && prompt)
            {
                for (int j = 0; j < i; j++)
                    Wipe(replies[j].Text);
                NativeMemory.Free(replies);
                state.Aborted = true;
                return PAM_CONV_ERR;
            }
            if (prompt)
                replies[i].Text = Utf8(answer!);
        }
        *responses = replies;
        return PAM_SUCCESS;
    }

    static void Wipe(byte* s)
    {
        if (s == null)
            return;
        int length = 0;
        while (s[length] != 0)
            length++;
        new Span<byte>(s, length).Clear();
        NativeMemory.Free(s);
    }

    /// <summary>A malloc'ed UTF-8 copy (PAM frees answers with free(), which NativeMemory.Alloc matches on Linux).</summary>
    static byte* Utf8(string s)
    {
        int length = Encoding.UTF8.GetByteCount(s);
        var buffer = (byte*)NativeMemory.Alloc((nuint)length + 1);
        Encoding.UTF8.GetBytes(s, new Span<byte>(buffer, length));
        buffer[length] = 0;
        return buffer;
    }
}
