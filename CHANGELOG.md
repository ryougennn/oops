# Changelog

## 0.1.0 (unreleased)

First release.

- **Core (`oopshell`)**: Wayland client on libwayland-client with bindings generated from protocol XML;
  `Toplevel`, `LayerSurface`, `Popup`, `LockSurface`; outputs and seats with hotplug; pointer, keyboard
  (xkb, compose, repeat) and touch input; frame-callback rendering with damage tracking; fractional scale;
  `ShmSwapchain`; idle notifications and inhibitors; keyboard-shortcut inhibitors; activation tokens and
  `LaunchAsync`; foreign toplevels; workspaces (ext-workspace); background blur (ext-background-effect);
  window and output capture (ext-image-copy-capture); `IpcServer`; wall-clock timers; hot reload;
  `UnhandledException` for shells that should survive a broken widget. NativeAOT with no trim warnings.
- **Rendering**: `oopshell.Egl`, `oopshell.Vulkan`, `oopshell.Skia`.
- **Services**: D-Bus helpers; desktop entries, icon themes and SVG; an app catalog with search, launching
  and default apps; a notification server; a StatusNotifierItem tray; UPower; PulseAudio/PipeWire volume;
  MPRIS; PAM; a polkit agent; NetworkManager with a secret agent; BlueZ with a pairing agent; greetd.
- **Demos**: oops95, OopsOSX and Crossbar shells; `samples/oops-lab` for the manual checks.
