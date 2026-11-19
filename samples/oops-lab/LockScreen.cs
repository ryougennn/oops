using oopshell;
using oopshell.Services.Pam;
using oopshell.Skia;
using SkiaSharp;

namespace OopsLab;

/// <summary>A plain lock screen on every output: type the password, Enter; PAM decides.</summary>
static class LockScreen
{
    public static async Task LockAsync(OopsApp app, Action<string, SKColor?> log)
    {
        string text = "", message = $"Locked. Type {Environment.UserName}'s password and press Enter.";
        bool busy = false;
        SessionLock? session = null;
        var surfaces = new List<(LockSurface Surface, SkiaSurface Skia)>();
        void InvalidateAll() => surfaces.ForEach(s => s.Surface.Invalidate());

        session = await app.LockAsync(surface =>
        {
            var skia = new SkiaSurface(surface);
            surfaces.Add((surface, skia));
            surface.Render += frame =>
            {
                if (skia.BeginFrame(frame) is not { } c)
                    return;
                c.Scale((float)frame.Scale);
                float w = frame.LogicalSize.Width, h = frame.LogicalSize.Height;
                c.Clear(new SKColor(0x10, 0x13, 0x1A));
                Ui.Label(c, "oops-lab lock screen", w / 2, h / 2 - 60, Ui.Big, Ui.Text, SKTextAlign.Center);
                Ui.Label(c, message, w / 2, h / 2 - 24, Ui.Font, busy ? Ui.Dim : Ui.Text, SKTextAlign.Center);
                var field = SKRect.Create(w / 2 - 160, h / 2, 320, 36);
                Ui.Fill(c, field, Ui.Panel, 8);
                Ui.Label(c, new string('•', text.Length) + "▏", field.Left + 12, field.Top + 24, Ui.Font, Ui.Text);
                skia.EndFrame();
            };
            surface.TextInput += e =>
            {
                if (!busy)
                    text += e.Text;
                InvalidateAll();
            };
            surface.KeyDown += e =>
            {
                if (busy)
                    return;
                if (e.Keysym == Keysym.BackSpace && text.Length > 0)
                    text = text[..^1];
                else if (e.Keysym is Keysym.Return or Keysym.KP_Enter)
                {
                    busy = true;
                    message = "Checking…";
                    string password = text;
                    text = "";
                    app.Post(async () =>
                    {
                        var result = await PamAuthenticator.AuthenticateAsync(Environment.UserName, password);
                        busy = false;
                        if (result == PamResult.Success)
                        {
                            session?.Unlock();
                            foreach (var (_, s) in surfaces)
                                s.Dispose();
                            log("Unlocked with PAM.", Ui.Good);
                            return;
                        }
                        message = $"Not accepted ({result}). Try again.";
                        InvalidateAll();
                    });
                }
                InvalidateAll();
            };
        });
        log("Locked.", Ui.Accent);
    }
}
