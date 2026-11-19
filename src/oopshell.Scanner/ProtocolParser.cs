using System.Xml;
using System.Xml.Linq;

namespace oopshell.Scanner;

/// <summary>Turns protocol XML into a <see cref="Protocol"/>. Throws <see cref="ProtocolFormatException"/> on bad input.</summary>
public static class ProtocolParser
{
    public static Protocol Parse(string xml)
    {
        XElement root;
        try
        {
            root = XDocument.Parse(xml, LoadOptions.SetLineInfo).Root ?? throw new ProtocolFormatException("empty document");
        }
        catch (XmlException e)
        {
            throw new ProtocolFormatException(e.Message);
        }

        if (root.Name.LocalName != "protocol")
            throw new ProtocolFormatException($"root element is <{root.Name.LocalName}>, expected <protocol>");

        var protocol = new Protocol { Name = Required(root, "name") };
        foreach (var element in root.Elements("interface"))
            protocol.Interfaces.Add(ParseInterface(element));
        return protocol;
    }

    private static Interface ParseInterface(XElement element)
    {
        var iface = new Interface
        {
            Name = Required(element, "name"),
            Version = ParseInt(Required(element, "version"), element),
            Description = ParseDescription(element),
        };
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "request": iface.Requests.Add(ParseMessage(child)); break;
                case "event": iface.Events.Add(ParseMessage(child)); break;
                case "enum": iface.Enums.Add(ParseEnum(child)); break;
            }
        }
        return iface;
    }

    private static Message ParseMessage(XElement element)
    {
        var message = new Message
        {
            Name = Required(element, "name"),
            IsDestructor = (string?)element.Attribute("type") == "destructor",
            Since = element.Attribute("since") is { } since ? ParseInt(since.Value, element) : 1,
            Description = ParseDescription(element),
        };
        foreach (var argElement in element.Elements("arg"))
        {
            message.Args.Add(new Arg
            {
                Name = Required(argElement, "name"),
                Type = ParseArgType(Required(argElement, "type"), argElement),
                Interface = (string?)argElement.Attribute("interface"),
                AllowNull = (string?)argElement.Attribute("allow-null") == "true",
                Enum = (string?)argElement.Attribute("enum"),
                Summary = (string?)argElement.Attribute("summary"),
            });
        }
        return message;
    }

    private static EnumDef ParseEnum(XElement element)
    {
        var def = new EnumDef
        {
            Name = Required(element, "name"),
            IsBitfield = (string?)element.Attribute("bitfield") == "true",
            Description = ParseDescription(element),
        };
        foreach (var entry in element.Elements("entry"))
        {
            def.Entries.Add(new EnumEntry
            {
                Name = Required(entry, "name"),
                Value = Required(entry, "value"),
                Summary = (string?)entry.Attribute("summary"),
            });
        }
        return def;
    }

    private static Description? ParseDescription(XElement element)
    {
        var description = element.Element("description");
        if (description is null)
            return null;
        return new Description { Summary = (string?)description.Attribute("summary"), Text = description.Value };
    }

    private static ArgType ParseArgType(string type, XElement element) => type switch
    {
        "int" => ArgType.Int,
        "uint" => ArgType.Uint,
        "fixed" => ArgType.Fixed,
        "string" => ArgType.String,
        "object" => ArgType.Object,
        "new_id" => ArgType.NewId,
        "array" => ArgType.Array,
        "fd" => ArgType.Fd,
        _ => throw new ProtocolFormatException($"unknown arg type '{type}'{Where(element)}"),
    };

    private static string Required(XElement element, string attribute) =>
        (string?)element.Attribute(attribute)
        ?? throw new ProtocolFormatException($"<{element.Name.LocalName}> is missing the '{attribute}' attribute{Where(element)}");

    private static int ParseInt(string text, XElement element) =>
        int.TryParse(text, out int value) ? value : throw new ProtocolFormatException($"'{text}' is not a number{Where(element)}");

    private static string Where(XElement element) =>
        element is IXmlLineInfo info && info.HasLineInfo() ? $" (line {info.LineNumber})" : "";
}

public sealed class ProtocolFormatException(string message) : Exception(message);
