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

## Hot reload
- [ ] `dotnet watch run --project samples/HelloSkia`: change the clear color in `Scene.Draw` → window redraws live.
- [ ] Make a rude edit (e.g. change a method signature): `dotnet watch` restarts; the window comes back cleanly.
- [ ] Same with oops95: after a restart there are no leftover exclusive zones or stuck layers; the control socket is reused.

## Results

| Date | Compositor | GPU | Notes |
|---|---|---|---|
