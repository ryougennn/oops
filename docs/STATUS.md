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

## Deviations from the spec (and why)

- **Name** is `oopshell` (nuget `oops` is taken), see DECISIONS D1.
- **`ShmSwapchain` needs `maxBuffers >= 2`.** wlroots keeps the current buffer until another one is attached, so one buffer can never be released (found by a test).
- **Swapchain tests live in the integration test project**, because `wl_buffer.release` needs a real compositor.
- **`xdg-output-unstable-v1` vendored** in addition to §5 (exact logical output geometry).
- **`PublishAot` is Release-only in samples**: set unconditionally it disables hot reload in Debug builds (see docs/guide/hot-reload.md).
- **`Seat.LastSerial` only tracks button/key/touch serials**, not enter/leave/modifiers: a click that moves keyboard focus is followed by `wl_keyboard.enter`, whose serial made popup grabs fail.
- **Sway grants CSD only to floating windows**; tiled toplevels get server-side mode. `Toplevel.Decoration` reports what the compositor chose.

## Owner action items

- **Update the .NET SDK** (`sudo dnf update dotnet-sdk-10.0`). The installed 10.0.113 ships ILCompiler 10.0.13 with advisory GHSA-p3jp-9v83-76w9; the NuGet audit warning is suppressed in `Directory.Build.props` (DECISIONS D11) only so builds work. Remove the suppression after updating.
- Reserve the `oopshell` package ID on nuget.org.
- Pick minimum sway/niri versions (§15); current assumption: whatever Fedora 44 ships (sway 1.11, niri 26.04).

## Known limitations

- Skia's fontconfig manager does not resolve generic families such as `sans-serif` on every system; samples fall back to `SKFontManager.MatchCharacter`.

- Each frame callback allocates one small managed `WlCallback` wrapper (protocol objects are new per frame).
- Damage is tracked as one bounding rectangle per frame, not a region.
