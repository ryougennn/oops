using oopshell.Services.Desktop;

namespace oopshell.Services.Launcher;

/// <summary>A search result: an application, or one of its actions ("New Private Window").</summary>
public sealed record AppMatch(DesktopEntry Entry, DesktopAction? Action, double Score)
{
    /// <summary>What to show: the app name, or "App — Action".</summary>
    public string Label => Action is null ? Entry.Name : $"{Entry.Name} — {Action.Name}";
}

/// <summary>
/// Ranks applications for a query the way people type into launchers. Per field (best first): exact name,
/// name prefix, every query word starting a word of the name ("lo wri" → LibreOffice Writer), initials
/// ("lw"), substring, then the letters in order ("ffx" → Firefox). Fields, strongest first: the name, its
/// untranslated form (so "fire" finds ファイアウォール), generic name, keywords, the program, the desktop id,
/// the comment. Matching ignores case, accents, full-width forms and hiragana/katakana.
/// </summary>
public static class AppSearch
{
    /// <param name="apps">The applications to search (usually the visible ones).</param>
    /// <param name="query">What the user typed.</param>
    /// <param name="boost">Extra score per desktop id (usage); 0..20 is meaningful.</param>
    /// <param name="max">At most this many results.</param>
    public static List<AppMatch> Search(IEnumerable<DesktopEntry> apps, string query, Func<string, double>? boost = null, int max = 20)
    {
        string q = TextFold.Fold(query).Trim();
        if (q.Length == 0)
            return [];
        var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<AppMatch>();
        foreach (var app in apps)
        {
            double score = ScoreApp(app, q, terms);
            double extra = boost?.Invoke(app.Id) ?? 0;
            if (score > 0)
                results.Add(new AppMatch(app, null, score + extra));
            foreach (var action in app.Actions)
            {
                // Actions match on "App Action" so "firefox private" and "private" both find the private window.
                double actionScore = Field($"{app.Name} {action.Name}", q, terms) * 0.8;
                if (action.DefaultName is { } untranslated && untranslated != action.Name)
                    actionScore = Math.Max(actionScore, Field($"{app.DefaultName} {untranslated}", q, terms) * 0.75);
                if (actionScore >= 40)
                    results.Add(new AppMatch(app, action, actionScore + extra * 0.5));
            }
        }
        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Action is null ? 0 : 1)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    static double ScoreApp(DesktopEntry app, string q, string[] terms)
    {
        double best = Field(app.Name, q, terms);
        if (app.DefaultName != app.Name)
            best = Math.Max(best, Field(app.DefaultName, q, terms) * 0.95);
        if (app.GenericName is { } generic)
            best = Math.Max(best, Field(generic, q, terms) * 0.7);
        foreach (var keyword in app.Keywords)
            best = Math.Max(best, Field(keyword, q, terms) * 0.6);
        foreach (var keyword in app.DefaultKeywords)
            best = Math.Max(best, Field(keyword, q, terms) * 0.55);
        if (Program(app) is { } program)
            best = Math.Max(best, Field(program, q, terms) * 0.5);
        string id = app.Id.EndsWith(".desktop", StringComparison.Ordinal) ? app.Id[..^8] : app.Id;
        best = Math.Max(best, Field(id[(id.LastIndexOf('.') + 1)..], q, terms) * 0.5);
        if (app.Comment is { } comment)
            best = Math.Max(best, Contains(comment, q, terms) * 0.25);
        return best;
    }

    /// <summary>The program in Exec, skipping env and VAR=value (e.g. snap and flatpak wrappers keep their names elsewhere).</summary>
    static string? Program(DesktopEntry app) =>
        app.Exec?.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(t => t == "env" || t.Contains('='))
            .FirstOrDefault() is { } p ? Path.GetFileName(p.Trim('"')) : null;

    /// <summary>0..100 for one field.</summary>
    internal static double Field(string text, string q, string[] terms)
    {
        if (text.Length == 0)
            return 0;
        string f = TextFold.Fold(text);
        if (f == q)
            return 100;
        if (f.StartsWith(q, StringComparison.Ordinal))
            return 92 - Math.Min(10, (f.Length - q.Length) * 0.2);
        var words = Words(text);
        if (terms.All(t => words.Any(w => w.StartsWith(t, StringComparison.Ordinal))))
            return 82 - Math.Min(10, words.Count);
        if (q.Length >= 2 && !q.Contains(' ') && string.Concat(words.Select(w => w[0])).StartsWith(q, StringComparison.Ordinal))
            return 74;
        double contains = Contains(text, q, terms);
        if (contains > 0)
            return contains;
        return Subsequence(f, q.Replace(" ", ""));
    }

    static double Contains(string text, string q, string[] terms)
    {
        string f = TextFold.Fold(text);
        if (f.Contains(q, StringComparison.Ordinal))
            return 62;
        return terms.Length > 1 && terms.All(t => f.Contains(t, StringComparison.Ordinal)) ? 52 : 0;
    }

    /// <summary>All letters of the query in order; fewer gaps and an early start score higher (10..40).</summary>
    static double Subsequence(string f, string q)
    {
        if (q.Length < 2)
            return 0;
        int pos = 0, first = -1, gaps = 0, last = -1;
        foreach (char c in q)
        {
            int found = f.IndexOf(c, pos);
            if (found < 0)
                return 0;
            if (first < 0)
                first = found;
            else if (found != last + 1)
                gaps++;
            last = found;
            pos = found + 1;
        }
        return Math.Max(10, 40 - gaps * 6 - first * 2);
    }

    /// <summary>Words of a name: split on spaces and punctuation and at camel-case humps ("LibreOffice" → libre, office), folded.</summary>
    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }
            if (current.Length > 0 && char.IsUpper(c) && char.IsLower(text[i - 1]))
                Flush();
            current.Append(c);
        }
        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0)
                words.Add(TextFold.Fold(current.ToString()));
            current.Clear();
        }
    }
}
