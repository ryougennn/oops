# Hot reload

Develop under the JIT with `dotnet watch`; ship NativeAOT binaries. oopshell connects the two:
when .NET Hot Reload applies an edit, oopshell (on the UI thread):

1. raises `app.HotReloadCacheClear` and then `app.HotReloaded(Type[]? updatedTypes)`: rebuild anything you
   cached from code (menus built from data, measured layouts, compiled paths);
2. invalidates **every** surface with full damage, so edited drawing code is visible immediately.

```csharp
app.HotReloaded += _ => menu = BuildMenu();   // optional
```

## Setup

```sh
dotnet watch run --project samples/HelloSkia
```

Edit a color or a drawing method in `Scene.Draw` and save: the window redraws without restarting.
Run your shell from a terminal inside the compositor session (or from a nested compositor) so it
connects to the right `WAYLAND_DISPLAY`.

### Do not set `PublishAot` unconditionally

`<PublishAot>true</PublishAot>` also writes `System.Reflection.Metadata.MetadataUpdater.IsSupported=false`
and `System.StartupHookProvider.IsSupported=false` into **Debug** builds' `runtimeconfig.json`, which
silently disables hot reload (`dotnet watch` waits forever for the app to connect). Make it Release-only:

```xml
<PublishAot Condition="'$(Configuration)' == 'Release'">true</PublishAot>
```

and publish with `dotnet publish -c Release -r linux-x64` (or pass `-p:PublishAot=true`).

### Recommended `Properties/launchSettings.json`

```json
{
  "profiles": {
    "shell": {
      "commandName": "Project",
      "hotReloadEnabled": true,
      "environmentVariables": { "DOTNET_WATCH_RESTART_ON_RUDE_EDIT": "true" }
    }
  }
}
```

`DOTNET_WATCH_RESTART_ON_RUDE_EDIT` makes `dotnet watch` restart the shell automatically when an edit
cannot be applied in place (instead of asking).

## Dev overlay

`OopsApp.Connect(new OopsOptions { DevOverlay = true })` flashes a small magenta square in the top-right
corner of every surface for 0.6 s after each reload, so you can tell a reload happened even when your
edit changed nothing visible. It is drawn by `ShmSwapchain` and `EglWindow`; raw-handle renderers do
not get it. Off by default.

## What reloads in place, and what restarts

Applied live (typical): method body edits, including lambdas and local functions; adding methods,
fields, properties and types; changing constants used in drawing code; adding `using`s.

Needs a restart ("rude edit"): changing a method signature that is in use, changing a type's base
class or interfaces, editing generic methods in some cases, editing `struct` layouts, changing
attributes that the runtime already acted on, editing the `Main`/top-level statements that already
ran (the new code only runs after a restart), and anything in a `.csproj`.

## Restarts are clean

On a restart the old process exits and its Wayland connection closes, so the compositor destroys all
of its surfaces: layer surfaces and their exclusive zones disappear, then reappear when the new process
starts. Two things deserve care:

- **Session lock**: if the shell is *locked* when it restarts, the compositor keeps the session locked
  (by design, see `SessionLock`). Don't hot-reload a locker while locked; test lock screens in a nested
  or headless compositor.
- **Single-instance sockets** (like `oops95`'s control socket): delete a stale socket file at startup.

Hot reload does not exist under NativeAOT; the handler is inert there and produces no trim warnings.
