using System.Runtime.InteropServices;
using System.Text;
using oopshell.Native;

namespace oopshell.Protocol;

/// <summary>Wayland's 24.8 signed fixed-point numbers.</summary>
public static class WlFixed
{
    public static double ToDouble(int value) => value / 256.0;
    public static int FromDouble(double value) => (int)Math.Round(value * 256.0);
}

/// <summary>A null-terminated UTF-8 copy of a string in native memory, freed on dispose.</summary>
public readonly unsafe struct NativeUtf8 : IDisposable
{
    public NativeUtf8(string? value) => Pointer = value is null ? null : WlInterfaceTable.Utf8(value);

    public byte* Pointer { get; }

    public void Dispose() => NativeMemory.Free(Pointer);
}

/// <summary>Builds <c>wl_interface</c>/<c>wl_message</c> structs in native memory. Used by generated code.</summary>
public static unsafe class WlInterfaceTable
{
    public readonly struct MessageInfo(string name, string signature, WlInterface*[] types)
    {
        public string Name { get; } = name;
        public string Signature { get; } = signature;
        public WlInterface*[] Types { get; } = types;
    }

    public static MessageInfo Message(string name, string signature, params WlInterface*[] types) => new(name, signature, types);

    public static WlInterface* Allocate() => (WlInterface*)NativeMemory.AllocZeroed((nuint)sizeof(WlInterface));

    /// <summary>Fills an interface allocated by <see cref="Allocate"/>. Never freed: tables live as long as the process.</summary>
    public static void Fill(WlInterface* iface, string name, int version, MessageInfo[] requests, MessageInfo[] events, (uint Code, string Name)[]? errors = null)
    {
        if (errors is not null)
        {
            lock (s_errorNames)
                s_errorNames[name] = errors;
        }
        iface->Name = Utf8(name);
        iface->Version = version;
        iface->MethodCount = requests.Length;
        iface->Methods = Messages(requests);
        iface->EventCount = events.Length;
        iface->Events = Messages(events);
    }

    private static readonly Dictionary<string, (uint Code, string Name)[]> s_errorNames = new();

    /// <summary>The name of an interface's <c>error</c> enum entry, if the bindings for it have been loaded.</summary>
    public static string? ErrorName(string iface, uint code)
    {
        lock (s_errorNames)
        {
            if (s_errorNames.TryGetValue(iface, out var entries))
                foreach (var entry in entries)
                    if (entry.Code == code)
                        return entry.Name;
        }
        return null;
    }

    private static WlMessage* Messages(MessageInfo[] infos)
    {
        var messages = (WlMessage*)NativeMemory.AllocZeroed((nuint)(Math.Max(infos.Length, 1) * sizeof(WlMessage)));
        for (int i = 0; i < infos.Length; i++)
        {
            messages[i].Name = Utf8(infos[i].Name);
            messages[i].Signature = Utf8(infos[i].Signature);
            int argCount = infos[i].Signature.Count(char.IsAsciiLetter);
            var types = (WlInterface**)NativeMemory.AllocZeroed((nuint)(Math.Max(argCount, 1) * sizeof(WlInterface*)));
            for (int t = 0; t < infos[i].Types.Length; t++)
                types[t] = infos[i].Types[t];
            messages[i].Types = types;
        }
        return messages;
    }

    /// <summary>A null-terminated UTF-8 copy in native memory. The caller frees it with <see cref="NativeMemory.Free"/>.</summary>
    public static byte* Utf8(string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)length + 1);
        Encoding.UTF8.GetBytes(value, new Span<byte>(buffer, length));
        buffer[length] = 0;
        return buffer;
    }
}

/// <summary>Marks an assembly that contains generated bindings, so other projects' scanners can reference its interfaces.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class WaylandProtocolsAttribute(string @namespace, string names) : Attribute
{
    public string Namespace { get; } = @namespace;
    /// <summary>Space-separated interface names and <c>interface.enum</c> names.</summary>
    public string Names { get; } = names;
}

/// <summary>A request was called on an object bound below the request's <c>since</c> version.</summary>
public sealed class WaylandVersionException(string iface, string request, uint since, uint bound)
    : InvalidOperationException($"{iface}.{request} needs version {since}, but the object is bound at version {bound}.")
{
    public string Interface { get; } = iface;
    public string Request { get; } = request;
    public uint Since { get; } = since;
    public uint BoundVersion { get; } = bound;
}

/// <summary>The compositor sent a protocol error; the connection is dead.</summary>
public sealed class WaylandProtocolException(string iface, uint objectId, uint code, string? codeName)
    : Exception($"Wayland protocol error on {iface}@{objectId}: {codeName ?? "code"} ({code})")
{
    public string Interface { get; } = iface;
    public uint ObjectId { get; } = objectId;
    public uint Code { get; } = code;
    public string? CodeName { get; } = codeName;
}

/// <summary>The Wayland connection failed or was lost.</summary>
public sealed class WaylandConnectionException(string message) : IOException(message);
