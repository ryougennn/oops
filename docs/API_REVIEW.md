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

## Candidates, not added (decide before freezing)

Ranked by how much both desktops would gain.

1. **Text with font fallback (oopshell.Skia).** Skia draws missing glyphs as boxes; it does not fall back.
   Both desktops show user-supplied text (window titles, user names, app names in other languages).
   OopsOSX carries a ~60-line run splitter (`Kit/Ui.cs`: `Runs`, `Measure`, `DrawText`). A
   `SkiaText.Draw/Measure` helper in `oopshell.Skia` would fix this for every app. Strong candidate.
2. **Single-instance control channel.** oops95 and OopsOSX carry the same `ControlSocket` (Unix socket in
   `$XDG_RUNTIME_DIR`, served with `WatchFd`) so compositor key bindings can reach the shell. Quickshell
   has `IpcHandler`. A small `OopsApp`-level helper (name → command handler, plus a client `Send`) would
   remove the duplication. Strong candidate.
3. **PAM.** Both desktops carry the same `PamAuthenticator` for the lock screen. Quickshell has
   `PamContext`. Candidate for an `oopshell.Services.Pam` package (conversation callbacks, so it can also
   handle OTP/fingerprint prompts later).
4. **SVG icons.** `IconTheme` finds SVG icons but nothing renders them, so SVG-only themes (Breeze,
   Papirus, Adwaita apps) fall back to drawn icons. Needs a dependency (Svg.Skia, or librsvg via interop);
   would be an optional package, not core.
5. **Audio and media.** No PipeWire/PulseAudio volume and no MPRIS service, so OopsOSX has no volume or
   now-playing menu extra. Quickshell has both. Services, not core; add when a demo needs them.
6. **Window previews.** No toplevel capture (ext-image-copy-capture with a foreign-toplevel source), so
   no Exposé or window thumbnails. Compositor support is still uneven (niri: output screencopy only).

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
