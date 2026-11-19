using System.Diagnostics;
using oopshell;
using oopshell.Services.Bluetooth;
using oopshell.Services.DBus;
using oopshell.Services.Media;
using oopshell.Services.Network;
using oopshell.Services.Polkit;
using Tmds.DBus.Protocol;

namespace OopsLab;

internal sealed partial class Lab
{
    // ---- Wi-Fi -------------------------------------------------------------------------------

    private void Wifi()
    {
        _ui.Heading("Wi-Fi (NetworkManager)");
        if (_network is null)
        {
            _ui.Line("NetworkManager is not available.", Ui.Bad);
            return;
        }
        _ui.Line($"Connectivity: {_network.Connectivity}    Primary: {_network.PrimaryConnectionName ?? "none"} ({_network.PrimaryConnectionType})");
        foreach (var device in _network.Devices)
            _ui.Line($"{device.Interface}: {device.Type}, {device.State}{(device.ConnectionName is { } n ? $" — {n}" : "")}", Ui.Dim, Ui.Small);
        _ui.Gap(6);
        _ui.Toggle("Wi-Fi", _network.WifiEnabled, () => Run("Wi-Fi", () => _network.SetWifiEnabledAsync(!_network.WifiEnabled)),
            _network.WifiHardwareEnabled ? null : "(hardware switch off)");
        _ui.Buttons(("Scan", () => { if (_network.Wifi is { } w) Run("Scan", () => _network.ScanAsync(w)); }
        ),
            ("Ask me for Wi-Fi passwords (secret agent)", () => Run("Secret agent", async () =>
            {
                await _network.RegisterSecretAgentAsync();
                Log("Registered as a NetworkManager secret agent.", Ui.Good);
            })));
        if (_network.Wifi is not { } wifi)
        {
            _ui.Line("No Wi-Fi device.", Ui.Dim);
            return;
        }
        foreach (var ap in wifi.AccessPoints.Take(12))
        {
            string detail = $"{ap.Strength}%  {(ap.Frequency > 3000 ? "5 GHz" : "2.4 GHz")}  {(ap.Secured ? "secured" : "open")}{(ap.IsActive ? "  — connected" : "")}";
            if (ap.IsActive)
                _ui.Item(ap.Ssid, detail, ("Disconnect", () => Run("Disconnect", () => _network.DisconnectAsync(wifi))));
            else
                _ui.Item(ap.Ssid, detail, ("Connect", () => ConnectWifi(wifi, ap)));
        }
    }

    private void ConnectWifi(NetworkDevice wifi, AccessPoint ap)
    {
        if (!ap.Secured)
        {
            Run("Connect", () => _network!.ConnectAsync(wifi, ap));
            Log($"Connecting to {ap.Ssid}…");
            return;
        }
        Ask(new Prompt
        {
            Title = $"Connect to {ap.Ssid}",
            Message = "Password (leave empty if this network is saved).",
            Masked = true,
            OkLabel = "Connect",
            OnOk = password =>
            {
                CloseAsk();
                Log($"Connecting to {ap.Ssid}…");
                Run("Connect", () => _network!.ConnectAsync(wifi, ap, password.Length > 0 ? password : null));
            },
            OnCancel = CloseAsk,
        });
    }

    private void OnSecretsRequested(NetworkSecretRequest request)
    {
        Log($"NetworkManager asks for the password of {request.Ssid ?? request.ConnectionName}{(request.IsRetry ? " (the last one was wrong)" : "")}.", Ui.Accent);
        var p = new Prompt
        {
            Title = $"Password for {request.Ssid ?? request.ConnectionName}",
            Message = request.IsRetry ? "The saved password did not work. Enter it again." : "NetworkManager needs the network's password.",
            MessageColor = request.IsRetry ? Ui.Bad : Ui.Dim,
            Masked = true,
            OnOk = password =>
            {
                request.Provide(password);
                CloseAsk();
            },
            OnCancel = () =>
            {
                request.Cancel();
                CloseAsk();
            },
        };
        request.Cancelled += () =>
        {
            Log("NetworkManager stopped waiting for the password.");
            if (_prompt == p)
                CloseAsk();
        };
        Ask(p);
    }

    // ---- Bluetooth ---------------------------------------------------------------------------

    private void Bluetooth()
    {
        _ui.Heading("Bluetooth (BlueZ)");
        if (_bluetooth is null)
        {
            _ui.Line("BlueZ is not available.", Ui.Bad);
            return;
        }
        if (_bluetooth.Adapter is not { } adapter)
        {
            _ui.Line("No adapter.", Ui.Dim);
            return;
        }
        _ui.Line($"{adapter.Name} ({adapter.Address})");
        _ui.Toggle("Powered", adapter.Powered, () => Run("Power", () => _bluetooth.SetPoweredAsync(adapter, !adapter.Powered)));
        _ui.Toggle("Discovering", adapter.Discovering, () => Run("Discovery", () =>
            adapter.Discovering ? _bluetooth.StopDiscoveryAsync(adapter) : _bluetooth.StartDiscoveryAsync(adapter)));
        _ui.Buttons(("Handle pairing codes (pairing agent)", () => Run("Pairing agent", async () =>
        {
            await _bluetooth.RegisterPairingAgentAsync();
            Log("Registered as BlueZ's default pairing agent.", Ui.Good);
        })));
        foreach (var device in _bluetooth.Devices.Take(12))
        {
            string detail = $"{device.Address}  {device.Icon}" + (device.Battery is { } b ? $"  battery {b}%" : "")
                + (device.Connected ? "  — connected" : device.Paired ? "  — paired" : "") + (device.Rssi is { } r ? $"  {r} dBm" : "");
            var buttons = new List<(string, Action)>();
            if (!device.Paired)
                buttons.Add(("Pair", () => Run($"Pair {device.Name}", async () => { await _bluetooth.PairAsync(device); Log($"Paired {device.Name}.", Ui.Good); })));
            else if (!device.Connected)
                buttons.Add(("Connect", () => Run($"Connect {device.Name}", () => _bluetooth.ConnectAsync(device))));
            if (device.Connected)
                buttons.Add(("Disconnect", () => Run($"Disconnect {device.Name}", () => _bluetooth.DisconnectAsync(device))));
            if (device.Paired)
                buttons.Add(("Forget", () => Run($"Forget {device.Name}", () => _bluetooth.ForgetAsync(device))));
            _ui.Item(device.Name, detail, buttons.ToArray());
        }
    }

    private void OnPairingRequested(BluetoothPairingRequest request)
    {
        string name = request.Device?.Name ?? request.DevicePath;
        Log($"Pairing with {name}: {request.Kind}.", Ui.Accent);
        Prompt p = request.Kind switch
        {
            BluetoothPairingKind.EnterPinCode or BluetoothPairingKind.EnterPasskey => new Prompt
            {
                Title = $"Pair with {name}",
                Message = request.Kind == BluetoothPairingKind.EnterPinCode ? "Enter the PIN for the device (often 0000)." : "Enter the 6-digit passkey shown on the device.",
                OnOk = text =>
                {
                    if (request.Kind == BluetoothPairingKind.EnterPinCode)
                        request.Provide(text);
                    else if (uint.TryParse(text, out uint passkey))
                        request.Provide(passkey);
                    else
                        return;
                    CloseAsk();
                },
                OnCancel = () => { request.Reject(); CloseAsk(); },
            },
            BluetoothPairingKind.ShowPinCode or BluetoothPairingKind.ShowPasskey => new Prompt
            {
                Title = $"Pair with {name}",
                Message = $"Type {request.PinCode ?? request.Passkey?.ToString("D6")} on the device, then Enter.",
                HasField = false,
                OnOk = _ => CloseAsk(),
            },
            BluetoothPairingKind.ConfirmPasskey => new Prompt
            {
                Title = $"Pair with {name}",
                Message = $"Does the device show {request.Passkey:D6}?",
                HasField = false,
                OkLabel = "Yes, pair",
                OnOk = _ => { request.Accept(); CloseAsk(); },
                OnCancel = () => { request.Reject(); CloseAsk(); },
            },
            _ => new Prompt
            {
                Title = $"Allow {name}?",
                Message = request.ServiceUuid is { } uuid ? $"The device wants to use service {uuid}." : "The device wants to pair.",
                HasField = false,
                OkLabel = "Allow",
                OnOk = _ => { request.Accept(); CloseAsk(); },
                OnCancel = () => { request.Reject(); CloseAsk(); },
            },
        };
        request.Finished += () =>
        {
            if (_prompt == p)
                CloseAsk();
        };
        Ask(p);
    }

    // ---- Polkit ------------------------------------------------------------------------------

    private void Polkit()
    {
        _ui.Heading("Polkit authentication agent");
        _ui.Line("Safe check: an agent for one helper process, then pkcheck asks polkit whether that process may run", Ui.Dim, Ui.Small);
        _ui.Line("programs as root. A correct password says \"authorized\"; nothing is run.", Ui.Dim, Ui.Small);
        _ui.Gap(4);
        _ui.Line(_polkit is null ? "Agent: not running" : _polkitTarget is null ? "Agent: this session" : $"Agent: for helper process {_polkitTarget.Id}",
            _polkit is null ? Ui.Dim : Ui.Good);
        if (_polkit is null)
        {
            _ui.Buttons(("Start agent for a helper process", StartTestAgent),
                ("Start agent for this session", () => StartAgent(null)));
        }
        else
        {
            _ui.Buttons(("Ask polkit (pkcheck)", RunPkcheck), ("Stop agent", StopAgent));
        }
        _ui.Gap();
        _ui.Line("For this session: works where no other agent runs (a bare sway or niri session); with KDE or", Ui.Dim, Ui.Small);
        _ui.Line("GNOME running, polkit refuses a second agent. Then try pkexec true from a terminal.", Ui.Dim, Ui.Small);
    }

    private void StartTestAgent()
    {
        _polkitTarget = Process.Start(new ProcessStartInfo("sleep", ["3600"]));
        StartAgent(_polkitTarget!.Id);
    }

    private void StartAgent(int? process) => Run("Polkit agent", async () =>
    {
        try
        {
            _polkit = await PolkitAgent.StartAsync(_app, _systemBus ?? throw new InvalidOperationException("No system bus."),
                new PolkitAgentOptions { Process = process });
        }
        catch
        {
            _polkitTarget?.Kill();
            _polkitTarget = null;
            throw;
        }
        _polkit.AuthenticationRequested += OnPolkitRequest;
        Log("Polkit agent registered.", Ui.Good);
    });

    private void StopAgent()
    {
        _polkit?.Dispose();
        _polkit = null;
        _polkitTarget?.Kill();
        _polkitTarget = null;
        Log("Polkit agent stopped.");
    }

    private void RunPkcheck()
    {
        if (_polkitTarget is null)
        {
            Log("pkcheck needs the helper-process agent; for a session agent run pkexec true in a terminal.");
            return;
        }
        Run("pkcheck", async () =>
        {
            using var check = Process.Start(new ProcessStartInfo("pkcheck",
                ["--action-id", "org.freedesktop.policykit.exec", "--process", _polkitTarget.Id.ToString(), "--allow-user-interaction"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            await check.WaitForExitAsync();
            Log(check.ExitCode == 0 ? "pkcheck: authorized." : $"pkcheck: not authorized (exit {check.ExitCode}).", check.ExitCode == 0 ? Ui.Good : Ui.Bad);
        });
    }

    private void OnPolkitRequest(PolkitRequest request)
    {
        int user = 0;
        string Who() => $"As {request.Users[user].RealName} ({request.Users[user].Name})";
        Prompt p = null!;
        p = new Prompt
        {
            Title = "Authentication required",
            Message = $"{request.Message}\n{Who()}",
            Masked = true,
            OkLabel = "Authenticate",
            OnOk = password => Run("Polkit", async () =>
            {
                if (await request.AuthenticateAsync(request.Users[user], password))
                {
                    Log("Authenticated.", Ui.Good);
                    CloseAsk();
                    return;
                }
                p.Text = "";
                p.Message = $"{request.Message}\n{Who()}\nThat password was not accepted. Try again.";
                p.MessageColor = Ui.Bad;
            }),
            OnCancel = () =>
            {
                request.Cancel();
                CloseAsk();
            },
        };
        if (request.Users.Count > 1)
        {
            p.Extra.Add(("Other user", () =>
            {
                user = (user + 1) % request.Users.Count;
                p.Message = $"{request.Message}\n{Who()}";
            }
            ));
        }
        request.Finished += () =>
        {
            if (_prompt == p)
                CloseAsk();
        };
        Log($"Polkit asks: {request.ActionId}", Ui.Accent);
        Ask(p);
    }

    // ---- Notifications -----------------------------------------------------------------------

    private uint _lastNotification;

    private async Task WatchNotificationSignalsAsync()
    {
        const string Path = "/org/freedesktop/Notifications", Interface = "org.freedesktop.Notifications";
        MessageValueReader<(uint Id, string Value)> read = (m, _) =>
        {
            var r = m.GetBodyReader();
            return (r.ReadUInt32(), r.ReadString());
        };
        await _sessionBus!.WatchSignalAsync(_app, null, Path, Interface, "ActivationToken", read, e =>
        {
            if (e.Id != _lastNotification)
                return;
            _notificationEvents.Add($"ActivationToken {e.Value}");
            Log($"Got an activation token ({e.Value}); raising the lab with it.", Ui.Good);
            _app.Activate(_window, e.Value);
        });
        await _sessionBus!.WatchSignalAsync(_app, null, Path, Interface, "ActionInvoked", read, e =>
        {
            if (e.Id != _lastNotification)
                return;
            _notificationEvents.Add($"ActionInvoked {e.Value}");
            Log($"Action invoked: {e.Value}.", Ui.Good);
        });
    }

    private void Notifications()
    {
        _ui.Heading("Notification actions and activation tokens");
        _ui.Line("Sends a notification to the running notification server (a demo shell, or your desktop's).", Ui.Dim, Ui.Small);
        _ui.Line("Click it: a server that supports activation tokens sends one before the action, and the lab", Ui.Dim, Ui.Small);
        _ui.Line("uses it to raise its window. Minimize or cover the lab first to see that.", Ui.Dim, Ui.Small);
        _ui.Gap(4);
        _ui.Buttons(("Send test notification", SendNotification));
        foreach (var line in _notificationEvents.TakeLast(6))
            _ui.Line(line, Ui.Text, Ui.Small);
        if (_notificationEvents.Count > 0)
        {
            bool tokenFirst = _notificationEvents.FindIndex(e => e.StartsWith("ActivationToken")) is int t and >= 0
                && t < _notificationEvents.FindIndex(e => e.StartsWith("ActionInvoked"));
            _ui.Line(tokenFirst ? "Token arrived before the action: correct." : "No token before the action (the server does not send one).",
                tokenFirst ? Ui.Good : Ui.Dim);
        }
    }

    private void SendNotification()
    {
        if (_sessionBus is null)
        {
            Log("No session bus.", Ui.Bad);
            return;
        }
        _notificationEvents.Clear();
        Run("Notify", async () =>
        {
            _lastNotification = await _sessionBus.CallAsync("org.freedesktop.Notifications", "/org/freedesktop/Notifications", "org.freedesktop.Notifications", "Notify",
                (m, _) => m.GetBodyReader().ReadUInt32(), (ref MessageWriter w) =>
                {
                    w.WriteString("oops-lab");
                    w.WriteUInt32(0);
                    w.WriteString("dialog-information");
                    w.WriteString("oops-lab");
                    w.WriteString("Click to bring the lab to the front.");
                    w.WriteArray(new[] { "default", "Open", "open", "Open the lab" });
                    w.WriteDictionary(new Dictionary<string, VariantValue> { ["desktop-entry"] = "oops-lab" });
                    w.WriteInt32(-1);
                }, "susssasa{sv}i");
            Log($"Sent notification #{_lastNotification}.");
        });
    }

    // ---- Workspaces --------------------------------------------------------------------------

    private void Workspaces()
    {
        _ui.Heading("Workspaces (ext-workspace)");
        if (_app.Workspaces is not { } workspaces)
        {
            _ui.Line("This compositor offers no ext-workspace (sway does not; niri does).", Ui.Dim);
            return;
        }
        foreach (var w in workspaces)
        {
            string detail = string.Join("  ", new[] { w.IsActive ? "active" : null, w.IsUrgent ? "urgent" : null, w.IsHidden ? "hidden" : null,
                w.Outputs.Count > 0 ? string.Join(",", w.Outputs.Select(o => o.Name)) : null, w.Id.Length > 0 ? $"id {w.Id}" : null }.OfType<string>());
            var buttons = new List<(string, Action)>();
            if (w.CanActivate && !w.IsActive)
                buttons.Add(("Show", w.Activate));
            if (w.CanRemove)
                buttons.Add(("Remove", w.Remove));
            _ui.Item(w.Name.Length > 0 ? w.Name : "(unnamed)", detail, buttons.ToArray());
        }
        _ui.Buttons(("New workspace", () => Ask(new Prompt
        {
            Title = "New workspace",
            Message = "Name:",
            OnOk = name =>
            {
                CloseAsk();
                if (!_app.CreateWorkspace(name))
                    Log("The compositor does not let clients create workspaces here.", Ui.Bad);
            },
            OnCancel = CloseAsk,
        })));
    }

    // ---- Inhibitors --------------------------------------------------------------------------

    private void Inhibit()
    {
        _ui.Heading("Idle and shortcut inhibitors");
        _ui.Toggle("Keep the screen awake while the lab is visible", _window.InhibitIdle, () => _window.InhibitIdle = !_window.InhibitIdle,
            _app.CanInhibitIdle ? null : "(not supported)");
        _ui.Toggle("Watch for 10 seconds without input", _idleWatch is not null, ToggleIdleWatch,
            _idleWatch is null ? null : _idleNow ? "idle now" : "active");
        _ui.Line("With the first toggle on, the watch should not report idle while the lab is visible (unless the", Ui.Dim, Ui.Small);
        _ui.Line("compositor ignores inhibitors for idle notifications).", Ui.Dim, Ui.Small);
        _ui.Gap();
        _ui.Toggle("Send the compositor's shortcuts to the lab", _window.InhibitShortcuts, () => _window.InhibitShortcuts = !_window.InhibitShortcuts,
            _app.CanInhibitShortcuts ? (_window.ShortcutsInhibited ? "in effect" : _window.InhibitShortcuts ? "requested, not in effect" : null) : "(not supported)");
        _ui.Line("While in effect, a compositor binding (e.g. Super+Enter) should not fire while the lab has focus.", Ui.Dim, Ui.Small);
    }

    private void ToggleIdleWatch()
    {
        if (_idleWatch is not null)
        {
            _idleWatch.Dispose();
            _idleWatch = null;
            return;
        }
        if (!_app.Idle.IsSupported)
        {
            Log("No ext-idle-notify here.", Ui.Bad);
            return;
        }
        _idleWatch = _app.Idle.Watch(TimeSpan.FromSeconds(10), () =>
        {
            _idleNow = true;
            Log("Idle for 10 s.", Ui.Accent);
        }, () =>
        {
            _idleNow = false;
            Log("Active again.");
        });
    }

    // ---- Lock --------------------------------------------------------------------------------

    private void Lock()
    {
        _ui.Heading("Lock screen (ext-session-lock + PAM)");
        _ui.Line($"Locks this compositor's session; unlock with {Environment.UserName}'s password.", Ui.Dim, Ui.Small);
        _ui.Line("Use a nested compositor: a lock that cannot be undone stays locked by design.", Ui.Bad, Ui.Small);
        _ui.Gap(4);
        _ui.Buttons(("Lock now", () => Ask(new Prompt
        {
            Title = "Lock this session?",
            Message = $"Unlocking needs the password of {Environment.UserName}. Only do this in a nested compositor.",
            HasField = false,
            OkLabel = "Lock",
            OnOk = _ =>
            {
                CloseAsk();
                Run("Lock", () => LockScreen.LockAsync(_app, Log));
            },
            OnCancel = CloseAsk,
        })));
    }

    // ---- Audio and media ---------------------------------------------------------------------

    private void Audio()
    {
        _ui.Heading("Volume and media");
        if (_audio?.DefaultSink is { } sink)
        {
            _ui.Line($"{sink.Description}: {Math.Round(sink.Volume * 100)}%{(sink.Muted ? ", muted" : "")}");
            _ui.Buttons(("−5%", () => _audio.SetVolume(sink, Math.Max(0, sink.Volume - 0.05))),
                ("+5%", () => _audio.SetVolume(sink, Math.Min(1, sink.Volume + 0.05))),
                (sink.Muted ? "Unmute" : "Mute", () => _audio.SetMuted(sink, !sink.Muted)));
        }
        else
            _ui.Line("No audio output.", Ui.Dim);
        _ui.Gap();
        if (_media?.Active is { } player)
        {
            _ui.Line($"{player.Identity}: {player.Status}");
            _ui.Line($"{player.Title}{(player.Artist.Length > 0 ? " — " + player.Artist : "")}", Ui.Dim);
            _ui.Buttons(("Previous", () => Run("Previous", player.PreviousAsync)), (player.Status == PlaybackStatus.Playing ? "Pause" : "Play", () => Run("Play", player.PlayPauseAsync)),
                ("Next", () => Run("Next", player.NextAsync)));
        }
        else
            _ui.Line("No media player running (start one that supports MPRIS).", Ui.Dim);
    }

    // ---- Battery -----------------------------------------------------------------------------

    private void Battery()
    {
        _ui.Heading("Battery and power (UPower)");
        if (_power is null)
        {
            _ui.Line("UPower is not available.", Ui.Bad);
            return;
        }
        _ui.Line($"On battery: {YesNo(_power.OnBattery)}");
        if (_power.DisplayDevice is { IsPresent: true } display)
            _ui.Line($"Battery: {display.Percentage:0}%, {display.State}");
        else
            _ui.Line("No battery (a desktop).", Ui.Dim);
        foreach (var device in _power.Devices)
            _ui.Item($"{device.Kind} {device.Model}".Trim(), $"{device.Percentage:0}%  {device.State}{(device.IsPresent ? "" : "  (not present)")}");
    }
}
