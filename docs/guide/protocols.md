# Your own protocol bindings

The `oopshell.Protocol` package includes the scanner. Add XML and get C# at compile time:

```xml
<ItemGroup>
  <WaylandProtocol Include="protocols/my-protocol.xml" />
</ItemGroup>
<PropertyGroup>
  <WaylandProtocolNamespace>MyShell.Protocols</WaylandProtocolNamespace> <!-- default: RootNamespace -->
</PropertyGroup>
```

References to interfaces and enums from oopshell's own protocols (`wl_surface`, `wl_output.transform`,
…) resolve automatically. An unknown reference is error OOPS002; unreadable XML is OOPS001.

## What is generated

* One `sealed partial class` per interface (`zwlr_layer_surface_v1` → `ZwlrLayerSurfaceV1`) deriving
  from `WlProxy`, with `InterfaceName`, `MaxVersion`, `BoundVersion`.
* Requests as methods: enums typed, `fixed` as `double`, strings as `string`, arrays as
  `ReadOnlySpan<byte>`, fds as `int` (libwayland dups them), `new_id` returned as the new proxy. Calling
  a request above the bound version throws `WaylandVersionException`. The destructor maps to `Dispose()`.
* Events as `event Action<XxxEvent>`: `XxxEvent` is a `ref struct` read lazily from the native
  arguments: no allocation unless you read a string. Only valid inside the handler. Event fds are
  closed after the handler: `dup` them to keep them.
* Enums (`[Flags]` for bitfields) named `{Interface}{Enum}`.

## Using them

```csharp
var manager = app.Get<MyManagerV1>();          // binds min(advertised, MaxVersion)
var thing = manager.GetThing(surface.Proxy);
thing.Changed += e => Console.WriteLine(e.Value);
```

`app.Bind<T>(global)` binds multi-instance globals yourself. Everything is reflection-free and
NativeAOT-safe.
