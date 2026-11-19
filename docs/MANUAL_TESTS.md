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

## System services (polkit, Wi-Fi, Bluetooth, greetd)
- [ ] polkit: in a session without another agent (e.g. sway), a `PolkitAgent` dialog appears for `pkexec true` and `systemctl restart` of a unit; the right password authorizes, a wrong one allows retry, Cancel denies.
- [ ] Wi-Fi: join a WPA2 network with its password through `NetworkService.ConnectAsync`, reconnect to it later without one, switch Wi-Fi off and on; the device's state follows.
- [ ] Bluetooth: discover, pair and connect a mouse and headphones with `BluetoothService`; the battery level appears where the device reports one; forget them again.
- [ ] greetd: a greeter using `GreetdClient` logs in with a real greetd (`greetd` with `default_session` set to the greeter), wrong passwords retry, and the chosen session starts.

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
