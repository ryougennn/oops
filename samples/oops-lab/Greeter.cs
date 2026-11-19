using oopshell;
using oopshell.Services.Greetd;
using SkiaSharp;

namespace OopsLab;

/// <summary>A minimal greetd greeter: user name, password (retries), then the session starts.</summary>
sealed class Greeter(OopsApp app, Toplevel window, string session)
{
    readonly Ui ui = new();
    Prompt? prompt;
    string status = "Connecting to greetd…";
    GreetdClient? greetd;

    public void Start()
    {
        window.TextInput += e =>
        {
            if (prompt is { HasField: true })
                prompt.Text += e.Text;
            window.Invalidate();
        };
        window.KeyDown += e =>
        {
            if (prompt is null)
                return;
            if (e.Keysym is Keysym.Return or Keysym.KP_Enter)
                prompt.OnOk(prompt.Text);
            else if (e.Keysym == Keysym.BackSpace && prompt.Text.Length > 0)
                prompt.Text = prompt.Text[..^1];
            window.Invalidate();
        };
        window.PointerButton += e =>
        {
            if (e.Pressed)
                ui.HitTest(e.X, e.Y)?.Invoke();
            window.Invalidate();
        };
        app.Post(async () =>
        {
            try
            {
                greetd = await GreetdClient.ConnectAsync();
                AskUser();
            }
            catch (Exception e)
            {
                status = $"Not running under greetd: {e.Message}";
                window.Invalidate();
            }
        });
    }

    void AskUser()
    {
        status = $"Session: {session}";
        prompt = new Prompt { Title = "Log in", Message = "User name", OnOk = user => AskPassword(user, "Password") };
        window.Invalidate();
    }

    void AskPassword(string user, string message, bool wrong = false)
    {
        prompt = new Prompt
        {
            Title = $"Log in as {user}",
            Message = message,
            MessageColor = wrong ? Ui.Bad : Ui.Dim,
            Masked = true,
            OnOk = password => app.Post(async () =>
            {
                prompt = null;
                status = "Checking…";
                window.Invalidate();
                var result = await greetd!.LoginAsync(user, password);
                if (result.Succeeded)
                {
                    status = $"Starting {session}…";
                    window.Invalidate();
                    await greetd.StartSessionAsync(session.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    app.Quit(); // greetd starts the session once the greeter exits
                    return;
                }
                if (result.Status == GreetdLoginStatus.AuthenticationFailed)
                    AskPassword(user, "Wrong password. Try again.", wrong: true);
                else
                {
                    status = $"greetd: {result.Description}";
                    AskUser();
                }
            }),
            OnCancel = AskUser,
        };
        window.Invalidate();
    }

    public void Draw(SKCanvas c, float width, float height)
    {
        ui.ClearHits();
        c.Clear(new SKColor(0x10, 0x13, 0x1A));
        Ui.Label(c, "oops-lab greeter", width / 2, 80, Ui.Big, Ui.Text, SKTextAlign.Center);
        Ui.Label(c, status, width / 2, height - 40, Ui.Font, Ui.Dim, SKTextAlign.Center);
        prompt?.Draw(c, ui, width, height);
    }
}
