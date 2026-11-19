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
sealed partial class Lab
{
    readonly OopsApp app;
    readonly Toplevel window;
    readonly Ui ui = new();
    readonly List<(string Text, SKColor Color)> log = new();
    readonly string[] pages = ["Overview", "Wi-Fi", "Bluetooth", "Polkit", "Notifications", "Workspaces", "Inhibit", "Lock", "Audio", "Battery"];
    string page = "Overview";
    Prompt? prompt;

    DBusConnection? systemBus, sessionBus;
    NetworkService? network;
    BluetoothService? bluetooth;
    PolkitAgent? polkit;
    Process? polkitTarget;
    AudioService? audio;
    MediaService? media;
    PowerService? power;
    IDisposable? idleWatch;
    bool idleNow;
    readonly List<string> notificationEvents = new();

    public Lab(OopsApp app, Toplevel window)
    {
        this.app = app;
        this.window = window;
        window.KeyDown += OnKey;
        window.TextInput += e =>
        {
            if (prompt is { HasField: true })
            {
                prompt.Text += e.Text;
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

    void Log(string text, SKColor? color = null)
    {
        log.Add(($"{DateTime.Now:HH:mm:ss}  {text}", color ?? Ui.Dim));
        if (log.Count > 200)
            log.RemoveAt(0);
        window.Invalidate();
    }

    void Error(string what, Exception e) => Log($"{what}: {(e is DBusErrorReplyException d ? $"{d.ErrorName}: {d.Message}" : e.Message)}", Ui.Bad);

    /// <summary>Runs async work from a button, logging failures instead of crashing.</summary>
    void Run(string what, Func<Task> work) => app.Post(async () =>
    {
        try
        {
            await work();
        }
        catch (Exception e)
        {
            Error(what, e);
        }
        window.Invalidate();
    });

    void Ask(Prompt p)
    {
        prompt = p;
        window.Invalidate();
    }

    void CloseAsk()
    {
        prompt = null;
        window.Invalidate();
    }

    void OnKey(KeyEvent e)
    {
        if (prompt is null)
        {
            if (e.Keysym == Keysym.Tab)
            {
                page = pages[(Array.IndexOf(pages, page) + ((e.Modifiers & Modifiers.Shift) != 0 ? pages.Length - 1 : 1)) % pages.Length];
                window.Invalidate();
            }
            return;
        }
        switch (e.Keysym)
        {
            case Keysym.Return or Keysym.KP_Enter:
                prompt.OnOk(prompt.Text);
                break;
            case Keysym.Escape:
                prompt.OnCancel?.Invoke();
                break;
            case Keysym.BackSpace when prompt.Text.Length > 0:
                prompt.Text = prompt.Text[..^1];
                break;
        }
        window.Invalidate();
    }

    public async Task StartServicesAsync()
    {
        try
        {
            systemBus = await OopsDBus.ConnectAsync(app, DBusAddress.System);
        }
        catch (Exception e)
        {
            Error("System bus", e);
        }
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is not null)
        {
            try
            {
                sessionBus = await OopsDBus.ConnectAsync(app);
                media = await MediaService.StartAsync(app, sessionBus);
                media.Changed += window.Invalidate;
                await WatchNotificationSignalsAsync();
            }
            catch (Exception e)
            {
                Error("Session bus", e);
            }
        }
        if (systemBus is not null)
        {
            try
            {
                network = await NetworkService.StartAsync(app, systemBus);
                network.Changed += window.Invalidate;
                network.SecretsRequested += OnSecretsRequested;
            }
            catch (Exception e)
            {
                Error("NetworkManager", e);
            }
            try
            {
                bluetooth = await BluetoothService.StartAsync(app, systemBus);
                bluetooth.Changed += window.Invalidate;
                bluetooth.PairingRequested += OnPairingRequested;
            }
            catch (Exception e)
            {
                Error("BlueZ", e);
            }
            try
            {
                power = await PowerService.StartAsync(app, systemBus);
                power.OnBatteryChanged += window.Invalidate;
            }
            catch (Exception e)
            {
                Error("UPower", e);
            }
        }
        try
        {
            audio = await AudioService.StartAsync(app);
            audio.Changed += window.Invalidate;
        }
        catch (Exception e)
        {
            Error("Audio", e);
        }
        Log("Services started.", Ui.Good);
    }

    public void Draw(SKCanvas c, float width, float height)
    {
        ui.ClearHits();
        sidebarHits.Clear();
        c.Clear(Ui.Background);
        // Sidebar.
        Ui.Fill(c, SKRect.Create(0, 0, 170, height), Ui.Panel);
        Ui.Label(c, "oops-lab", 18, 34, Ui.Big, Ui.Text);
        float y = 56;
        foreach (var name in pages)
        {
            var r = SKRect.Create(10, y, 150, 30);
            if (name == page)
                Ui.Fill(c, r, Ui.ButtonFill, 6);
            Ui.Label(c, name, 22, y + 20, Ui.Font, name == page ? Ui.Text : Ui.Dim);
            string target = name;
            sidebarHits.Add((r, () => page = target));
            y += 34;
        }
        Ui.Label(c, "Tab: next page", 18, height - 16, Ui.Small, Ui.Dim);

        // Page, then the log below it.
        float logHeight = 150;
        c.Save();
        c.ClipRect(SKRect.Create(170, 0, width - 170, height - logHeight));
        ui.Begin(c, 196, 20, width - 196 - 24);
        DrawPage();
        c.Restore();
        var logBox = SKRect.Create(170, height - logHeight, width - 170, logHeight);
        Ui.Fill(c, logBox, new SKColor(0x15, 0x18, 0x1F));
        float ly = logBox.Top + 20;
        foreach (var (text, color) in log.TakeLast(7))
        {
            Ui.Label(c, text, logBox.Left + 16, ly, Ui.Small, color);
            ly += 18;
        }
        promptHits = ui.HitCount;
        prompt?.Draw(c, ui, width, height);
    }

    int promptHits;

    readonly List<(SKRect, Action)> sidebarHits = new();

    /// <summary>While a prompt is open only its buttons count.</summary>
    Action? HitTest(double x, double y) => prompt is not null
        ? ui.HitTest(x, y, promptHits)
        : ui.HitTest(x, y) ?? sidebarHits.LastOrDefault(h => h.Item1.Contains((float)x, (float)y)).Item2;

    void DrawPage()
    {
        switch (page)
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

    static string YesNo(bool value) => value ? "yes" : "no";

    void Overview()
    {
        ui.Heading("What this compositor and system offer");
        ui.Line($"Background blur (ext-background-effect): {YesNo(app.CanBlurBehind)}");
        ui.Line($"Idle inhibitor: {YesNo(app.CanInhibitIdle)}    Shortcut inhibitor: {YesNo(app.CanInhibitShortcuts)}");
        ui.Line($"Workspaces (ext-workspace): {YesNo(app.Workspaces is not null)}    Session lock: {YesNo(app.Has<oopshell.Protocol.ExtSessionLockManagerV1>())}");
        ui.Line($"Activation tokens (xdg-activation): {YesNo(app.Has<oopshell.Protocol.XdgActivationV1>())}    Window list: {YesNo(app.Toplevels is not null)}");
        ui.Gap();
        ui.Line($"NetworkManager: {YesNo(network is not null)}    BlueZ: {YesNo(bluetooth is not null)}    UPower: {YesNo(power is not null)}");
        ui.Line($"Audio: {YesNo(audio is not null)}    MPRIS: {YesNo(media is not null)}    Session bus: {YesNo(sessionBus is not null)}");
        ui.Gap();
        ui.Line("Each page is one manual check from docs/MANUAL_TESTS.md. Results appear in the log below.", Ui.Dim, Ui.Small);
        ui.Line("Run inside a nested compositor for the lock and greeter checks.", Ui.Dim, Ui.Small);
    }
}
