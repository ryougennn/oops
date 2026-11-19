using oopshell;
using oopshell.Services.Greetd;
using SkiaSharp;

namespace OopsLab;

/// <summary>A minimal greetd greeter: user name, password (retries), then the session starts.</summary>
internal sealed class Greeter(OopsApp app, Toplevel window, string session)
{
    private readonly Ui _ui = new();
    private Prompt? _prompt;
    private string _status = "Connecting to greetd…";
    private GreetdClient? _greetd;

    public void Start()
    {
        window.TextInput += e =>
        {
            if (_prompt is { HasField: true })
                _prompt.Text += e.Text;
            window.Invalidate();
        };
        window.KeyDown += e =>
        {
            if (_prompt is null)
                return;
            if (e.Keysym is Keysym.Return or Keysym.KP_Enter)
                _prompt.OnOk(_prompt.Text);
            else if (e.Keysym == Keysym.BackSpace && _prompt.Text.Length > 0)
                _prompt.Text = _prompt.Text[..^1];
            window.Invalidate();
        };
        window.PointerButton += e =>
        {
            if (e.Pressed)
                _ui.HitTest(e.X, e.Y)?.Invoke();
            window.Invalidate();
        };
        app.Post(async () =>
        {
            try
            {
                _greetd = await GreetdClient.ConnectAsync();
                AskUser();
            }
            catch (Exception e)
            {
                _status = $"Not running under greetd: {e.Message}";
                window.Invalidate();
            }
        });
    }

    private void AskUser()
    {
        _status = $"Session: {session}";
        _prompt = new Prompt { Title = "Log in", Message = "User name", OnOk = user => AskPassword(user, "Password") };
        window.Invalidate();
    }

    private void AskPassword(string user, string message, bool wrong = false)
    {
        _prompt = new Prompt
        {
            Title = $"Log in as {user}",
            Message = message,
            MessageColor = wrong ? Ui.Bad : Ui.Dim,
            Masked = true,
            OnOk = password => app.Post(async () =>
            {
                _prompt = null;
                _status = "Checking…";
                window.Invalidate();
                var result = await _greetd!.LoginAsync(user, password);
                if (result.Succeeded)
                {
                    _status = $"Starting {session}…";
                    window.Invalidate();
                    await _greetd.StartSessionAsync(session.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    app.Quit(); // greetd starts the session once the greeter exits
                    return;
                }
                if (result.Status == GreetdLoginStatus.AuthenticationFailed)
                    AskPassword(user, "Wrong password. Try again.", wrong: true);
                else
                {
                    AskUser();
                    _status = $"greetd: {result.Description}"; // after AskUser, which resets the status line
                }
            }),
            OnCancel = AskUser,
        };
        window.Invalidate();
    }

    public void Draw(SKCanvas c, float width, float height)
    {
        _ui.ClearHits();
        c.Clear(new SKColor(0x10, 0x13, 0x1A));
        Ui.Label(c, "oops-lab greeter", width / 2, 80, Ui.Big, Ui.Text, SKTextAlign.Center);
        Ui.Label(c, _status, width / 2, height - 40, Ui.Font, Ui.Dim, SKTextAlign.Center);
        _prompt?.Draw(c, _ui, width, height);
    }
}
