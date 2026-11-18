# Decisions

Short ADR-style log. Newest at the bottom.

### D1. Package name: `oopshell`
- **Context:** §15 asks to check `oops` on nuget.org first.
- **Decision:** `https://api.nuget.org/v3/registration5-semver1/oops/index.json` returned 200 (taken); `oopshell` returned 404 (free). Everything is named `oopshell` (`oopshell`, `oopshell.Protocol`, …). The type `OopsApp` keeps its name. The owner should reserve the ID by pushing an early prerelease.

### D2. libwayland-client instead of a managed wire implementation
- **Context:** EGL (`wl_egl_window_create`) and Vulkan (`VK_KHR_wayland_surface`) need real `wl_display*`/`wl_surface*`.
- **Decision:** bind libwayland-client via `[LibraryImport]`. `WAYLAND_DEBUG=1` works unchanged.
- **Alternatives:** a pure managed protocol implementation (no native dep, but breaks every GPU library).

### D3. Events are C# `event`s, not overridable `On*` methods
- **Context:** §4.1 lets us choose.
- **Decision:** generated proxies expose `event Action<TArgs>?`. Several listeners can subscribe (the high-level layer and user code both listen to the same `wl_output`), classes can stay `sealed`, and nothing needs subclassing.
- **Event args** are `readonly ref struct`s that read lazily from the native `wl_argument*` (strings are only decoded if the handler reads them), so dispatching allocates nothing. They are only valid during the handler. `Action<T>` accepts them because .NET 9+ declares `allows ref struct` on its type parameter.

### D4. One native dispatcher, virtual managed dispatch
- **Context:** §4.2 says "a generated per-interface dispatcher".
- **Decision:** a single `[UnmanagedCallersOnly]` function (`WlProxy.DispatchNative`) receives every event and calls the proxy's generated `Dispatch(opcode, args)` override. Same effect, one native entry point instead of one per interface.
- Proxies created by oopshell are tagged with `wl_proxy_set_tag`, so a `wl_proxy*` created by someone else (EGL, libwayland-cursor) is never mistaken for one of ours when it shows up as an object argument.

### D5. Event fds are closed after the handler
- Generated code passes `fd` event args as `int` and closes them after the handlers return. Handlers that keep an fd must `dup` it. This makes leaks impossible when nobody subscribes.

### D6. Request `fd` args are `int`; libwayland dups them
- `wl_closure_marshal` dups request fds, so the caller keeps ownership. An `int` lets callers pass any fd source.

### D7. Cross-assembly protocol references
- Each generated assembly carries `[assembly: WaylandProtocols(namespace, "iface iface.enum …")]`. A user's scanner run reads these attributes from referenced assemblies to resolve `interface="wl_surface"` or `enum="wl_output.transform"` to `global::oopshell.Protocol.…`. Unknown names produce OOPS002.

### D8. Vendored `xdg-output-unstable-v1` (not listed in §5)
- `Output.LogicalSize`/`LogicalPosition` are only exact with xdg-output (fractional scales, transforms). Without it oopshell derives them from `wl_output` geometry, mode and integer scale.

### D9. Initial commit is deferred to the next render phase
- Constructing a surface sets up its role; the bufferless initial commit happens in the loop's next render phase. Properties set right after construction (title, anchors, popup grab) are therefore part of the initial state, as layer-shell and `xdg_popup.grab` require.

### D10. Frame pacing: render immediately when idle, otherwise on the frame callback
- `Invalidate()` marks the surface dirty. In the render phase, a dirty surface renders if no frame callback is in flight (or if it was just configured). The frame callback is requested inside `PrepareCommit()` (called by every present path), so a frame that skips presenting (e.g. no free SHM buffer) does not leave the surface waiting for a callback that never comes. Net effect equals §8.1: at most one frame per callback, no frames without invalidation.

### D11. NuGet audit suppression for the installed SDK
- The machine's .NET SDK 10.0.113 brings ILCompiler 10.0.13, flagged by GHSA-p3jp-9v83-76w9 (NU1903, an error under TreatWarningsAsErrors). The advisory is suppressed in `Directory.Build.props` so AOT builds work; **update the SDK and remove the suppression before releasing binaries**.

### D12. Geometry types from System.Drawing
- `Size`, `Point`, `Rectangle` come from `System.Drawing.Primitives` (part of the BCL, AOT-safe) instead of new oopshell types. Pointer positions stay `double`.

### D13. SkiaSharp 3.119.4
- `oopshell.Skia` pins SkiaSharp 3.119.4 (+ `SkiaSharp.NativeAssets.Linux`), the latest 3.x stable. 4.x exists but was not evaluated; 3.119.4 AOT-publishes with zero warnings.

### D14. One GL context per app, one GRContext per EGLDisplay
- Every `EglWindow` of an app uses the same `EGLContext` (made current with its own `EGLSurface`), so resources are shared without share-lists, and `SkiaSurface` keeps one `GRContext` per `EglDisplay`.
- `EglDisplay` is terminated when its `OopsApp` is disposed (`OopsApp.Disposing`): Mesa caches EGLDisplays by `wl_display*` address and a new connection can reuse the address, which otherwise produced `invalid object` protocol errors.

### D15. `PublishAot` only in Release
- `PublishAot=true` sets `MetadataUpdater.IsSupported=false` and `StartupHookProvider.IsSupported=false` in every build's runtimeconfig, so `dotnet watch` hot reload never connects. Samples and the demo set it under `Configuration == Release`; the spec's `-p:PublishAot=true` command still works.

### D16. Grab/activation serial = latest button, key or touch serial
- `Seat.LastSerial` ignores enter/leave/modifiers serials. Compositors validate grab serials against input events; a click that changes keyboard focus produces a `wl_keyboard.enter` right after the button press, and using that serial got popups dismissed immediately.

### D17. Lock surfaces skip the initial bufferless commit
- ext-session-lock configures lock surfaces without an initial commit and makes committing before the first ack a protocol error (`commit_before_first_ack`). Roles can opt out via `CommitsBeforeConfigure`.

### D18. D-Bus via Tmds.DBus.Protocol 0.95.2
- Evaluated first, as §10 asks: a probe app AOT-publishes with zero IL warnings, so the services use it instead of a home-made client.
- Integration is through `app.SynchronizationContext` (signal watches and exported-object calls run on the UI thread) rather than `WatchFd`, because Tmds owns its socket I/O. Recorded as a deviation in STATUS.
- **Pitfall:** Tmds' `MessageWriter` is a mutable struct. Passing it through `Action<MessageWriter>` (legal since .NET 9's `allows ref struct`) writes into a copy, producing messages with an empty body, which dbus-daemon answers by disconnecting. oopshell's helpers take `BodyWriter(ref MessageWriter)` and never pass writers by value.

### D19. Tray: become the watcher when nobody else is
- `TrayHost` tries to own `org.kde.StatusNotifierWatcher`; if another bar already does, it registers as a host with that watcher and follows its signals. Items whose bus name disappears are unregistered via `NameOwnerChanged`.
