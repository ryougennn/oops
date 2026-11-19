using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.IntegrationTests;

/// <summary>
/// A stand-in for a system service's object: properties per interface (served through
/// org.freedesktop.DBus.Properties) and method handlers by name. Change a property with <see cref="Set"/>, which
/// also emits PropertiesChanged, as real services do.
/// </summary>
internal sealed class FakeDBusObject(OopsApp app, string path) : ExportedObject(app, path)
{
    public readonly Dictionary<string, Dictionary<string, VariantValue>> Interfaces = new();
    public readonly Dictionary<string, Action<MethodContext>> Methods = new();
    public readonly List<string> Calls = new();
    public DBusConnection? Connection;

    /// <summary>Replies with no body; usable directly as a <see cref="Methods"/> handler.</summary>
    public static new void ReplyEmpty(MethodContext context) => ExportedObject.ReplyEmpty(context);

    public FakeDBusObject With(string iface, Dictionary<string, VariantValue> properties)
    {
        Interfaces[iface] = properties;
        return this;
    }

    public void Set(string iface, string name, VariantValue value)
    {
        Interfaces[iface][name] = value;
        Connection?.EmitSignal(Path, "org.freedesktop.DBus.Properties", "PropertiesChanged", "sa{sv}as", (ref MessageWriter w) =>
        {
            w.WriteString(iface);
            w.WriteDictionary(new Dictionary<string, VariantValue> { [name] = value });
            w.WriteArray(Array.Empty<string>());
        });
    }

    protected override void HandleMethod(MethodContext context)
    {
        string member = context.Request.MemberAsString ?? "";
        Calls.Add(member);
        var reader = context.Request.GetBodyReader();
        if (context.Request.InterfaceAsString == "org.freedesktop.DBus.Properties")
        {
            string iface = reader.ReadString();
            if (!Interfaces.TryGetValue(iface, out var properties))
            {
                ReplyError(context, "org.freedesktop.DBus.Error.UnknownInterface", iface);
                return;
            }
            switch (member)
            {
                case "GetAll":
                    using (var w = context.CreateReplyWriter("a{sv}"))
                    {
                        w.WriteDictionary(properties);
                        context.Reply(w.CreateMessage());
                    }
                    return;
                case "Get":
                    using (var w = context.CreateReplyWriter("v"))
                    {
                        w.WriteVariant(properties[reader.ReadString()]);
                        context.Reply(w.CreateMessage());
                    }
                    return;
                case "Set":
                    string name = reader.ReadString();
                    var value = reader.ReadVariantValue();
                    ReplyEmpty(context);
                    Set(iface, name, value);
                    return;
            }
        }
        if (Methods.TryGetValue(member, out var handler))
            handler(context);
    }
}
