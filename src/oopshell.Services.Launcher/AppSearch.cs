using System.Runtime.CompilerServices;
using System.Text;
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
        string compact = q.Replace(" ", "");
        var results = new List<AppMatch>();
        foreach (var app in apps)
        {
            var prepared = Prepared.For(app);
            double score = ScoreApp(prepared, q, terms, compact);
            double extra = boost?.Invoke(app.Id) ?? 0;
            if (score > 0)
                results.Add(new AppMatch(app, null, score + extra));
            foreach (var (action, label, defaultLabel) in prepared.Actions)
            {
                // Actions match on "App Action" so "firefox private" and "private" both find the private window.
                double actionScore = Field(label, q, terms, compact) * 0.8;
                if (defaultLabel is not null)
                    actionScore = Math.Max(actionScore, Field(defaultLabel, q, terms, compact) * 0.75);
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

    /// <summary>A field's text prepared once: folded, split into folded words, and their initials.</summary>
    private sealed class FieldText
    {
        public readonly string Folded;
        public readonly List<string> Words;
        public readonly string Initials;

        public FieldText(string text)
        {
            Folded = TextFold.Fold(text);
            Words = AppSearch.Words(text);
            Initials = string.Concat(Words.Select(w => w[0]));
        }
    }

    /// <summary>
    /// Everything search needs from one entry, computed on first use and cached for the entry's lifetime
    /// (a catalog reload creates new entries, so stale caches are collected with them).
    /// </summary>
    private sealed class Prepared
    {
        private static readonly ConditionalWeakTable<DesktopEntry, Prepared> s_cache = new();

        public readonly FieldText Name, IdTail;
        public readonly FieldText? DefaultName, Generic, Program;
        public readonly FieldText[] Keywords, DefaultKeywords;
        public readonly string? Comment;
        public readonly (DesktopAction Action, FieldText Label, FieldText? DefaultLabel)[] Actions;

        public static Prepared For(DesktopEntry app) => s_cache.GetValue(app, a => new Prepared(a));

        private Prepared(DesktopEntry app)
        {
            Name = new FieldText(app.Name);
            DefaultName = app.DefaultName != app.Name && app.DefaultName.Length > 0 ? new FieldText(app.DefaultName) : null;
            Generic = app.GenericName is { } generic ? new FieldText(generic) : null;
            Keywords = app.Keywords.Select(k => new FieldText(k)).ToArray();
            DefaultKeywords = app.DefaultKeywords.Select(k => new FieldText(k)).ToArray();
            Program = AppSearch.Program(app) is { } program ? new FieldText(program) : null;
            string id = app.Id.EndsWith(".desktop", StringComparison.Ordinal) ? app.Id[..^8] : app.Id;
            IdTail = new FieldText(id[(id.LastIndexOf('.') + 1)..]);
            Comment = app.Comment is { } comment ? TextFold.Fold(comment) : null;
            Actions = app.Actions.Select(a => (a, new FieldText($"{app.Name} {a.Name}"),
                a.DefaultName is { } untranslated && untranslated != a.Name ? new FieldText($"{app.DefaultName} {untranslated}") : null)).ToArray();
        }
    }

    private static double ScoreApp(Prepared app, string q, string[] terms, string compact)
    {
        double best = Field(app.Name, q, terms, compact);
        if (app.DefaultName is { } defaultName)
            best = Math.Max(best, Field(defaultName, q, terms, compact) * 0.95);
        if (app.Generic is { } generic)
            best = Math.Max(best, Field(generic, q, terms, compact) * 0.7);
        foreach (var keyword in app.Keywords)
            best = Math.Max(best, Field(keyword, q, terms, compact) * 0.6);
        foreach (var keyword in app.DefaultKeywords)
            best = Math.Max(best, Field(keyword, q, terms, compact) * 0.55);
        if (app.Program is { } program)
            best = Math.Max(best, Field(program, q, terms, compact) * 0.5);
        best = Math.Max(best, Field(app.IdTail, q, terms, compact) * 0.5);
        if (app.Comment is { } comment)
            best = Math.Max(best, Contains(comment, q, terms) * 0.25);
        return best;
    }

    /// <summary>The program in Exec, skipping env and VAR=value (e.g. snap and flatpak wrappers keep their names elsewhere).</summary>
    private static string? Program(DesktopEntry app) =>
        app.Exec?.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(t => t == "env" || t.Contains('='))
            .FirstOrDefault() is { } p ? Path.GetFileName(p.Trim('"')) : null;

    /// <summary>0..100 for one field.</summary>
    private static double Field(FieldText field, string q, string[] terms, string compact)
    {
        string f = field.Folded;
        if (f.Length == 0)
            return 0;
        if (f == q)
            return 100;
        if (f.StartsWith(q, StringComparison.Ordinal))
            return 92 - Math.Min(10, (f.Length - q.Length) * 0.2);
        if (AllTermsStartWords(field.Words, terms))
            return 82 - Math.Min(10, field.Words.Count);
        if (q.Length >= 2 && !q.Contains(' ') && field.Initials.StartsWith(q, StringComparison.Ordinal))
            return 74;
        double contains = Contains(f, q, terms);
        if (contains > 0)
            return contains;
        return Subsequence(f, compact);
    }

    private static bool AllTermsStartWords(List<string> words, string[] terms)
    {
        foreach (var term in terms)
        {
            bool found = false;
            foreach (var word in words)
            {
                if (word.StartsWith(term, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }
        return true;
    }

    /// <summary>Substring match on folded text.</summary>
    private static double Contains(string folded, string q, string[] terms)
    {
        if (folded.Contains(q, StringComparison.Ordinal))
            return 62;
        if (terms.Length < 2)
            return 0;
        foreach (var term in terms)
        {
            if (!folded.Contains(term, StringComparison.Ordinal))
                return 0;
        }
        return 52;
    }

    /// <summary>All letters of the query in order; fewer gaps and an early start score higher (10..40).</summary>
    private static double Subsequence(string f, string q)
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
        var current = new StringBuilder();
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
