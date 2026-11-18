namespace oopshell.Scanner;

/// <summary>A parsed protocol XML file. Plain data; no Roslyn types.</summary>
public sealed class Protocol
{
    public string Name = "";
    public List<Interface> Interfaces = new();
}

public sealed class Interface
{
    public string Name = "";
    public int Version;
    public Description? Description;
    public List<Message> Requests = new();
    public List<Message> Events = new();
    public List<EnumDef> Enums = new();
}

public sealed class Message
{
    public string Name = "";
    public bool IsDestructor;
    public int Since = 1;
    public Description? Description;
    public List<Arg> Args = new();
}

public enum ArgType { Int, Uint, Fixed, String, Object, NewId, Array, Fd }

public sealed class Arg
{
    public string Name = "";
    public ArgType Type;
    /// <summary>The referenced interface name for object/new_id args, if any.</summary>
    public string? Interface;
    public bool AllowNull;
    /// <summary>The referenced enum, as written ("wl_output.transform" or "transform").</summary>
    public string? Enum;
    public string? Summary;
}

public sealed class EnumDef
{
    public string Name = "";
    public bool IsBitfield;
    public Description? Description;
    public List<EnumEntry> Entries = new();
}

public sealed class EnumEntry
{
    public string Name = "";
    public string Value = "";
    public string? Summary;
}

public sealed class Description
{
    public string? Summary;
    public string? Text;
}
