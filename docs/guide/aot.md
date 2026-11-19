# Publishing with NativeAOT

```xml
<PropertyGroup>
  <PublishAot Condition="'$(Configuration)' == 'Release'">true</PublishAot>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

```sh
dotnet publish -c Release -r linux-x64      # or linux-arm64
```

Needs `clang` and `lld` on the build machine. The result is a single executable (plus
`libSkiaSharp.so` if you use Skia). Every oopshell package is trimmable and AOT-clean; a sample with
zero warnings is about 1.7–2.2 MB, a full desktop shell about 5.4 MB.

**Keep `PublishAot` Release-only.** Set unconditionally, it also disables hot reload in Debug builds
(see [hot-reload.md](hot-reload.md)).

What to avoid in your own shell code: reflection-based JSON (use `JsonSerializerContext`),
`Activator.CreateInstance(Type)`, `dynamic`, `Expression.Compile`. Native callbacks must be
`[UnmanagedCallersOnly]` functions passed as function pointers.

Runtime libraries needed on the target: `libwayland-client.so.0`, `libxkbcommon.so.0`; optionally
`libEGL.so.1` + `libwayland-egl.so.1` (GPU), `libwayland-cursor.so.0` (cursor theme fallback).
