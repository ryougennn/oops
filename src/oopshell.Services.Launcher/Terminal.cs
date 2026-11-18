namespace oopshell.Services.Launcher;

/// <summary>
/// Runs a command in the user's terminal (for <c>Terminal=true</c> applications). Picks, in order:
/// the <c>preferred</c> argument, <c>$TERMINAL</c>, <c>xdg-terminal-exec</c>, then the first installed of the
/// common terminals, each with its own way of taking a command.
/// </summary>
public static class Terminal
{
    // How each terminal takes a command to run (what goes between the terminal and the command).
    static readonly (string Name, string[] Prefix)[] Known =
    [
        ("foot", []), ("kitty", []), ("alacritty", ["-e"]), ("wezterm", ["start", "--"]), ("ghostty", ["-e"]),
        ("konsole", ["-e"]), ("ptyxis", ["--"]), ("gnome-terminal", ["--"]), ("kgx", ["-e"]), ("xfce4-terminal", ["-x"]),
        ("tilix", ["-e"]), ("terminator", ["-x"]), ("lxterminal", ["-e"]), ("mate-terminal", ["-x"]), ("st", ["-e"]),
        ("urxvt", ["-e"]), ("xterm", ["-e"]),
    ];

    /// <summary>
    /// The command line that runs <paramref name="command"/> in a terminal, or null if no terminal is installed.
    /// <paramref name="preferred"/> and <c>$TERMINAL</c> may include options ("foot -o font=Mono:size=11").
    /// </summary>
    public static IReadOnlyList<string>? Wrap(IReadOnlyList<string> command, string? preferred = null, string? pathVariable = null)
    {
        foreach (var choice in new[] { preferred, Environment.GetEnvironmentVariable("TERMINAL") })
        {
            if (string.IsNullOrWhiteSpace(choice))
                continue;
            var parts = choice.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (FindOnPath(parts[0], pathVariable) is null)
                continue;
            string name = Path.GetFileName(parts[0]);
            var prefix = Known.FirstOrDefault(k => k.Name == name).Prefix ?? ["-e"];
            return [.. parts, .. prefix, .. command];
        }
        if (FindOnPath("xdg-terminal-exec", pathVariable) is not null)
            return ["xdg-terminal-exec", .. command];
        foreach (var (name, prefix) in Known)
        {
            if (FindOnPath(name, pathVariable) is not null)
                return [name, .. prefix, .. command];
        }
        return null;
    }

    /// <summary>The full path of an executable on <c>$PATH</c> (or an absolute/relative path that exists), or null.</summary>
    public static string? FindOnPath(string program, string? pathVariable = null)
    {
        if (program.Contains('/'))
            return IsExecutable(program) ? program : null;
        foreach (var dir in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, program);
            if (IsExecutable(candidate))
                return candidate;
        }
        return null;
    }

    static bool IsExecutable(string path) =>
        !OperatingSystem.IsWindows() && File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
