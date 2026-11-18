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

### D13. SkiaSharp 4.157.0
- `oopshell.Skia` pins SkiaSharp 4.157.0 (+ `SkiaSharp.NativeAssets.Linux`), the latest 4.x stable; it AOT-publishes with zero warnings.
- Moved from 3.119.4 on 2026-11-18 with no source changes in the library (oops95 only had to pass `SKSamplingOptions` to two `DrawImage` calls). No parallel 3.x package: both majors ship the same `SkiaSharp` assembly and `libSkiaSharp.so`, so one app can only load one of them.
- 4.157 still has no semaphore API and no persistent shader cache (checked the native exports), so D20 and the shader-cache notes are unchanged.

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

### D20. Vulkan: CPU-waited acquire, oopshell does the layout transitions, opt-in for Skia
- SkiaSharp (3.119 and 4.157) cannot wait on or signal semaphores, so `VulkanWindow.Acquire` waits for the acquire fence on the CPU and transitions the image to `COLOR_ATTACHMENT_OPTIMAL`; `Present` transitions to `PRESENT_SRC_KHR` and signals the semaphore the present waits on. Skia's wrapped surfaces are told the image is in `COLOR_ATTACHMENT_OPTIMAL`, which is where Skia leaves a render target after drawing.
- Mailbox (else immediate, else FIFO) present mode: with FIFO, Mesa waits for its own frame callbacks and blocks the event loop while a surface is hidden.
- Device choice: first device in loader order (Mesa's device-select layer puts the display GPU first) whose graphics queue can present to the display; CPU devices (lavapipe) only as a fallback.
- `SkiaBackend.Auto` stays EGL: GL has buffer-age partial repaints and longer field use; Vulkan measured equal on steady frames and memory.

### D21. `ShadowMargins` on `Toplevel` and `Popup` (added for OopsOSX)
- Client-side shadows need a surface larger than the window geometry. Before, oopshell sized buffers from the configure size, so this was impossible even with the raw `xdg_surface` escape hatch. Now the role grows the surface by the margins and sends `set_window_geometry`; `LogicalSize`/render frames include the margins, `Toplevel.GeometrySize` is the configured size. Margins can change at any time (zero margins send the full-surface geometry once, then nothing).
- A property per role, not a general "window geometry" API: margins are what toolkits actually vary, and a free-form geometry rectangle would let the surface and geometry disagree about size.

### D22. `DesktopEntries.FindByAppId` and `DesktopEntry.StartupWMClass` (added for OopsOSX)
- Docks and taskbars must map a window's app_id to an application (name, icon, "is this pinned app running"). oops95 had no way to do it (generic task icons); OopsOSX needs it everywhere. Quickshell has the same helper (`DesktopEntries.heuristicLookup`).
- Rules in order: exact desktop id, case-insensitive id, `StartupWMClass`, last part of a reverse-DNS id, the program in `Exec` (skipping `env` and assignments), `Name`. Non-`NoDisplay` entries win ties. Unit-tested rule by rule.

### D23. `Popup.Grab` records the serial when it is called
- It used to read `seat.LastSerial` at the popup's first commit, which comes after the button *release* when press and release arrive together (fast clicks, scripted input). niri then grants the pointer grab but not the keyboard grab, so menus ignored arrow keys and Escape. Found with OopsOSX on niri; regression test checks the serial on the wire.

### D24. `IpcServer` in the core package
- Both demos carried the same single-instance Unix socket so compositor key bindings could reach the shell (Quickshell has `IpcHandler`). It is now core: `$XDG_RUNTIME_DIR/<name>.sock`, arguments NUL-separated, the client half-closes, the reply is read to EOF. Served from the event loop with non-blocking sockets (`WatchFd` per client), so a stuck client cannot block the UI; clients that never finish are dropped after two seconds; requests are capped at 64 KiB. An empty request is a ping that never reaches the handler.

### D25. `oopshell.Services.Pam`
- Both demos carried the same PAM interop. The package exposes `PamAuthenticator.AuthenticateAsync(user, password, service)` and a full-conversation overload (info/error messages, visible prompts) for later OTP or fingerprint flows; it runs on a worker thread and returns a `PamResult` (success, auth failed, account unavailable, aborted, error). Answers are malloc'ed copies wiped before freeing on abort.
- Tested with `pam_start_confdir` and a private service where `pam_exec` checks the password it receives through our conversation, so CI needs no real password. Real `pam_unix` was checked by hand with a test account (it can only verify the calling user's password).

### D26. SVG icons through the system librsvg (not a managed dependency)
- Most icon themes are SVG-only. Managed SVG renderers are large and reflection-heavy (AOT warnings); librsvg and cairo are on every desktop Linux. `SvgRenderer.Render(path, w, h)` loads them at run time and returns premultiplied BGRA; `IsAvailable` is false where they are missing, so nothing else breaks.

### D27. Volume through libpulse
- PipeWire serves the PulseAudio protocol, so one small libpulse binding covers both servers. A `pa_threaded_mainloop` thread does the I/O; snapshots (`AudioSink` records) are posted to the UI thread. Struct fields are read at offsets checked with `offsetof` against `<pulse/introspect.h>` (LP64). Native PipeWire API would need far more interop for the same result.

### D28. `oopshell.Services.Media` (MPRIS)
- Players are found by bus name (`org.mpris.MediaPlayer2.*`, via ListNames + NameOwnerChanged) and refreshed with GetAll when their unique name emits PropertiesChanged. `Active` is a playing player, else the one that changed last.

### D29. Layer-surface state changes are committed at once
- Setting a `LayerSurface` property used to wait for the next frame, and a pending frame callback could delay it past later requests: on niri a menu's popup grab then saw the old keyboard interactivity. Changes made outside a render now get a state-only commit right away (inside a render, the frame's commit carries them).

### D30. `ScreenCapture`: windows and outputs through ext-image-copy-capture
- The standard protocols, not portals: xdg-desktop-portal's ScreenCast is for sandboxed apps (a consent dialog, PipeWire streams); a shell asks the compositor directly. `ScreenCapture.ForWindow/ForOutput` keep one capture session each; `CaptureAsync` copies a frame into shared memory (ARGB/XRGB8888) and returns the pixels; a buffer-constraints failure (size change) is retried once with the new constraints.
- Driven by callbacks from the Wayland events, not `await`s inside the library: a `ConfigureAwait(false)` continuation is not run inline when the completing thread has a synchronization context (the UI thread always does), so it would resume on the thread pool and touch thread-affine Wayland objects.
- Window capture needs `ext_foreign_toplevel_handle_v1`s while actions need the wlr handles. When the compositor offers window capture and both lists, oopshell binds both and pairs each ext handle with the wlr window with the same app id and title (in announcement order for identical ones). With the ext list alone, the handles are used directly.
- Tested against `tests/fixtures/capturewl` (tinywl + wlroots 0.20 capture sources, optional wlr toplevel list and minimal layer-shell), because no packaged compositor here implements window capture yet. wlroots' scene-node capture renders whatever covers the window's area, so the fixture keeps windows apart; real compositors differ.

### D31. `oopshell.Services.Launcher`
- Every shell needs the same launcher plumbing, and both demos had their own (OopsOSX's search and 30-second reload, oops95's reload per menu open, two copies of terminal handling hard-coded to foot). The package is the shared version: `AppCatalog` (live: FileSystemWatcher on every applications dir, or on its parent until it exists, debounced on the UI thread), `AppSearch`, `UsageHistory`, `Terminal`, `AppLauncher`, `MimeDatabase` and `DefaultApps`. It sits on top of `Services.Desktop`, which stays the plain spec layer (parsing, icon lookup).
- Search folds text itself (accents, full-width forms, hiragana→katakana): under `InvariantGlobalization`, which shells and the demos use, `string.Normalize` returns its input unchanged. Ranking tiers (exact, prefix, word starts, initials, substring, letters in order) over the name, the untranslated name (a Japanese desktop still finds "Firewall" by "fire"), generic name, keywords, program and id; actions match as "App Action", in either language; usage adds up to 20.
- Terminal=true apps use, in order: an explicit choice, `$TERMINAL`, `xdg-terminal-exec`, then common terminals with their own conventions (`-e`, `--`, `-x`, none). Opening files follows the mime-apps spec (desktop-specific lists first, config then data dirs, defaults, added associations, declaring apps, removals, MIME parents) with shared-mime-info globs; `xdg-open` only when nothing claims the type.
- Tested with fixtures in the layouts distributions really ship (Flatpak `@@u %u @@`, Snap `env …` wrappers, NixOS store symlinks of files and of whole directories, Steam URLs, AppImage paths with spaces, BOM/CRLF files, Hidden overrides, TryExec, OnlyShowIn), plus a smoke test that every visible application on the build machine expands its Exec, is found by its own name and by its window's app id.

### D32. Background blur (ext-background-effect)
- `Surface.BlurRegion` (a `Region`, like the input and opaque regions, sent with the next commit) and `OopsApp.CanBlurBehind` + `CanBlurBehindChanged` from the manager's capabilities. `Region.All` covers a whole surface; the compositor clips it.
- The per-surface protocol object is created only when a surface first asks for blur, so surfaces that never blur cost nothing, and it is destroyed before the surface. Without the global, or without the blur capability, setting the region is a no-op rather than an error: blur is decoration, and a shell should not need two code paths to set it.
- The capability can change at runtime (a user turning blur off), so it is an event, not just a startup flag. Verified on niri 26.04 (Crossbar's overlay over a running app) and as a no-op on sway.

### D33. Async: tasks for operations, events for changes, everything on the UI thread
- A one-off operation (launching, capturing a frame, locking, authenticating, a D-Bus call) returns a `Task`; state that keeps changing (windows, outputs, players, volume, capabilities) is an event or a `Changed` notification. No public API takes completion callbacks.
- Every event, the services' included, is raised on the UI thread (services post from their own threads). Awaiting from the UI thread resumes there through the app's synchronization context, so application code never marshals.
- Inside the library, protocol exchanges complete their tasks from Wayland event callbacks rather than awaiting each other (D30): the rule is about the public surface, which stays uniform.

### D34. IPC replies carry a status
- `IpcServer.Send` returns `IpcReply(Status, Text)`: `Ok` with the handler's reply, `Failed` with the message of the exception the handler threw, or `NotRunning`. On the wire the server prefixes its answer with one status byte.
- It used to return the reply text, or null when not running, with failures as text starting "error: ". A failure could not be told from a reply that looked like one, and a caller that only checked for null treated failures as success (this hid a real bug in a demo's toggle command).

### D35. Clock timers on the wall clock
- `SetTimer` measures on CLOCK_MONOTONIC, which stops during suspend: a clock refreshed by a one-minute timer showed the time from before the suspend for up to a minute after resume (both demos did exactly this). `SetClockTimer(interval, callback)` fires on every multiple of the interval of local time, from an absolute CLOCK_REALTIME deadline that is re-armed after each tick: a deadline that passed while suspended fires on resume, and `TFD_TIMER_CANCEL_ON_SET` fires it as soon as the clock is set (NTP step, manual change), after which it realigns.
- Boundaries are computed in local time, so hour and day intervals follow the time zone and DST. A change of time zone while running is not noticed (.NET caches the zone until restart); a shell that travels can call `TimeZoneInfo.ClearCachedData()` when it learns of one.
