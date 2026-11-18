# Getting started

Requirements: .NET 10 SDK, a wlroots-based compositor (sway, niri, …), `libwayland-client`,
`libxkbcommon`. GNOME is not supported (no layer-shell).

```sh
dotnet new console -n MyBar && cd MyBar
dotnet add package oopshell
```

## The app and the loop

`OopsApp.Connect()` connects (honoring `WAYLAND_DISPLAY`/`WAYLAND_SOCKET`), binds the globals oopshell
uses, and waits until outputs and seats are described. `app.Run()` turns the calling thread into the UI
thread and returns an exit code (non-zero after connection loss; `app.Quit(code)`, SIGINT and SIGTERM
also stop it).

Everything Wayland belongs to that thread. From other threads use `app.Post(action)` or
`await app.InvokeAsync(...)`; `await` inside handlers resumes on the UI thread automatically.

```csharp
app.SetTimer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), () => clock.Invalidate());
app.WatchFd(fd, FdEvents.Readable, events => ReadMore());
```

## Surfaces

| Type | Use |
|---|---|
| `Toplevel` | normal windows: title, app id, min/max size, maximize/fullscreen/minimize, `Move`/`Resize`, `CloseRequested`, decoration negotiation |
| `LayerSurface` | bars, docks, wallpapers, overlays: output, layer, anchors, margins, size, exclusive zone, keyboard interactivity |
| `Popup` | menus and tooltips from any of the above; `Grab(seat)` for menus; `Reposition` |
| `LockSurface` | created for you by `app.LockAsync(...)` |

You never handle configure serials: subscribe to `Configured` (sizes) and `Render` (draw). Call
`surface.Invalidate()` (or `Invalidate(rect)`) when something changed; with no invalidation there are no
frames and no CPU use.

## Input

Every surface raises `PointerEnter/Leave/Move/Button/Scroll`, `KeyDown/KeyUp/TextInput`,
`KeyboardFocusChanged`, `ModifiersChanged` and `TouchDown/Move/Up/Cancel`. Positions are logical
pixels. `TextInput` delivers composed text (compose sequences included); key repeat is built in. Set
`surface.Cursor` to a `CursorShape`.

## Outputs

`app.Outputs`, `app.OutputAdded/Removed`, and the handy `app.ForEachOutput(output => …)`, whose
returned `IDisposable` is disposed when that output goes away.

## Desktop integration

`app.Toplevels` (other apps' windows for a taskbar), `app.LockAsync`, `app.Idle.Watch`,
`app.LaunchAsync` (starts a detached process with an activation token).

## Escape hatches

`app.Globals`, `app.Get<T>()`/`Has<T>()`/`Bind<T>()` for any generated interface, `surface.Proxy`
(the raw `wl_surface`), `toplevel.Role`, `layer.Role` … all generated proxies are public.

See `samples/` for complete programs: `oops-info`, `HelloWindow`, `HelloBar`, `HelloSkia`.
