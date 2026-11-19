# Quick start: build a bar

This walks through a small but real status bar: workspaces on the left, the focused window in the middle,
volume and a clock on the right, one bar per monitor, a key to hide it, and starting it with your session.
Each step runs on its own, so you can stop at any point. The finished program is
`samples/QuickBar` in the repository, listed in full [at the end](#the-whole-program).

## What you need

- The [.NET 10 SDK](https://dotnet.microsoft.com/download).
- A Wayland compositor with layer-shell, which is how bars, docks and wallpapers place themselves: niri,
  sway, Hyprland, river, KDE Plasma and most wlroots compositors have it. GNOME does not.
- To be inside that Wayland session (a terminal in it is enough).

## 1. An empty bar

```sh
dotnet new console -n QuickBar && cd QuickBar
dotnet add package oopshell.Skia --prerelease
```

Replace `Program.cs`:

```csharp
using oopshell;
using oopshell.Skia;
using SkiaSharp;

using var app = OopsApp.Connect();

var surface = new LayerSurface(app, app.Outputs[0], Layer.Top, "quickbar")
{
    Anchor = Anchor.Top | Anchor.Left | Anchor.Right,
    Size = new(0, 30),   // 0 wide: stretch between the left and right anchors
    ExclusiveZone = 30,  // windows are placed below the bar instead of under it
};
var skia = new SkiaSurface(surface);

surface.Render += frame =>
{
    if (skia.BeginFrame(frame) is not { } canvas)
        return;
    canvas.Clear(new SKColor(0x1E, 0x22, 0x2A));
    skia.EndFrame();
};

return app.Run();
```

`dotnet run` puts a dark strip along the top of your screen, and windows move down to make room. Ctrl+C
ends it.

A `LayerSurface` is a surface the compositor places for you: `Layer.Top` keeps it above windows, the
anchors pin it to edges, and the exclusive zone reserves that space. `Render` runs whenever the surface needs
pixels; oopshell calls it only when something changed, so an idle bar uses no CPU.

## 2. A clock

Text goes through Skia. Draw in logical pixels and let `frame.Scale` handle HiDPI and fractional scaling:

```csharp
var font = new SKFont(SKTypeface.Default, 14);
var paint = new SKPaint { Color = new SKColor(0xD8, 0xDE, 0xE9), IsAntialias = true };

surface.Render += frame =>
{
    if (skia.BeginFrame(frame) is not { } canvas)
        return;
    canvas.Scale((float)frame.Scale);
    canvas.Clear(new SKColor(0x1E, 0x22, 0x2A));
    canvas.DrawText(DateTime.Now.ToString("HH:mm"), frame.LogicalSize.Width - 10, 20, SKTextAlign.Right, font, paint);
    skia.EndFrame();
};

using var clock = app.SetClockTimer(TimeSpan.FromMinutes(1), surface.Invalidate);
```

`Invalidate()` asks for a new frame. `SetClockTimer` fires on the minute, and right after the machine wakes
from suspend, which is what a clock needs (a plain `SetTimer` pauses while suspended).

## 3. Every monitor

`ForEachOutput` calls you for every monitor, including ones plugged in later, and disposes what you returned
when a monitor goes away. Move the bar into a class:

```csharp
using var bars = app.ForEachOutput(output => new Bar(app, output));

sealed class Bar : IDisposable
{
    private readonly LayerSurface _surface;
    private readonly SkiaSurface _skia;

    public Bar(OopsApp app, Output output)
    {
        _surface = new LayerSurface(app, output, Layer.Top, "quickbar") { /* as before */ };
        _skia = new SkiaSurface(_surface);
        _surface.Render += Render;
    }

    private void Render(RenderFrame frame) { /* as before */ }

    public void Invalidate() => _surface.Invalidate();

    public void Dispose()
    {
        _skia.Dispose();
        _surface.Dispose();
    }
}
```

The clock timer now redraws every bar; the sample keeps them in a list.

## 4. Workspaces and the focused window

oopshell follows the compositor's state for you:

- `app.Workspaces` lists workspaces (compositors with the ext-workspace protocol, such as niri; null
  elsewhere). Each has `Name`, `IsActive`, and `Activate()`. `app.WorkspacesChanged` fires after changes.
- `app.Toplevels` lists other applications' windows, with `Title`, `AppId`, `IsActivated` and a `Changed`
  event, and is an observable collection, so you hear about new and closed windows.

Draw them, remember where each workspace was drawn, and activate the one clicked:

```csharp
_surface.PointerButton += e =>
{
    if (e.Pressed && _buttons.FirstOrDefault(b => b.Area.Contains((float)e.X, (float)e.Y)) is { Workspace: { } w })
        w.Activate();
};
```

Pointer coordinates are in the surface's logical pixels, the same space you draw in.

## 5. Volume

Services are separate packages, so a bar only carries what it uses:

```sh
dotnet add package oopshell.Services.Audio --prerelease
```

```csharp
var audio = await AudioService.StartAsync(app);   // PulseAudio, or PipeWire's PulseAudio server
audio.Changed += redrawAll;
// draw audio.DefaultSink.Volume; on scroll: audio.SetVolume(sink, newVolume)
```

Every event is raised on the UI thread, and awaiting from it resumes there, so you never lock or marshal.
Other services work the same way: notifications, tray, batteries, media players, Wi-Fi, Bluetooth.

## 6. A key to hide it

Compositors bind keys to commands. `IpcServer` lets that command reach the running bar:

```csharp
if (args is ["toggle"])
    return IpcServer.Send("quickbar", "toggle").IsOk ? 0 : 1;

using var ipc = IpcServer.Listen(app, "quickbar", request => { ToggleBars(); return null; });
```

Bind it, for example in niri's `config.kdl`:

```kdl
binds {
    Mod+B { spawn "quickbar" "toggle"; }
}
```

or sway's config: `bindsym $mod+b exec quickbar toggle`.

## 7. Start it with your session

Publish a native binary (no .NET runtime needed on the machine, and it starts instantly). Add
`<PublishAot>true</PublishAot>` to the project, then:

```sh
dotnet publish -c Release -r linux-x64 -o ~/.local/bin
```

and start it from your compositor: niri `spawn-at-startup "quickbar"`, sway `exec quickbar`, Hyprland
`exec-once = quickbar`.

## While you work on it

- `dotnet watch` applies most code changes to the running bar without restarting it
  ([hot reload](hot-reload.md)).
- "Could not connect to the Wayland compositor": run it from inside your Wayland session.
- Nothing appears: the compositor probably lacks layer-shell (GNOME).
- Squares instead of letters: the font lacks those characters; pick one with `SKFontManager.MatchCharacter`.
- `app.UnhandledException` lets the bar log an exception and keep running instead of exiting.

## The whole program

[!code-csharp[](../../samples/QuickBar/Program.cs)]

## Next

- [Getting started](getting-started.md) explains the app, the loop, surfaces, input and the services in more depth.
- [Rendering](rendering.md) and [Skia](skia.md) cover drawing without Skia, GPU rendering and damage.
- The API reference lists everything, with the details in each member's documentation.
- Full desktops built on oopshell: `demo/oops95`, `demo/oopsosx` and `demo/crossbar`. `samples/oops-lab`
  exercises every service.
