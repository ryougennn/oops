# Status

Hand-off document. Updated at the end of each milestone.

## How to build and test

```sh
dotnet build oopshell.slnx
dotnet test --project tests/oopshell.Scanner.Tests      # golden + diagnostics
dotnet test --project tests/oopshell.Tests              # event loop, xkb (no compositor needed)
dotnet test --project tests/oopshell.IntegrationTests   # headless sway; skips if sway/grim are missing
tests/update-goldens.sh                                  # regenerate goldens after an intended change
tools/idle-check.sh <aot-binary> 10                      # counts event-loop wakeups while idle
```
Integration prerequisites: `dnf install sway grim` (or `apt install sway grim`); `strace` for idle checks.

## Milestones

| # | State | Notes |
|---|---|---|
| M0 | done | `oops-info` (AOT, 1.7 MB) lists the same 50 globals/versions as `wayland-info` on headless sway, plus outputs and seats. Scanner golden tests for core + xdg-shell + layer-shell, diagnostics OOPS001/OOPS002, cross-assembly compile test. |
| M1 | done | Event loop (epoll, eventfd, timerfd, fd watches, SynchronizationContext, SIGINT/SIGTERM), `ShmSwapchain`, `Toplevel`, xdg-decoration. `HelloWindow` animates only while the pointer is over it (or with `--animate`); golden screenshot test; idle AOT `HelloWindow`: **0 epoll wakeups in 10 s**. |
| M2 | done | Outputs with hotplug (`ForEachOutput`), `LayerSurface`, input/opaque regions. `HelloBar` sample. Tests: exclusive zone shrinks toplevels, hotplug create/unplug, empty input region is click-through. |
| M3 | done | Pointer (frame grouping, value120 incl. pre-v8 discrete), keyboard (xkb keymap via fd, compose from locale, timerfd key repeat that stops on focus loss/release), touch, cursor-shape + optional libwayland-cursor fallback. Virtual pointer/keyboard test helper uses the generated bindings. Tests: enter/motion/button, scroll coalescing, text + `é` via compose, repeat. |
| M4 | done | Integer + fractional scale (viewporter): buffer sizes tested at 1.5 and 2. Buffer age + `DamageHistory`; partial damage verified on the wire with a WAYLAND_DEBUG trace test. presentation-time (`Surface.Presented`). `oopshell.Egl` (one EGLDisplay + shared context per app, swap interval 0, buffer age, swap-with-damage, `GetProcAddress`). `oopshell.Skia` (`SkiaSurface`: GPU via EGL, SHM fallback, canvas clipped to the age-based repair area). GPU and SHM render identically (max channel diff ≤ 2) against the same golden, on Mesa llvmpipe inside pixman sway. `HelloSkia` AOT: 2.2 MB + libSkiaSharp.so, zero warnings, renders on the GPU path. |

| M5 | done | Popups from toplevels, layer surfaces (`get_popup`) and popups (nested), explicit grabs (serial of the latest button/key/touch), `Reposition` (xdg_popup v3), `Dismissed`. `LaunchAsync` with xdg-activation token (`XDG_ACTIVATION_TOKEN` + `DESKTOP_STARTUP_ID`), detached via `setsid --fork`. Tests: nested grabbed popups dismissed by a click on another client, layer popup + reposition, launched child sees the token. |
| M6 | done | `app.Toplevels` (wlr-foreign-toplevel-management, ext-foreign-toplevel-list fallback), `app.LockAsync` (lock surfaces per output incl. hotplug while locked), `app.Idle.Watch`. Tests per §12.2. |
| M7 | done | `MetadataUpdateHandler` → `HotReloadCacheClear`/`HotReloaded` + invalidate all surfaces; optional `DevOverlay` flash. Verified with `dotnet watch` on `HelloSkia` in headless sway: changing the clear color redraws live ("Hot reload succeeded", screenshot changes). Rude-edit restart on niri: see MANUAL_TESTS.md (not run unattended; the live niri session was off limits). |

| M8 | done | `oopshell.Services.Desktop` (desktop entries across XDG dirs with id precedence, localized keys, Exec field codes and quoting, visibility rules, icon theme lookup with inheritance/scale/threshold, PNG before SVG), `oopshell.Services.DBus` (Tmds.DBus.Protocol, callbacks on the UI thread, `ExportedObject`), `oopshell.Services.Notifications` (server; tested with real `notify-send` on a private dbus-daemon: fields, hints, actions, replace-id, expiry), `oopshell.Services.Tray` (StatusNotifierWatcher or host-only, items with properties/pixmaps/tooltips, activate/scroll, dbusmenu model with mnemonics and clicks; tested with an in-test SNI item). `oopshell.Services.Power` (stretch, added after M10): UPower on the system bus, devices + display device + OnBattery, coarse `BatteryLevel` for peripherals, `Changed` only on real changes; tested against a fake UPower on a private bus and read live from a Logitech G602 (level-only). A real third-party tray app is a manual check. |

| M9 | done (headless-verified) | `demo/oops95`: separate solution consuming oopshell only as packages from `artifacts/packages` (`build.sh`). Desktop, taskbar (Start, tasks, tray, battery + tooltip, clock + tooltip), cascading Start menu (mouse + keyboard), notification toasts, Run dialog, Shut Down dialog (dithered dim, dry-run commands), lock screen (ext-session-lock + PAM; fake authenticator only in `-c Test`), System Properties (CSD with SSD fallback), screensaver (stretch, idle-notify), control socket. AOT publish: **0 warnings**, `oops95` 5.4 MB + libSkiaSharp.so 11 MB. **Cold start ≈ 90 ms** (to control socket, headless sway). **Idle: 1 epoll wakeup in 30 s** (the minute clock). Verified by scripted headless tours (screenshots) on sway; the live niri session was not touched. Real-session checks are in MANUAL_TESTS.md. |
| Post | done | `oopshell.Vulkan` (one device per app, Wayland swapchain per surface, mailbox, CPU-waited acquire + layout transitions so SkiaSharp needs no semaphores) and `SkiaBackend.Vulkan` (opt-in). Same golden as SHM/GL; resize and dst-read blend tests. Verified on RX 7900 XT (RADV) in a nested sway: HelloSkia `--vulkan`, oops95 `OOPS95_BACKEND=vulkan` incl. popups; AOT 0 warnings. Shader cache: measured, see docs/guide/skia.md (driver cache only; SkiaSharp has no persistent-cache hook). |
| OopsOSX | done (niri-verified) | `demo/oopsosx`: a second desktop to test the API before freezing it (see `docs/API_REVIEW.md`). Menu bar (logo, app and Window menus, sliding between menus via `Reposition`, tray/battery/clock/search extras), Dock (magnification, auto-hide, bounce, running indicators, context menus, kept apps saved), wallpaper and desktop icons, search overlay, app switcher (held modifier), notification bubbles, shut-down/restart/log-out alerts with a pulsing default button, lock screen with shake, About window with client-side decorations and shadow. All drawing procedural; no third-party assets. Library additions: `ShadowMargins` (D21), `FindByAppId` (D22), popup grab serial fix (D23). AOT: **0 warnings**, 5.7 MB; cold start ~70 ms; idle ~0 CPU. Verified by scripted tours in a private niri nested in headless sway, and by hand in a nested niri. |
| Review follow-up | done | From docs/API_REVIEW.md: `IpcServer` (core), `oopshell.Services.Pam`, `SvgRenderer` (librsvg), `oopshell.Services.Audio` (libpulse), `oopshell.Services.Media` (MPRIS); both demos switched over (oops95: IPC, PAM, SVG icons; OopsOSX also gets volume and now-playing menu extras). Fix: layer-surface state is committed at once (D29). Tests: IPC (sway), PAM (private `pam_start_confdir` service with `pam_exec`), SVG (pixels), audio (temporary null sink on the user's server; real outputs untouched), media (fake MPRIS player). System PAM checked by hand with a test account. Window previews not done: no compositor here supports toplevel capture. |
| Window capture | done (fixture-verified) | `ScreenCapture` (D30): windows and outputs via ext-image-copy-capture, ext handles paired with wlr windows. Tested against `tests/fixtures/capturewl` (tinywl on wlroots 0.20 with capture sources; built by `build.sh`, which unpacks wlroots-devel locally if needed): own pixels per window, paired lists, output capture, stop on close. OopsOSX Exposé (F3 / Mod+E) shows captured thumbnails there and app-icon tiles on niri. Found while testing: `IpcServer.Dispose` no longer throws when the runtime dir is already gone. |
| Crossbar | done | Third demo (`demo/crossbar`): a 10-foot, keyboard-driven kiosk shell for sway with a cross-shaped bar, SkSL wave background and glass tiles, synthesized interface sounds, fullscreen apps on their own workspaces, and the bar as an overlay over them. No library changes were needed. |
| Launcher | done | `oopshell.Services.Launcher` (D31): live `AppCatalog`, ranked `AppSearch` (cross-language, accent/width/kana-insensitive, initials, subsequence, actions, usage), `Terminal`, `AppLauncher`, `MimeDatabase` + `DefaultApps`. Desktop entries gained untranslated names/keywords, MIME types, `DBusActivatable`, desktop actions; key files tolerate BOMs and indentation. Both demos use it (OopsOSX search lists actions). Tested on distro-shaped fixtures and this machine's real catalog. |
| M10 | done | Guides in `docs/guide/` (getting started, rendering paths incl. Vulkan example, Skia, hot reload, own protocols, AOT). `docs/MANUAL_TESTS.md`. Root README. |

### API review (M10)
Public surface kept small; items not used by the samples or demo were kept deliberately: `OopsApp.TryGet/Bind/Globals` (escape hatches the spec asks for), `RequestActivationTokenAsync`/`Activate` (activation for apps that receive a token), `DamageHistory` (custom renderers implementing buffer age), `IdleMonitor.Watch(ignoreInhibitors)`. `tools/ci.sh` runs the full build/test/AOT check.

### Library bugs the demo found (fixed, with regression tests)
- EGL windows created before the first configure presented a 1×1 first frame (black popups via the viewport) → EGL surfaces are created lazily; Skia sets up GL with a surfaceless current context.
- The shared `GRContext` kept stale GL state across EGL surfaces → reset per frame.
- Swapchain images with INPUT_ATTACHMENT usage (still true on SkiaSharp 4.157) made SkiaSharp's Vulkan backend segfault on blend modes that read the destination (Multiply) → usage flag dropped.

## Deviations from the spec (and why)

- **Name** is `oopshell` (nuget `oops` is taken), see DECISIONS D1.
- **`ShmSwapchain` needs `maxBuffers >= 2`.** wlroots keeps the current buffer until another one is attached, so one buffer can never be released (found by a test).
- **Swapchain tests live in the integration test project**, because `wl_buffer.release` needs a real compositor.
- **`xdg-output-unstable-v1` vendored** in addition to §5 (exact logical output geometry).
- **D-Bus I/O is not on `WatchFd`.** Tmds.DBus.Protocol is AOT-clean (0 warnings) so §10 says use it; it does its own async socket I/O, so callbacks are marshalled to the UI thread through `app.SynchronizationContext` instead of the bus fd being registered with the loop. No idle wakeups either way.
- **`PublishAot` is Release-only in samples**: set unconditionally it disables hot reload in Debug builds (see docs/guide/hot-reload.md).
- **`Seat.LastSerial` only tracks button/key/touch serials**, not enter/leave/modifiers: a click that moves keyboard focus is followed by `wl_keyboard.enter`, whose serial made popup grabs fail.
- **Sway grants CSD only to floating windows**; tiled toplevels get server-side mode. `Toplevel.Decoration` reports what the compositor chose.

## Owner action items

- **Update the .NET SDK** (`sudo dnf update dotnet-sdk-10.0`). The installed 10.0.113 ships ILCompiler 10.0.13 with advisory GHSA-p3jp-9v83-76w9; the NuGet audit warning is suppressed in `Directory.Build.props` (DECISIONS D11) only so builds work. Remove the suppression after updating.
- Reserve the `oopshell` package ID on nuget.org.
- Pick minimum sway/niri versions (§15); current assumption: whatever Fedora 44 ships (sway 1.11, niri 26.04).

## Profiling (2026-11-18)

`samples/oops-stress` drives one subsystem hard and prints CPU, allocations, GCs, RSS and fds; `tools/stress.sh <binary> <scenario>` runs it in a throwaway headless sway (GPU renderer) with its own session bus. AOT builds, RX 7900 XT, 800x600 headless output, 4 surfaces at 60 fps unless noted.

| Scenario | Result |
|---|---|
| `frames raw` (ShmSwapchain only) | 28 µs CPU/frame, 105 B/frame allocated (the per-frame `WlCallback`), no GCs |
| `frames shm/gl/vulkan`, clear only (library overhead) | 40 / 92 / 132 µs CPU/frame, 105 B/frame on every path |
| same with a text-heavy scene | 200 / 270 / 290 µs; the 5.4 KB/frame is the scene's strings and SkiaSharp text calls |
| resize storm (300 sizes) on each backend | keeps 60 fps, no errors |
| `input` (virtual-pointer flood) | ~590k motion events/s, ~1 µs and 0 B per event |
| `churn` (toplevel + layer + popup + 3 Skia surfaces, create/draw/destroy) | ~1.1 ms/round; RSS levels off (same after 1000 and 5000 rounds), heap and fds flat |
| `outputs` (hotplug with a bar per output) | bars follow outputs, no growth |
| `notify` (NotificationServer, 20k notifications) | ~3.5k/s incl. the client in the same process; heap flat once expired |

RSS grows by up to ~20 MB under steady allocation before it levels off: that is the GC's default gen0 budget, not a leak (with `DOTNET_GCgen0size=100000`, i.e. 1 MB in hex, it stays flat). NativeAOT does not take this setting from the project file; set the environment variable if footprint matters more than GC frequency.

oops95 under load (headless sway, GL): idle 0% CPU and 0 wakeups; 40 windows retitled 20×/s: 2.4% CPU (was 10.4%, see below); 1000 Hz pointer sweep over the taskbar: +0.4%; cold start to control socket ~55 ms.

Found and fixed by profiling:
- oops95 drew pixel-art icons as one rect per pixel (~256 draw calls per icon): sprites are now rasterized once (4x less CPU per taskbar repaint). Theme colors and the dither shader are cached instead of re-parsed/re-created per draw.
- oops95 kept every notification's buttons forever (`Toasts.buttons` was never pruned): a leak of one entry per notification, found with `dotnet-gcdump` diffs.
- oops95 task buttons narrower than their icon drew over their neighbours: button content is clipped.
- `IconTheme.Find` stat()ed every theme directory per lookup (~2 ms/icon, a ~200 ms stall the first time the Programs menu opened): directory listings are read once (9x faster).

SkiaSharp 4.157 (moved from 3.119.4 after this profiling): all tests pass, library frame costs within noise of 3.x (clear-only shm/gl/vulkan 47/95/154 µs, text scene 178/254/291 µs), oops95 retitling 2.4% → 1.4% CPU and cold start ~55 → ~38 ms. `libSkiaSharp.so` is 12.4 MB (was 11.2 MB).

Not a bug: a floating window resized by `swaymsg resize set` drifts off-screen and sway (correctly) stops sending frame callbacks, which looks like a stalled renderer.

## Known limitations

- OopsOSX: apps without any themed icon get drawn ones; on niri the active window looks inactive while a menu-bar menu is open (niri only lets a focused layer surface's popup take the keyboard, so the bar takes focus while the pointer is over it and gives it back after); niri ignores minimize (Hide, Minimize do nothing there); the switcher's held-modifier path was verified on sway (niri does not carry a virtual keyboard's modifiers across focus changes, so the scripted rig cannot hold one there).

- `Popup.Reposition` needs xdg_popup v3 (throws otherwise; recreate the popup).
- When the Start menu is opened from a compositor keybinding there is no input serial to grab with, so it opens without a grab and takes keys through the taskbar's exclusive keyboard; an outside click does not close it then (Escape or the Start button does).
- The battery indicator was checked with a wireless mouse only; a laptop battery (exact percentage, charging, time left) is a manual check.
- niri binds cannot use a bare modifier; `config/niri.kdl` uses Mod+Space for the Start menu.
- Not verified unattended: real GPU, real HiDPI, real tray apps, PAM with a real password, niri (see MANUAL_TESTS.md).

- Skia's fontconfig manager does not resolve generic families such as `sans-serif` on every system; samples fall back to `SKFontManager.MatchCharacter`.

- Window capture is verified only against the wlroots 0.20 fixture: niri 26.04, sway 1.11 and KWin do not offer it yet, so Exposé shows icons there. Pairing ext and wlr handles relies on app id + title (identical windows pair in announcement order).
- `oopshell.Services.Audio` reads libpulse structs at fixed LP64 offsets (x86_64/aarch64); `AudioTests` would catch a mismatch.
- Unprivileged `pam_unix` can only check the calling user's password (fine for lock screens; not for greeters).

- Each frame callback allocates one small managed `WlCallback` wrapper (protocol objects are new per frame).
- Damage is tracked as one bounding rectangle per frame, not a region.
- Vulkan windows repaint the whole buffer every frame and report full damage (no buffer age, no VK_KHR_incremental_present).
