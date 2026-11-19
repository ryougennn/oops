# API freeze review: what a second desktop needed

OopsOSX (`demo/oopsosx`) is a second, deliberately different desktop built only on the published
packages, to find out what is missing before the API is frozen. It exercises: layer surfaces on every
layer (wallpaper, menu bar, Dock, overlays), dynamic input regions and exclusive zones (Dock magnification
and auto-hide), grabbed popups with cascading submenus and in-place `Reposition` (menu bar tracking),
client-side decorated toplevels, `ForEachOutput`, foreign toplevels (window lists, activation, MRU),
`LaunchAsync` with activation tokens, session lock, notifications, tray, UPower, keyboard-exclusive
overlays with modifier tracking (the app switcher). Tested on niri 26.04 (nested) and sway 1.11.

## Changed in the library

| Change | Why | Where |
|---|---|---|
| `Toplevel.ShadowMargins`, `Popup.ShadowMargins`, `Toplevel.GeometrySize` | CSD shadows were impossible: buffers were sized from the configure size | DECISIONS D21; `ShadowMarginTests` |
| `DesktopEntry.StartupWMClass`, `DesktopEntries.FindByAppId` | every dock/taskbar must map app_id → application; oops95 lacked it too | D22; `DesktopTests` |
| Fix: `Popup.Grab` uses the serial current at the call | the release serial cost the keyboard grab on niri | D23; `DamageTraceTests.Popup_grab_uses_the_serial_of_the_click_that_opened_it` |

## Candidates and what became of them

1. **Text with font fallback (oopshell.Skia): still open.** Skia draws missing glyphs as boxes; it does
   not fall back. OopsOSX carries a ~60-line run splitter (`Kit/Ui.cs`: `Runs`, `Measure`, `DrawText`);
   oops95 still shows boxes for CJK window titles. A `SkiaText.Draw/Measure` helper would fix it for every app.
2. **Single-instance control channel: added** as `IpcServer` in the core package (D24). Both demos use it.
3. **PAM: added** as `oopshell.Services.Pam` (D25). Both demos use it.
4. **SVG icons: added** as `SvgRenderer` in `oopshell.Services.Desktop`, through the system librsvg (D26).
   Both demos render SVG theme icons.
5. **Audio and media: added** as `oopshell.Services.Audio` (volume and mute, PulseAudio API, D27) and
   `oopshell.Services.Media` (MPRIS, D28). OopsOSX has volume and now-playing menu extras.
6. **Window previews: added** as `ScreenCapture` (core, D30): window and output capture through
   `ext-image-copy-capture-v1`. No packaged compositor here offers window capture yet (niri 26.04: none;
   sway 1.11: outputs only; KWin: its own `zkde_screencast`), so it is tested against
   `tests/fixtures/capturewl`, a tinywl built on wlroots 0.20 (which implements it). OopsOSX uses it for
   Exposé, with app-icon tiles where capture is missing. For comparison, Quickshell's `ScreencopyView`
   captures outputs through ext-image-copy-capture or wlr-screencopy, and windows only through Hyprland's
   own `hyprland-toplevel-export` protocol.

Found while doing these: layer-surface property changes waited for the next frame and could arrive after a
popup's grab (fixed, D29).

## Things that are app-level and stay that way

- Animation: invalidating from inside `Render` while something moves is enough (OopsOSX's `Tween`, Dock
  bounce, menu blink and fade, pulsing default button). Idle stays at zero wakeups.
- Closing menus on clicks on the app's own surfaces (grabs only dismiss on other clients' surfaces).
- Compositor differences found: niri gives keyboard focus to a layer surface rather than its grabbing
  popup (forward keys from the layer surface; give it `OnDemand` interactivity while a menu is open),
  and niri ignores minimize requests. Documented in the guide; no API needed.

## Quickshell comparison

| Quickshell | oopshell |
|---|---|
| `PanelWindow`, `PopupWindow`, `FloatingWindow` | `LayerSurface`, `Popup`, `Toplevel` |
| `Variants { model: Quickshell.screens }` | `app.ForEachOutput` |
| `ToplevelManager` | `app.Toplevels` |
| `WlSessionLock`, `IdleMonitor` | `app.LockAsync`, `app.Idle.Watch` |
| `DesktopEntries.heuristicLookup` | `DesktopEntries.FindByAppId` (new) |
| `SystemTray`, `NotificationServer`, `UPower` | `TrayHost`, `NotificationServer`, `PowerService` |
| `IpcHandler`, `PamContext` | `IpcServer`, `PamAuthenticator` |
| `Pipewire`, `Mpris`, `ScreencopyView` | `AudioService`, `MediaService`, `ScreenCapture` |
| `Process`, `FileView`, `SystemClock` | .NET's own `Process`, `File`, timers |

## Freeze (2026-11-18)

After a third desktop (Crossbar, a 10-foot kiosk on sway and niri) needed no core changes, the remaining
shape questions were settled (IPC reply status D34, wall-clock timers D35, the async rule D33), screen
loss down to zero outputs and during a lock was tested, and the system services a full desktop needs were
added (D36). From here the public API of every package changes only by addition; anything else needs a
decision entry explaining why. The first additions (D37): idle and keyboard-shortcut inhibitors,
workspaces through ext-workspace, activation tokens for notification actions, a NetworkManager secret
agent and a BlueZ pairing agent.
