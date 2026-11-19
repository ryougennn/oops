using SkiaSharp;

namespace OopsLab;

/// <summary>
/// A tiny immediate-mode UI: each frame the page draws rows and buttons top to bottom, and buttons register
/// where they are so a click can find them. One modal prompt (text, password or yes/no) at a time.
/// </summary>
sealed class Ui
{
    public static readonly SKColor Background = new(0x1B, 0x1F, 0x27), Panel = new(0x24, 0x29, 0x33),
        Text = new(0xE6, 0xE9, 0xEF), Dim = new(0x8A, 0x93, 0xA3), Accent = new(0x5B, 0x9C, 0xF5), Good = new(0x6C, 0xC6, 0x7C),
        Bad = new(0xE8, 0x6B, 0x6B), ButtonFill = new(0x33, 0x3A, 0x47);

    static readonly SKTypeface Face = Pick("Noto Sans CJK JP", "Noto Sans", "DejaVu Sans");
    public static readonly SKFont Font = new(Face, 15) { Subpixel = true, Edging = SKFontEdging.Antialias };
    public static readonly SKFont Small = new(Face, 12.5f) { Subpixel = true, Edging = SKFontEdging.Antialias };
    public static readonly SKFont Big = new(Face, 21) { Subpixel = true, Edging = SKFontEdging.Antialias, Embolden = true };
    static readonly SKPaint Paint = new() { IsAntialias = true };

    static SKTypeface Pick(params string[] families) =>
        families.Select(f => SKFontManager.Default.MatchFamily(f)).FirstOrDefault(t => t is not null)
        ?? SKFontManager.Default.MatchCharacter('A');

    readonly List<(SKRect Rect, Action Action)> hits = new();
    SKCanvas c = null!;
    public float X, Y, Width;

    public void Begin(SKCanvas canvas, float x, float y, float width)
    {
        c = canvas;
        X = x;
        Y = y;
        Width = width;
    }

    public void ClearHits() => hits.Clear();

    public static void Fill(SKCanvas c, SKRect r, SKColor color, float radius = 0)
    {
        Paint.Style = SKPaintStyle.Fill;
        Paint.Color = color;
        c.DrawRoundRect(r, radius, radius, Paint);
    }

    public static void Label(SKCanvas c, string text, float x, float baseline, SKFont font, SKColor color, SKTextAlign align = SKTextAlign.Left)
    {
        Paint.Color = color;
        c.DrawText(text, x, baseline, align, font, Paint);
    }

    public void Heading(string text)
    {
        Label(c, text, X, Y + 22, Big, Text);
        Y += 38;
    }

    public void Line(string text, SKColor? color = null, SKFont? font = null)
    {
        Label(c, text, X, Y + 16, font ?? Font, color ?? Text);
        Y += 24;
    }

    public void Gap(float h = 10) => Y += h;

    /// <summary>A button at (x, y) in canvas space; returns its width.</summary>
    public float ButtonAt(float x, float y, string label, Action action, bool enabled = true, bool primary = false)
    {
        float w = Font.MeasureText(label) + 24;
        var r = SKRect.Create(x, y, w, 28);
        Fill(c, r, !enabled ? Panel : primary ? Accent : ButtonFill, 6);
        Label(c, label, r.MidX, r.Top + 19, Font, enabled ? Text : Dim, SKTextAlign.Center);
        if (enabled)
            hits.Add((r, action));
        return w;
    }

    /// <summary>A row of buttons.</summary>
    public void Buttons(params (string Label, Action Action)[] buttons)
    {
        float x = X;
        foreach (var (label, action) in buttons)
            x += ButtonAt(x, Y, label, action) + 8;
        Y += 36;
    }

    public void Toggle(string label, bool on, Action toggle, string? detail = null)
    {
        var box = SKRect.Create(X, Y + 3, 40, 22);
        Fill(c, box, on ? Good : ButtonFill, 11);
        Paint.Color = Text;
        c.DrawCircle(on ? box.Right - 11 : box.Left + 11, box.MidY, 8, Paint);
        hits.Add((SKRect.Create(X, Y, 60 + Font.MeasureText(label), 28), toggle));
        Label(c, label, X + 52, Y + 19, Font, Text);
        if (detail is not null)
            Label(c, detail, X + 64 + Font.MeasureText(label), Y + 19, Small, Dim);
        Y += 34;
    }

    /// <summary>A list row: a title, a dimmer detail, and buttons on the right.</summary>
    public void Item(string title, string? detail, params (string Label, Action Action)[] buttons)
    {
        var row = SKRect.Create(X, Y, Width, 40);
        Fill(c, row, Panel, 6);
        Label(c, title, X + 12, Y + (detail is null ? 25 : 18), Font, Text);
        if (detail is not null)
            Label(c, detail, X + 12, Y + 34, Small, Dim);
        float x = row.Right - 6;
        foreach (var (label, action) in buttons.Reverse())
        {
            x -= Font.MeasureText(label) + 24;
            ButtonAt(x, Y + 6, label, action);
            x -= 6;
        }
        Y += 46;
    }

    public int HitCount => hits.Count;

    /// <summary>The topmost button under (x, y), if any, among those registered from <paramref name="from"/> on.</summary>
    public Action? HitTest(double x, double y, int from = 0)
    {
        for (int i = hits.Count - 1; i >= from; i--)
        {
            if (hits[i].Rect.Contains((float)x, (float)y))
                return hits[i].Action;
        }
        return null;
    }
}

/// <summary>A modal question: a text or password field, or just buttons.</summary>
sealed class Prompt
{
    public required string Title { get; init; }
    public string Message { get; set; } = "";
    public bool HasField { get; init; } = true;
    public bool Masked { get; init; }
    public string Text = "";
    public SKColor MessageColor { get; set; } = Ui.Dim;
    /// <summary>Buttons other than the default (Enter) one; Escape runs <see cref="OnCancel"/>.</summary>
    public List<(string Label, Action Action)> Extra { get; } = new();
    public string OkLabel { get; init; } = "OK";
    public required Action<string> OnOk { get; init; }
    public Action? OnCancel { get; init; }

    public void Draw(SKCanvas c, Ui ui, float width, float height)
    {
        Ui.Fill(c, SKRect.Create(0, 0, width, height), new SKColor(0, 0, 0, 0x99));
        var box = SKRect.Create((width - 460) / 2, (height - 220) / 2, 460, 220);
        Ui.Fill(c, box, Ui.Panel, 10);
        Ui.Label(c, Title, box.Left + 20, box.Top + 36, Ui.Big, Ui.Text);
        float y = box.Top + 62;
        foreach (var line in Wrap(Message, 410))
        {
            Ui.Label(c, line, box.Left + 20, y, Ui.Small, MessageColor);
            y += 18;
        }
        if (HasField)
        {
            var field = SKRect.Create(box.Left + 20, box.Bottom - 92, 420, 32);
            Ui.Fill(c, field, Ui.Background, 6);
            string shown = Masked ? new string('•', Text.Length) : Text;
            Ui.Label(c, shown + "▏", field.Left + 10, field.Top + 22, Ui.Font, Ui.Text);
        }
        float x = box.Right - 20;
        var buttons = new List<(string, Action, bool)> { (OkLabel, () => OnOk(Text), true) };
        buttons.AddRange(Extra.Select(e => (e.Label, e.Action, false)));
        if (OnCancel is not null)
            buttons.Add(("Cancel", OnCancel, false));
        foreach (var (label, action, primary) in buttons)
        {
            x -= Ui.Font.MeasureText(label) + 24;
            ui.ButtonAt(x, box.Bottom - 44, label, action, primary: primary);
            x -= 8;
        }
    }

    static IEnumerable<string> Wrap(string text, float width)
    {
        foreach (var paragraph in text.Split('\n'))
        {
            string line = "";
            foreach (var word in paragraph.Split(' '))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (Ui.Small.MeasureText(next) > width && line.Length > 0)
                {
                    yield return line;
                    line = word;
                }
                else
                    line = next;
            }
            yield return line;
        }
    }
}
