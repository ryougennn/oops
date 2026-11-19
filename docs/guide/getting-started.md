# Getting started

Requirements: .NET 10 SDK, a wlroots-based compositor (sway, niri, …), `libwayland-client`,
`libxkbcommon`. GNOME is not supported (no layer-shell).

```sh
dotnet new console -n MyBar && cd MyBar
dotnet add package oopshell
```

## The app and the loop

`OopsApp.Connect()` connects (honoring `WAYLAND_DISPLAY`/`WAYLAND_SOCKET`), binds the globals oopshell
uses, and waits until outputs and seats are described. Without a compositor it throws
`WaylandConnectionException`, whose message names the socket and the reason; catch it to exit cleanly.
`app.Run()` turns the calling thread into the UI thread and returns an exit code (`app.Quit(code)`,
SIGINT and SIGTERM stop it). When the compositor goes away, `Run()` returns 1 and `app.ConnectionError`
says why; disposing surfaces and services afterwards is safe, so `using` blocks unwind normally.

Everything Wayland belongs to that thread. From other threads use `app.Post(action)` or
`await app.InvokeAsync(...)`; `await` inside handlers resumes on the UI thread automatically.

The API follows one rule: a one-off operation returns a `Task` (`LaunchAsync`, `CaptureAsync`,
`LockAsync`, `AuthenticateAsync`, D-Bus calls…), and anything that keeps changing is an event or a
`Changed` notification. Events, including every service's, are raised on the UI thread, and awaiting
a `Task` from the UI thread resumes there, so handlers never need to marshal.

```csharp
app.SetClockTimer(TimeSpan.FromMinutes(1), clock.Invalidate);   // on the minute, and right after resume
app.SetTimer(TimeSpan.FromSeconds(5), null, HideToast);           // monotonic: pauses during suspend
app.WatchFd(fd, FdEvents.Readable, events => ReadMore());
```

## Surfaces

| Type | Use |
|---|---|
| `Toplevel` | normal windows: title, app id, min/max size, maximize/fullscreen/minimize, `Move`/`Resize`, `CloseRequested`, decoration negotiation |
| `LayerSurface` | bars, docks, wallpapers, overlays: output, layer, anchors, margins, size, exclusive zone, keyboard interactivity |
| `Popup` | menus and tooltips from any of the above; `Grab(seat)` for menus; `Reposition` |
| `LockSurface` | created for you by `app.LockAsync(...)` |

Client-side decorations with a shadow: set `ShadowMargins` on a `Toplevel` or `Popup`. The compositor
sizes, tiles and positions the window by its geometry (`GeometrySize`, `PopupOptions.Size`); render frames
include the margins, so draw the window inset by them and set `InputRegion` to the geometry so clicks on
the shadow fall through. Popup anchors are in the parent's geometry coordinates.

Frosted glass: set `BlurRegion` on any surface (`Region.All` for all of it, null for none) and draw
translucent content over it; the compositor blurs what is behind (ext-background-effect, e.g. niri).
`app.CanBlurBehind` (and `CanBlurBehindChanged`) say whether it currently can, so a shell can fall back
to a more opaque look; where it can't, the request is simply ignored.

Open menus from the input handler that should own the grab: `Grab(seat)` records the seat's serial at that
moment. Grabs are only dismissed by clicks on *other* clients, so close your menus yourself when another of
your own surfaces is clicked. On niri, a layer surface's grabbing popup does not get the keyboard itself:
give the layer surface `KeyboardInteractivity.OnDemand` while the menu is open and forward its `KeyDown`
events to the menu (OopsOSX's menu bar does this).

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

`app.Toplevels` (other apps' windows for a taskbar; `DesktopEntries.FindByAppId` in
`oopshell.Services.Desktop` maps a window's app id to its application for names and icons), `app.LockAsync`, `app.Idle.Watch`,
`app.LaunchAsync` (starts a detached process with an activation token).

## Commands from key bindings

Compositor key bindings run a command; `IpcServer` gets it to the running shell:

```csharp
using var ipc = IpcServer.Listen(app, "myshell", args => args[0] == "toggle-menu" ? Toggle() : null);
// `myshell --toggle-menu` in another process:
var reply = IpcServer.Send("myshell", "toggle-menu");   // Status: Ok, Failed (handler threw), NotRunning
if (!reply.IsOk) Console.Error.WriteLine(reply.Status == IpcStatus.NotRunning ? "not running" : reply.Text);
```

The socket lives in `$XDG_RUNTIME_DIR`; `Listen` refuses to start a second instance and replaces a stale
socket left by a crash. Requests are served on the UI thread from the event loop.

## Capture (thumbnails)

`ScreenCapture.ForWindow(app, window)` / `ForOutput(app, output)` (null where the compositor cannot), then
`await capture.CaptureAsync()` for the pixels. Window capture needs ext-image-copy-capture and
ext-foreign-toplevel-image-capture-source (check `ScreenCapture.CanCaptureWindows(app)` and fall back to icons).

## Services

Optional packages, each started with `await XService.StartAsync(app, …)`, raising events on the UI thread:
`oopshell.Services.Desktop` (applications, icon themes, `SvgRenderer`), `.Launcher` (`AppCatalog`: live
app list, `Search`, `LaunchAsync` with terminals and desktop actions, `OpenAsync` with the user's default
apps), `.Notifications` (be the
notification daemon), `.Tray` (StatusNotifierItem), `.Power` (UPower), `.Audio` (default output volume and
mute through the PulseAudio API, which PipeWire serves), `.Media` (MPRIS players: now playing, play/pause,
next), `.Pam` (`PamAuthenticator.AuthenticateAsync` for lock screens; runs off the UI thread), `.Polkit`
(`PolkitAgent`: the "authentication is required" dialog's backend; a desktop's own agent may already hold
the session), `.Network` (`NetworkService`: NetworkManager devices, connectivity, Wi-Fi networks, connect),
`.Bluetooth` (`BluetoothService`: BlueZ adapters and devices with batteries; discover, pair, connect) and
`.Greetd` (`GreetdClient`: log a user in from a greeter and start their session). The system-bus services
take a connection from `OopsDBus.ConnectAsync(app, DBusAddress.System)`.

## Escape hatches

`app.Globals`, `app.Get<T>()`/`Has<T>()`/`Bind<T>()` for any generated interface, `surface.Proxy`
(the raw `wl_surface`), `toplevel.Role`, `layer.Role` … all generated proxies are public.

See `samples/` for complete programs: `oops-info`, `HelloWindow`, `HelloBar`, `HelloSkia`.
