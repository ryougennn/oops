using System.Diagnostics;
using oopshell;
using oopshell.Services.Bluetooth;
using oopshell.Services.DBus;
using oopshell.Services.Media;
using oopshell.Services.Network;
using oopshell.Services.Polkit;
using Tmds.DBus.Protocol;

namespace OopsLab;

sealed partial class Lab
{
    // ---- Wi-Fi -------------------------------------------------------------------------------

    void Wifi()
    {
        ui.Heading("Wi-Fi (NetworkManager)");
        if (network is null)
        {
            ui.Line("NetworkManager is not available.", Ui.Bad);
            return;
        }
        ui.Line($"Connectivity: {network.Connectivity}    Primary: {network.PrimaryConnectionName ?? "none"} ({network.PrimaryConnectionType})");
        foreach (var device in network.Devices)
            ui.Line($"{device.Interface}: {device.Type}, {device.State}{(device.ConnectionName is { } n ? $" — {n}" : "")}", Ui.Dim, Ui.Small);
        ui.Gap(6);
        ui.Toggle("Wi-Fi", network.WifiEnabled, () => Run("Wi-Fi", () => network.SetWifiEnabledAsync(!network.WifiEnabled)),
            network.WifiHardwareEnabled ? null : "(hardware switch off)");
        ui.Buttons(("Scan", () => { if (network.Wifi is { } w) Run("Scan", () => network.ScanAsync(w)); }),
            ("Ask me for Wi-Fi passwords (secret agent)", () => Run("Secret agent", async () =>
            {
                await network.RegisterSecretAgentAsync();
                Log("Registered as a NetworkManager secret agent.", Ui.Good);
            })));
        if (network.Wifi is not { } wifi)
        {
            ui.Line("No Wi-Fi device.", Ui.Dim);
            return;
        }
        foreach (var ap in wifi.AccessPoints.Take(12))
        {
            string detail = $"{ap.Strength}%  {(ap.Frequency > 3000 ? "5 GHz" : "2.4 GHz")}  {(ap.Secured ? "secured" : "open")}{(ap.IsActive ? "  — connected" : "")}";
            if (ap.IsActive)
                ui.Item(ap.Ssid, detail, ("Disconnect", () => Run("Disconnect", () => network.DisconnectAsync(wifi))));
            else
                ui.Item(ap.Ssid, detail, ("Connect", () => ConnectWifi(wifi, ap)));
        }
    }

    void ConnectWifi(NetworkDevice wifi, AccessPoint ap)
    {
        if (!ap.Secured)
        {
            Run("Connect", () => network!.ConnectAsync(wifi, ap));
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
                Run("Connect", () => network!.ConnectAsync(wifi, ap, password.Length > 0 ? password : null));
            },
            OnCancel = CloseAsk,
        });
    }

    void OnSecretsRequested(NetworkSecretRequest request)
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
            if (prompt == p)
                CloseAsk();
        };
        Ask(p);
    }

    // ---- Bluetooth ---------------------------------------------------------------------------

    void Bluetooth()
    {
        ui.Heading("Bluetooth (BlueZ)");
        if (bluetooth is null)
        {
            ui.Line("BlueZ is not available.", Ui.Bad);
            return;
        }
        if (bluetooth.Adapter is not { } adapter)
        {
            ui.Line("No adapter.", Ui.Dim);
            return;
        }
        ui.Line($"{adapter.Name} ({adapter.Address})");
        ui.Toggle("Powered", adapter.Powered, () => Run("Power", () => bluetooth.SetPoweredAsync(adapter, !adapter.Powered)));
        ui.Toggle("Discovering", adapter.Discovering, () => Run("Discovery", () =>
            adapter.Discovering ? bluetooth.StopDiscoveryAsync(adapter) : bluetooth.StartDiscoveryAsync(adapter)));
        ui.Buttons(("Handle pairing codes (pairing agent)", () => Run("Pairing agent", async () =>
        {
            await bluetooth.RegisterPairingAgentAsync();
            Log("Registered as BlueZ's default pairing agent.", Ui.Good);
        })));
        foreach (var device in bluetooth.Devices.Take(12))
        {
            string detail = $"{device.Address}  {device.Icon}" + (device.Battery is { } b ? $"  battery {b}%" : "")
                + (device.Connected ? "  — connected" : device.Paired ? "  — paired" : "") + (device.Rssi is { } r ? $"  {r} dBm" : "");
            var buttons = new List<(string, Action)>();
            if (!device.Paired)
                buttons.Add(("Pair", () => Run($"Pair {device.Name}", async () => { await bluetooth.PairAsync(device); Log($"Paired {device.Name}.", Ui.Good); })));
            else if (!device.Connected)
                buttons.Add(("Connect", () => Run($"Connect {device.Name}", () => bluetooth.ConnectAsync(device))));
            if (device.Connected)
                buttons.Add(("Disconnect", () => Run($"Disconnect {device.Name}", () => bluetooth.DisconnectAsync(device))));
            if (device.Paired)
                buttons.Add(("Forget", () => Run($"Forget {device.Name}", () => bluetooth.ForgetAsync(device))));
            ui.Item(device.Name, detail, buttons.ToArray());
        }
    }

    void OnPairingRequested(BluetoothPairingRequest request)
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
            if (prompt == p)
                CloseAsk();
        };
        Ask(p);
    }

    // ---- Polkit ------------------------------------------------------------------------------

    void Polkit()
    {
        ui.Heading("Polkit authentication agent");
        ui.Line("Safe check: an agent for one helper process, then pkcheck asks polkit whether that process may run", Ui.Dim, Ui.Small);
        ui.Line("programs as root. A correct password says \"authorized\"; nothing is run.", Ui.Dim, Ui.Small);
        ui.Gap(4);
        ui.Line(polkit is null ? "Agent: not running" : polkitTarget is null ? "Agent: this session" : $"Agent: for helper process {polkitTarget.Id}",
            polkit is null ? Ui.Dim : Ui.Good);
        if (polkit is null)
        {
            ui.Buttons(("Start agent for a helper process", StartTestAgent),
                ("Start agent for this session", () => StartAgent(null)));
        }
        else
        {
            ui.Buttons(("Ask polkit (pkcheck)", RunPkcheck), ("Stop agent", StopAgent));
        }
        ui.Gap();
        ui.Line("For this session: works where no other agent runs (a bare sway or niri session); with KDE or", Ui.Dim, Ui.Small);
        ui.Line("GNOME running, polkit refuses a second agent. Then try pkexec true from a terminal.", Ui.Dim, Ui.Small);
    }

    void StartTestAgent()
    {
        polkitTarget = Process.Start(new ProcessStartInfo("sleep", ["3600"]));
        StartAgent(polkitTarget!.Id);
    }

    void StartAgent(int? process) => Run("Polkit agent", async () =>
    {
        try
        {
            polkit = await PolkitAgent.StartAsync(app, systemBus ?? throw new InvalidOperationException("No system bus."),
                new PolkitAgentOptions { Process = process });
        }
        catch
        {
            polkitTarget?.Kill();
            polkitTarget = null;
            throw;
        }
        polkit.AuthenticationRequested += OnPolkitRequest;
        Log("Polkit agent registered.", Ui.Good);
    });

    void StopAgent()
    {
        polkit?.Dispose();
        polkit = null;
        polkitTarget?.Kill();
        polkitTarget = null;
        Log("Polkit agent stopped.");
    }

    void RunPkcheck()
    {
        if (polkitTarget is null)
        {
            Log("pkcheck needs the helper-process agent; for a session agent run pkexec true in a terminal.");
            return;
        }
        Run("pkcheck", async () =>
        {
            using var check = Process.Start(new ProcessStartInfo("pkcheck",
                ["--action-id", "org.freedesktop.policykit.exec", "--process", polkitTarget.Id.ToString(), "--allow-user-interaction"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            await check.WaitForExitAsync();
            Log(check.ExitCode == 0 ? "pkcheck: authorized." : $"pkcheck: not authorized (exit {check.ExitCode}).", check.ExitCode == 0 ? Ui.Good : Ui.Bad);
        });
    }

    void OnPolkitRequest(PolkitRequest request)
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
            }));
        }
        request.Finished += () =>
        {
            if (prompt == p)
                CloseAsk();
        };
        Log($"Polkit asks: {request.ActionId}", Ui.Accent);
        Ask(p);
    }

    // ---- Notifications -----------------------------------------------------------------------

    uint lastNotification;

    async Task WatchNotificationSignalsAsync()
    {
        const string Path = "/org/freedesktop/Notifications", Interface = "org.freedesktop.Notifications";
        MessageValueReader<(uint Id, string Value)> read = (m, _) =>
        {
            var r = m.GetBodyReader();
            return (r.ReadUInt32(), r.ReadString());
        };
        await sessionBus!.WatchSignalAsync(app, null, Path, Interface, "ActivationToken", read, e =>
        {
            if (e.Id != lastNotification)
                return;
            notificationEvents.Add($"ActivationToken {e.Value}");
            Log($"Got an activation token ({e.Value}); raising the lab with it.", Ui.Good);
            app.Activate(window, e.Value);
        });
        await sessionBus!.WatchSignalAsync(app, null, Path, Interface, "ActionInvoked", read, e =>
        {
            if (e.Id != lastNotification)
                return;
            notificationEvents.Add($"ActionInvoked {e.Value}");
            Log($"Action invoked: {e.Value}.", Ui.Good);
        });
    }

    void Notifications()
    {
        ui.Heading("Notification actions and activation tokens");
        ui.Line("Sends a notification to the running notification server (a demo shell, or your desktop's).", Ui.Dim, Ui.Small);
        ui.Line("Click it: a server that supports activation tokens sends one before the action, and the lab", Ui.Dim, Ui.Small);
        ui.Line("uses it to raise its window. Minimize or cover the lab first to see that.", Ui.Dim, Ui.Small);
        ui.Gap(4);
        ui.Buttons(("Send test notification", SendNotification));
        foreach (var line in notificationEvents.TakeLast(6))
            ui.Line(line, Ui.Text, Ui.Small);
        if (notificationEvents.Count > 0)
        {
            bool tokenFirst = notificationEvents.FindIndex(e => e.StartsWith("ActivationToken")) is int t and >= 0
                && t < notificationEvents.FindIndex(e => e.StartsWith("ActionInvoked"));
            ui.Line(tokenFirst ? "Token arrived before the action: correct." : "No token before the action (the server does not send one).",
                tokenFirst ? Ui.Good : Ui.Dim);
        }
    }

    void SendNotification()
    {
        if (sessionBus is null)
        {
            Log("No session bus.", Ui.Bad);
            return;
        }
        notificationEvents.Clear();
        Run("Notify", async () =>
        {
            lastNotification = await sessionBus.CallAsync("org.freedesktop.Notifications", "/org/freedesktop/Notifications", "org.freedesktop.Notifications", "Notify",
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
            Log($"Sent notification #{lastNotification}.");
        });
    }

    // ---- Workspaces --------------------------------------------------------------------------

    void Workspaces()
    {
        ui.Heading("Workspaces (ext-workspace)");
        if (app.Workspaces is not { } workspaces)
        {
            ui.Line("This compositor offers no ext-workspace (sway does not; niri does).", Ui.Dim);
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
            ui.Item(w.Name.Length > 0 ? w.Name : "(unnamed)", detail, buttons.ToArray());
        }
        ui.Buttons(("New workspace", () => Ask(new Prompt
        {
            Title = "New workspace",
            Message = "Name:",
            OnOk = name =>
            {
                CloseAsk();
                if (!app.CreateWorkspace(name))
                    Log("The compositor does not let clients create workspaces here.", Ui.Bad);
            },
            OnCancel = CloseAsk,
        })));
    }

    // ---- Inhibitors --------------------------------------------------------------------------

    void Inhibit()
    {
        ui.Heading("Idle and shortcut inhibitors");
        ui.Toggle("Keep the screen awake while the lab is visible", window.InhibitIdle, () => window.InhibitIdle = !window.InhibitIdle,
            app.CanInhibitIdle ? null : "(not supported)");
        ui.Toggle("Watch for 10 seconds without input", idleWatch is not null, ToggleIdleWatch,
            idleWatch is null ? null : idleNow ? "idle now" : "active");
        ui.Line("With the first toggle on, the watch should not report idle while the lab is visible (unless the", Ui.Dim, Ui.Small);
        ui.Line("compositor ignores inhibitors for idle notifications).", Ui.Dim, Ui.Small);
        ui.Gap();
        ui.Toggle("Send the compositor's shortcuts to the lab", window.InhibitShortcuts, () => window.InhibitShortcuts = !window.InhibitShortcuts,
            app.CanInhibitShortcuts ? (window.ShortcutsInhibited ? "in effect" : window.InhibitShortcuts ? "requested, not in effect" : null) : "(not supported)");
        ui.Line("While in effect, a compositor binding (e.g. Super+Enter) should not fire while the lab has focus.", Ui.Dim, Ui.Small);
    }

    void ToggleIdleWatch()
    {
        if (idleWatch is not null)
        {
            idleWatch.Dispose();
            idleWatch = null;
            return;
        }
        if (!app.Idle.IsSupported)
        {
            Log("No ext-idle-notify here.", Ui.Bad);
            return;
        }
        idleWatch = app.Idle.Watch(TimeSpan.FromSeconds(10), () =>
        {
            idleNow = true;
            Log("Idle for 10 s.", Ui.Accent);
        }, () =>
        {
            idleNow = false;
            Log("Active again.");
        });
    }

    // ---- Lock --------------------------------------------------------------------------------

    void Lock()
    {
        ui.Heading("Lock screen (ext-session-lock + PAM)");
        ui.Line($"Locks this compositor's session; unlock with {Environment.UserName}'s password.", Ui.Dim, Ui.Small);
        ui.Line("Use a nested compositor: a lock that cannot be undone stays locked by design.", Ui.Bad, Ui.Small);
        ui.Gap(4);
        ui.Buttons(("Lock now", () => Ask(new Prompt
        {
            Title = "Lock this session?",
            Message = $"Unlocking needs the password of {Environment.UserName}. Only do this in a nested compositor.",
            HasField = false,
            OkLabel = "Lock",
            OnOk = _ =>
            {
                CloseAsk();
                Run("Lock", () => LockScreen.LockAsync(app, Log));
            },
            OnCancel = CloseAsk,
        })));
    }

    // ---- Audio and media ---------------------------------------------------------------------

    void Audio()
    {
        ui.Heading("Volume and media");
        if (audio?.DefaultSink is { } sink)
        {
            ui.Line($"{sink.Description}: {Math.Round(sink.Volume * 100)}%{(sink.Muted ? ", muted" : "")}");
            ui.Buttons(("−5%", () => audio.SetVolume(sink, Math.Max(0, sink.Volume - 0.05))),
                ("+5%", () => audio.SetVolume(sink, Math.Min(1, sink.Volume + 0.05))),
                (sink.Muted ? "Unmute" : "Mute", () => audio.SetMuted(sink, !sink.Muted)));
        }
        else
            ui.Line("No audio output.", Ui.Dim);
        ui.Gap();
        if (media?.Active is { } player)
        {
            ui.Line($"{player.Identity}: {player.Status}");
            ui.Line($"{player.Title}{(player.Artist.Length > 0 ? " — " + player.Artist : "")}", Ui.Dim);
            ui.Buttons(("Previous", () => Run("Previous", player.PreviousAsync)), (player.Status == PlaybackStatus.Playing ? "Pause" : "Play", () => Run("Play", player.PlayPauseAsync)),
                ("Next", () => Run("Next", player.NextAsync)));
        }
        else
            ui.Line("No media player running (start one that supports MPRIS).", Ui.Dim);
    }

    // ---- Battery -----------------------------------------------------------------------------

    void Battery()
    {
        ui.Heading("Battery and power (UPower)");
        if (power is null)
        {
            ui.Line("UPower is not available.", Ui.Bad);
            return;
        }
        ui.Line($"On battery: {YesNo(power.OnBattery)}");
        if (power.DisplayDevice is { IsPresent: true } display)
            ui.Line($"Battery: {display.Percentage:0}%, {display.State}");
        else
            ui.Line("No battery (a desktop).", Ui.Dim);
        foreach (var device in power.Devices)
            ui.Item($"{device.Kind} {device.Model}".Trim(), $"{device.Percentage:0}%  {device.State}{(device.IsPresent ? "" : "  (not present)")}");
    }
}
