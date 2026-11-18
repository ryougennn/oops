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
6. **Window previews: blocked by compositors.** Thumbnails need
   `ext-foreign-toplevel-image-capture-source-v1` (with `ext-image-copy-capture-v1`). niri 26.04 has neither;
   sway 1.11 can capture outputs only. Add when a target compositor ships it, so it can be tested.

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
| `IpcHandler`, `PamContext` | missing (candidates 2 and 3) |
| `Pipewire`, `Mpris`, `ScreencopyView` | missing (candidates 5 and 6) |
| `Process`, `FileView`, `SystemClock` | .NET's own `Process`, `File`, timers |
