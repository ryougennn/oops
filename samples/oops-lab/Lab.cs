using System.Diagnostics;
using oopshell;
using oopshell.Services.Audio;
using oopshell.Services.Bluetooth;
using oopshell.Services.DBus;
using oopshell.Services.Media;
using oopshell.Services.Network;
using oopshell.Services.Pam;
using oopshell.Services.Polkit;
using oopshell.Services.Power;
using SkiaSharp;
using Tmds.DBus.Protocol;

namespace OopsLab;

/// <summary>The lab: one page per manual check, the services behind them, and a log of what happened.</summary>
internal sealed partial class Lab
{
    private readonly OopsApp _app;
    private readonly Toplevel _window;
    private readonly Ui _ui = new();
    private readonly List<(string Text, SKColor Color)> _log = new();
    private readonly string[] _pages = ["Overview", "Wi-Fi", "Bluetooth", "Polkit", "Notifications", "Workspaces", "Inhibit", "Lock", "Audio", "Battery"];
    private string _page = "Overview";
    private Prompt? _prompt;
    private int _promptHits;
    private readonly List<(SKRect, Action)> _sidebarHits = new();

    private DBusConnection? _systemBus, _sessionBus;
    private NetworkService? _network;
    private BluetoothService? _bluetooth;
    private PolkitAgent? _polkit;
    private Process? _polkitTarget;
    private AudioService? _audio;
    private MediaService? _media;
    private PowerService? _power;
    private IDisposable? _idleWatch;
    private bool _idleNow;
    private readonly List<string> _notificationEvents = new();

    public Lab(OopsApp app, Toplevel window)
    {
        _app = app;
        _window = window;
        window.KeyDown += OnKey;
        window.TextInput += e =>
        {
            if (_prompt is { HasField: true })
            {
                _prompt.Text += e.Text;
                window.Invalidate();
            }
        };
        window.PointerButton += e =>
        {
            if (!e.Pressed || e.Button != PointerButton.Left)
                return;
            HitTest(e.X, e.Y)?.Invoke();
            window.Invalidate();
        };
        if (app.Workspaces is not null)
            app.WorkspacesChanged += window.Invalidate;
        window.ShortcutsInhibitedChanged += () =>
        {
            Log(window.ShortcutsInhibited ? "Shortcuts now go to the lab." : "Shortcuts go to the compositor again.");
            window.Invalidate();
        };
    }

    private void Log(string text, SKColor? color = null)
    {
        _log.Add(($"{DateTime.Now:HH:mm:ss}  {text}", color ?? Ui.Dim));
        if (_log.Count > 200)
            _log.RemoveAt(0);
        _window.Invalidate();
    }

    private void Error(string what, Exception e) => Log($"{what}: {(e is DBusErrorReplyException d ? $"{d.ErrorName}: {d.Message}" : e.Message)}", Ui.Bad);

    /// <summary>Runs async work from a button, logging failures instead of crashing.</summary>
    private void Run(string what, Func<Task> work) => _app.Post(async () =>
    {
        try
        {
            await work();
        }
        catch (Exception e)
        {
            Error(what, e);
        }
        _window.Invalidate();
    });

    private void Ask(Prompt p)
    {
        _prompt = p;
        _window.Invalidate();
    }

    private void CloseAsk()
    {
        _prompt = null;
        _window.Invalidate();
    }

    private void OnKey(KeyEvent e)
    {
        if (_prompt is null)
        {
            if (e.Keysym == Keysym.Tab)
            {
                _page = _pages[(Array.IndexOf(_pages, _page) + ((e.Modifiers & Modifiers.Shift) != 0 ? _pages.Length - 1 : 1)) % _pages.Length];
                _window.Invalidate();
            }
            return;
        }
        switch (e.Keysym)
        {
            case Keysym.Return or Keysym.KP_Enter:
                _prompt.OnOk(_prompt.Text);
                break;
            case Keysym.Escape:
                _prompt.OnCancel?.Invoke();
                break;
            case Keysym.BackSpace when _prompt.Text.Length > 0:
                _prompt.Text = _prompt.Text[..^1];
                break;
        }
        _window.Invalidate();
    }

    public async Task StartServicesAsync()
    {
        try
        {
            _systemBus = await OopsDBus.ConnectAsync(_app, DBusAddress.System);
        }
        catch (Exception e)
        {
            Error("System bus", e);
        }
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is not null)
        {
            try
            {
                _sessionBus = await OopsDBus.ConnectAsync(_app);
                _media = await MediaService.StartAsync(_app, _sessionBus);
                _media.Changed += _window.Invalidate;
                await WatchNotificationSignalsAsync();
            }
            catch (Exception e)
            {
                Error("Session bus", e);
            }
        }
        if (_systemBus is not null)
        {
            try
            {
                _network = await NetworkService.StartAsync(_app, _systemBus);
                _network.Changed += _window.Invalidate;
                _network.SecretsRequested += OnSecretsRequested;
            }
            catch (Exception e)
            {
                Error("NetworkManager", e);
            }
            try
            {
                _bluetooth = await BluetoothService.StartAsync(_app, _systemBus);
                _bluetooth.Changed += _window.Invalidate;
                _bluetooth.PairingRequested += OnPairingRequested;
            }
            catch (Exception e)
            {
                Error("BlueZ", e);
            }
            try
            {
                _power = await PowerService.StartAsync(_app, _systemBus);
                _power.OnBatteryChanged += _window.Invalidate;
            }
            catch (Exception e)
            {
                Error("UPower", e);
            }
        }
        try
        {
            _audio = await AudioService.StartAsync(_app);
            _audio.Changed += _window.Invalidate;
        }
        catch (Exception e)
        {
            Error("Audio", e);
        }
        Log("Services started.", Ui.Good);
    }

    public void Draw(SKCanvas c, float width, float height)
    {
        _ui.ClearHits();
        _sidebarHits.Clear();
        c.Clear(Ui.Background);
        // Sidebar.
        Ui.Fill(c, SKRect.Create(0, 0, 170, height), Ui.Panel);
        Ui.Label(c, "oops-lab", 18, 34, Ui.Big, Ui.Text);
        float y = 56;
        foreach (var name in _pages)
        {
            var r = SKRect.Create(10, y, 150, 30);
            if (name == _page)
                Ui.Fill(c, r, Ui.ButtonFill, 6);
            Ui.Label(c, name, 22, y + 20, Ui.Font, name == _page ? Ui.Text : Ui.Dim);
            string target = name;
            _sidebarHits.Add((r, () => _page = target));
            y += 34;
        }
        Ui.Label(c, "Tab: next page", 18, height - 16, Ui.Small, Ui.Dim);

        // Page, then the log below it.
        float logHeight = 150;
        c.Save();
        c.ClipRect(SKRect.Create(170, 0, width - 170, height - logHeight));
        _ui.Begin(c, 196, 20, width - 196 - 24);
        DrawPage();
        c.Restore();
        var logBox = SKRect.Create(170, height - logHeight, width - 170, logHeight);
        Ui.Fill(c, logBox, new SKColor(0x15, 0x18, 0x1F));
        float ly = logBox.Top + 20;
        foreach (var (text, color) in _log.TakeLast(7))
        {
            Ui.Label(c, text, logBox.Left + 16, ly, Ui.Small, color);
            ly += 18;
        }
        _promptHits = _ui.HitCount;
        _prompt?.Draw(c, _ui, width, height);
    }

    /// <summary>While a prompt is open only its buttons count.</summary>
    private Action? HitTest(double x, double y) => _prompt is not null
        ? _ui.HitTest(x, y, _promptHits)
        : _ui.HitTest(x, y) ?? _sidebarHits.LastOrDefault(h => h.Item1.Contains((float)x, (float)y)).Item2;

    private void DrawPage()
    {
        switch (_page)
        {
            case "Overview": Overview(); break;
            case "Wi-Fi": Wifi(); break;
            case "Bluetooth": Bluetooth(); break;
            case "Polkit": Polkit(); break;
            case "Notifications": Notifications(); break;
            case "Workspaces": Workspaces(); break;
            case "Inhibit": Inhibit(); break;
            case "Lock": Lock(); break;
            case "Audio": Audio(); break;
            case "Battery": Battery(); break;
        }
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private void Overview()
    {
        _ui.Heading("What this compositor and system offer");
        _ui.Line($"Background blur (ext-background-effect): {YesNo(_app.CanBlurBehind)}");
        _ui.Line($"Idle inhibitor: {YesNo(_app.CanInhibitIdle)}    Shortcut inhibitor: {YesNo(_app.CanInhibitShortcuts)}");
        _ui.Line($"Workspaces (ext-workspace): {YesNo(_app.Workspaces is not null)}    Session lock: {YesNo(_app.Has<oopshell.Protocol.ExtSessionLockManagerV1>())}");
        _ui.Line($"Activation tokens (xdg-activation): {YesNo(_app.Has<oopshell.Protocol.XdgActivationV1>())}    Window list: {YesNo(_app.Toplevels is not null)}");
        _ui.Gap();
        _ui.Line($"NetworkManager: {YesNo(_network is not null)}    BlueZ: {YesNo(_bluetooth is not null)}    UPower: {YesNo(_power is not null)}");
        _ui.Line($"Audio: {YesNo(_audio is not null)}    MPRIS: {YesNo(_media is not null)}    Session bus: {YesNo(_sessionBus is not null)}");
        _ui.Gap();
        _ui.Line("Each page is one manual check from docs/MANUAL_TESTS.md. Results appear in the log below.", Ui.Dim, Ui.Small);
        _ui.Line("Run inside a nested compositor for the lock and greeter checks.", Ui.Dim, Ui.Small);
    }
}
