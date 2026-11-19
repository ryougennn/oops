# Manual tests

Things headless sway in CI cannot cover. Run each on **niri** and on **sway** (real session), with Release
builds of the samples.

Record results (date, compositor + version, GPU) at the bottom.

## GPU and HiDPI
- [ ] `samples/HelloSkia` prints "Rendering on the GPU (EGL)" on real hardware and looks identical with `--shm`.
- [ ] HiDPI: set the output scale to 1, 1.5 and 2 (`swaymsg output X scale 1.5`; niri `scale 1.5` in config). HelloSkia and oops-lab text stay crisp; no blurry upscaling; sizes stay the same in logical pixels.
- [ ] Moving a window between outputs with different scales re-renders at the new scale.

## Lock screen (PAM)
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
- [ ] **Notifications**: with a notification server that sends tokens (a desktop that sends them), cover the lab, send the test notification and click it: the log shows the token before the action and the lab comes to the front.
- [ ] **Workspaces** (niri): the list matches niri's, Show switches, New workspace either appears or reports that niri does not allow it.
- [ ] **Inhibit**: with "Keep the screen awake" on, the 10-second idle watch stays active while the lab is visible; off, it reports idle. With shortcuts sent to the lab, a compositor binding does nothing while the lab has focus.
- [ ] **Lock** (nested compositor only): locks every output; the right password unlocks, a wrong one says so.
- [ ] **Audio, Battery**: volume and mute follow the system; a playing MPRIS player can be paused and skipped; the battery matches the desktop's indicator.
- [ ] **greetd**: with greetd configured to run `cage -s -- oops-lab --greeter --session niri-session` (or a sway config that execs it) as its default session, log in; a wrong password asks again; the session starts.

## Battery (UPower)
- [ ] On a laptop: the tray battery shows the charge; plugging/unplugging AC toggles the bolt; the tooltip shows the percentage and time left.
- [ ] A wireless mouse/keyboard with a battery appears in the tooltip with its level.

## Hot reload
- [ ] `dotnet watch run --project samples/HelloSkia`: change the clear color in `Scene.Draw` → window redraws live.
- [ ] Make a rude edit (e.g. change a method signature): `dotnet watch` restarts; the window comes back cleanly.
- [ ] Same with `samples/QuickBar`: after a restart there are no leftover exclusive zones or stuck layers; the IPC socket is reused.

## Results

| Date | Compositor | GPU | Notes |
|---|---|---|---|
