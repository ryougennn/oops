# oops

oopshell (*Object Oriented Programming Shell*) is a little .NET library for building your own Wayland desktop
shell in plain C#. Builds on NativeAOT.

I wanted something like Quickshell but in C#, without a QML layer in the middle, so here we are.

```csharp
using oopshell;

using var app = OopsApp.Connect();
using var bars = app.ForEachOutput(output =>
{
    var bar = new LayerSurface(app, output, Layer.Top, "panel")
    {
        Anchor = Anchor.Bottom | Anchor.Left | Anchor.Right,
        Size = new(0, 32),
        ExclusiveZone = 32,
    };
    var chain = new ShmSwapchain(bar);
    bar.Render += frame =>
    {
        if (chain.Acquire(frame) is not { } buffer)
            return;
        buffer.Pixels.Fill(0xFF202830);
        buffer.Present(frame.Damage);
    };
    return bar;
});
return app.Run();
```

puts a bar on every monitor.

## what's in it

- The Wayland basics: layer-shell surfaces, windows, popups, lock screens, input (keyboard with compose,
  pointer, touch), monitor hotplug, idle, blur behind surfaces, workspaces, window and screen capture.
- Draw however you like: CPU buffers, EGL or Vulkan, or just use Skia and forget about it.
- Hot reload with `dotnet watch` so you can tweak your bar while it's running.
- Optional service packages for the stuff every desktop needs: app launcher and search, notifications,
  tray icons, batteries, volume, media players, Wi-Fi, Bluetooth, polkit prompts, PAM, and greetd.

It works on niri, sway, Hyprland, river, KDE Plasma and most wlroots compositors. anything that does layer-shell.

## getting started

The [quick start](docs/guide/quickstart.md) walks you through building a real status bar in seven steps.
After that, [`docs/guide`](docs/guide) goes deeper. `tools/docs.sh --serve` builds the full HTML docs and API
reference from the source and serves them locally.

## demos

slopped up demos used to prove it works;

- [`samples/QuickBar`](samples/QuickBar): the finished quick start bar
- [`samples/oops-lab`](samples/oops-lab): every service in one app, handy for testing

## packages

`oopshell`, `oopshell.Protocol`, `oopshell.Native`, `oopshell.Egl`, `oopshell.Vulkan`, `oopshell.Skia`, and
`oopshell.Services.{DBus,Desktop,Launcher,Notifications,Tray,Power,Audio,Media,Pam,Polkit,Network,Bluetooth,Greetd}`.

## Agent Use Disclosure
This project was built entirely through the use of AI agents. It's vibe coded slop. Don't let it offend you.
No guarantees are made in regards to code quality, efficiency, or safety. Things might break. You've been warned.
