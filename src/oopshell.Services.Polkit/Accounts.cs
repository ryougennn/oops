using System.Runtime.InteropServices;

namespace oopshell.Services.Polkit;

/// <summary>A user who can answer an authentication request (from the identities polkit offers).</summary>
/// <param name="Name">Login name.</param>
/// <param name="Uid">User id.</param>
/// <param name="RealName">The full name from the password database, or the login name.</param>
public sealed record PolkitUser(string Name, uint Uid, string RealName);

/// <summary>Users and groups from the system's account database (NSS: files, LDAP, SSSD…).</summary>
internal static unsafe partial class Accounts
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Passwd
    {
        public byte* Name, Password;
        public uint Uid, Gid;
        public byte* Gecos, Dir, Shell;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Group
    {
        public byte* Name, Password;
        public uint Gid;
        public byte** Members;
    }

    // Not reentrant; only called on the UI thread.
    [LibraryImport("libc")] private static partial Passwd* getpwuid(uint uid);
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)] private static partial Passwd* getpwnam(string name);
    [LibraryImport("libc")] private static partial Group* getgrgid(uint gid);

    private static string Str(byte* p) => Marshal.PtrToStringUTF8((nint)p) ?? "";

    private static PolkitUser? From(Passwd* pw)
    {
        if (pw == null)
            return null;
        string name = Str(pw->Name);
        string gecos = Str(pw->Gecos).Split(',')[0];
        return new PolkitUser(name, pw->Uid, gecos.Length > 0 ? gecos : name);
    }

    public static PolkitUser? ByUid(uint uid) => From(getpwuid(uid));

    public static PolkitUser? ByName(string name) => From(getpwnam(name));

    /// <summary>The group's members (explicit members; users whose primary group it is are not listed by NSS).</summary>
    public static IEnumerable<PolkitUser> Members(uint gid)
    {
        var group = getgrgid(gid);
        if (group == null || group->Members == null)
            return [];
        var names = new List<string>();
        for (var member = group->Members; *member != null; member++)
            names.Add(Str(*member));
        return names.Select(ByName).OfType<PolkitUser>().ToList();
    }
}
