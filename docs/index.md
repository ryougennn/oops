---
_layout: landing
---

# oopshell

Build Wayland desktop shells (bars, docks, launchers, wallpapers, lock screens, notification daemons) in C#,
published as NativeAOT binaries that start instantly and need no .NET runtime.

- **New here?** [Build a bar](guide/quickstart.md) in seven short steps.
- **The concepts:** [Getting started](guide/getting-started.md) covers the app, the event loop, surfaces,
  input, outputs and services.
- **Everything else:** the [API reference](api/index.md), generated from the documentation in the source.

## Packages

| Package | What it gives you |
|---|---|
| `oopshell` | The Wayland client: surfaces, input, outputs, the event loop, layer shell, lock screens, capture, IPC. |
| `oopshell.Skia` | Drawing with SkiaSharp on the CPU, EGL or Vulkan. |
| `oopshell.Egl`, `oopshell.Vulkan` | GPU surfaces for your own renderer. |
| `oopshell.Protocol`, `oopshell.Native` | Generated protocol bindings and the native interop under them. |
| `oopshell.Services.Desktop` | Desktop entries, icon themes, SVG icons. |
| `oopshell.Services.Launcher` | A live application catalog, search, launching, default applications. |
| `oopshell.Services.Notifications` | Be the notification daemon. |
| `oopshell.Services.Tray` | StatusNotifierItem system tray and menus. |
| `oopshell.Services.Audio`, `.Media` | Volume (PulseAudio/PipeWire) and media players (MPRIS). |
| `oopshell.Services.Power` | Batteries and power state (UPower). |
| `oopshell.Services.Network`, `.Bluetooth` | Wi-Fi and network state (NetworkManager), Bluetooth (BlueZ). |
| `oopshell.Services.Pam`, `.Polkit`, `.Greetd` | Lock-screen authentication, a polkit agent, login greeters. |
| `oopshell.Services.DBus` | The D-Bus plumbing the services share. |
