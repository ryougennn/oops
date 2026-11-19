# Manual tests

Things headless sway in CI cannot cover. Run each on **niri** and on **sway** (real session), with a
Release build: `demo/oops95/build.sh publish`, then `demo/oops95/out/oops95`.

Record results (date, compositor + version, GPU) at the bottom.

## GPU and HiDPI
- [ ] `samples/HelloSkia` prints "Rendering on the GPU (EGL)" on real hardware and looks identical with `--shm`.
- [ ] HiDPI: set the output scale to 1, 1.5 and 2 (`swaymsg output X scale 1.5`; niri `scale 1.5` in config). HelloSkia text and oops95 chrome stay crisp; no blurry upscaling; sizes stay the same in logical pixels.
- [ ] Moving a window between outputs with different scales re-renders at the new scale.

## oops95 session
- [ ] Taskbar reserves space on every output; plugging a monitor in/out adds/removes desktop + taskbar.
- [ ] Start menu: click opens it; hovering cascades submenus; outside click closes it.
- [ ] Super (sway) / Mod+Space (niri): `--toggle-start` opens it with keyboard navigation (arrows, Enter, Escape).
- [ ] Launching an app from the Start menu focuses the new window (activation token).
- [ ] Task buttons follow real windows: title changes, pressed = focused, click minimizes/restores, middle click closes; minimize animation targets the button (where the compositor animates).
- [ ] Tray shows a real StatusNotifierItem app (e.g. `nm-applet --indicator`, `blueman-applet`, Steam, Discord): icon, left click activates, right click opens its menu in oops95 style, menu actions work.
- [ ] If another bar (waybar) already owns the StatusNotifierWatcher, oops95 still shows the items (host-only mode).
- [ ] `notify-send -A ok=OK "Hi" "There"` shows a toast; clicking OK prints `ok`; the toast expires; clicks in the gap between toasts reach the window below.
- [ ] Run dialog: typing (including compose, e.g. Compose ' e → é), key repeat, history with Up/Down, Enter launches.
- [ ] Shut Down dialog dims every output; Log out/Restart/Shut down only print the command with `--dry-run` (do not test the real commands carelessly).
- [ ] System Properties: tiled (sway) uses server-side decorations; floating (`swaymsg floating enable`, or on niri) shows the oops95 chrome; drag title moves, drag edges resize, min/max/close work. Performance tab shows frame times.
- [ ] Idle: with no animation and no hover, `strace -f -e trace=epoll_wait -p $(pidof oops95)` shows one wakeup per minute (clock).

## Lock screen (PAM)
- [ ] `oops95 --lock` locks every output; the right password unlocks; a wrong one shows the error and stays locked.
- [ ] Plug a monitor in while locked: it gets a lock surface too.
- [ ] `kill -9` oops95 while locked: the session **stays locked** (compositor fallback). Recover per compositor docs (e.g. restart a locker from another VT). Do this last.
- [ ] Optional: install `config/pam.d/oops95` to `/etc/pam.d/oops95` and lock again.
- [ ] System PAM (`pam_unix`) with a throwaway account: unprivileged processes can only check their own
  user's password, so run the check *as* that user (e.g. `su testuser -c '<probe>'` with a tiny program calling
  `PamAuthenticator.AuthenticateAsync(Environment.UserName, password)`): the right password gives `Success`, a
  wrong one `AuthenticationFailed` after PAM's delay. (Checked 2026-11-18 with a test account.) The automated
  tests use a private PAM config (`pam_start_confdir`) instead.

## oops-lab: services and protocols
`samples/oops-lab` has one page per check (Tab moves between pages; results go to its log). Build with
`dotnet run --project samples/oops-lab`, ideally inside a nested niri (it has every protocol below; sway
lacks workspaces and blur).
- [ ] **Wi-Fi**: join a WPA2 network with its password, reconnect later without one, switch Wi-Fi off and on. Press "Ask me for Wi-Fi passwords", change the network's password on the router (or connect with a wrong one): the lab asks again, marked as a retry.
- [ ] **Bluetooth**: discover, pair (press "Handle pairing codes" first: a keyboard asks you to type a code, a phone asks to compare one) and connect a mouse and headphones; the battery level appears where reported; forget them.
- [ ] **Polkit**: "Start agent for a helper process", then "Ask polkit": your password says authorized, a wrong one asks again, Cancel says not authorized. In a session without another agent (bare sway or niri), "Start agent for this session" and `pkexec true` in a terminal.
- [ ] **Notifications**: with a notification server that sends tokens (OopsOSX or oops95, or a desktop that does), cover the lab, send the test notification and click it: the log shows the token before the action and the lab comes to the front.
- [ ] **Workspaces** (niri): the list matches niri's, Show switches, New workspace either appears or reports that niri does not allow it.
- [ ] **Inhibit**: with "Keep the screen awake" on, the 10-second idle watch stays active while the lab is visible; off, it reports idle. With shortcuts sent to the lab, a compositor binding does nothing while the lab has focus.
- [ ] **Lock** (nested compositor only): locks every output; the right password unlocks, a wrong one says so.
- [ ] **Audio, Battery**: volume and mute follow the system; a playing MPRIS player can be paused and skipped; the battery matches the desktop's indicator.
- [ ] **greetd**: with greetd configured to run `cage -s -- oops-lab --greeter --session niri-session` (or a sway config that execs it) as its default session, log in; a wrong password asks again; the session starts.

## Crossbar (sway kiosk)
- [ ] `sway -c demo/crossbar/config/sway.conf` on a TV or monitor from the couch: labels are readable, the waves and glass animate smoothly, ticks play on every move without lag, Super+Escape brings the bar over a fullscreen game or video and Escape returns to it, closing the last app comes home, the Running list switches between several apps.

## Launcher on other distributions
- [ ] On Debian/Ubuntu (Snap apps), Arch, NixOS and a Flatpak-heavy system: the OopsOSX search finds installed apps by name, by their English name in another locale, and their actions; Snap and Flatpak apps launch; a Terminal=true app opens in the system's terminal; Computer/Home in the Go menu open the default file manager; installing an app makes it searchable without restarting.

## Volume and media (OopsOSX)
- [ ] The speaker in the menu bar follows volume changes made elsewhere (e.g. `wpctl set-volume @DEFAULT_AUDIO_SINK@ 30%`); dragging its slider and scrolling on it change the real volume; muting shows the crossed speaker.
- [ ] Start a real player (a browser video, Spotify, mpv with MPRIS): the note appears; its menu shows the track and Play/Pause/Next work.

## Battery (UPower)
- [ ] On a laptop: the tray battery shows the charge; plugging/unplugging AC toggles the bolt; the tooltip shows the percentage and time left.
- [ ] A wireless mouse/keyboard with a battery appears in the tooltip with its level.

## Hot reload
- [ ] `dotnet watch run --project samples/HelloSkia`: change the clear color in `Scene.Draw` → window redraws live.
- [ ] Make a rude edit (e.g. change a method signature): `dotnet watch` restarts; the window comes back cleanly.
- [ ] Same with oops95: after a restart there are no leftover exclusive zones or stuck layers; the control socket is reused.

## Results

| Date | Compositor | GPU | Notes |
|---|---|---|---|
