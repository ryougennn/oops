using Tmds.DBus.Protocol;

namespace oopshell.Services.DBus;

/// <summary>
/// Type-checked reads of values sent by other processes. A wrong type yields null instead of throwing, so a
/// buggy or hostile peer cannot crash the shell. Integers convert between widths when they fit; nested
/// variants are unwrapped.
/// </summary>
public static class Variants
{
    private static VariantValue Unwrap(VariantValue v) => v.Type == VariantValueType.Variant ? Unwrap(v.GetVariantValue()) : v;

    public static string? AsString(this VariantValue value)
    {
        var v = Unwrap(value);
        return v.Type switch
        {
            VariantValueType.String => v.GetString(),
            VariantValueType.ObjectPath => v.GetObjectPathAsString(),
            _ => null,
        };
    }

    public static bool? AsBool(this VariantValue value)
    {
        var v = Unwrap(value);
        return v.Type == VariantValueType.Bool ? v.GetBool() : null;
    }

    /// <summary>Any integer type, as a long (UInt64 values above long.MaxValue give null).</summary>
    public static long? AsInt64(this VariantValue value)
    {
        var v = Unwrap(value);
        return v.Type switch
        {
            VariantValueType.Byte => v.GetByte(),
            VariantValueType.Int16 => v.GetInt16(),
            VariantValueType.UInt16 => v.GetUInt16(),
            VariantValueType.Int32 => v.GetInt32(),
            VariantValueType.UInt32 => v.GetUInt32(),
            VariantValueType.Int64 => v.GetInt64(),
            VariantValueType.UInt64 => v.GetUInt64() <= long.MaxValue ? (long)v.GetUInt64() : null,
            _ => null,
        };
    }

    /// <summary>An integer that fits in an int.</summary>
    public static int? AsInt32(this VariantValue value) => value.AsInt64() is long l and >= int.MinValue and <= int.MaxValue ? (int)l : null;

    /// <summary>A non-negative integer that fits in a uint.</summary>
    public static uint? AsUInt32(this VariantValue value) => value.AsInt64() is long l and >= 0 and <= uint.MaxValue ? (uint)l : null;

    /// <summary>A double, or any integer as one.</summary>
    public static double? AsDouble(this VariantValue value)
    {
        var v = Unwrap(value);
        return v.Type == VariantValueType.Double ? v.GetDouble() : v.AsInt64();
    }

    /// <summary>An array of strings (as), or null.</summary>
    public static string[]? AsStrings(this VariantValue value)
    {
        var v = Unwrap(value);
        return v.Type == VariantValueType.Array && v.ItemType == VariantValueType.String ? v.GetArray<string>() : null;
    }

    /// <summary>A byte array (ay), or null.</summary>
    public static byte[]? AsBytes(this VariantValue value)
    {
        var v = Unwrap(value);
        return v.Type == VariantValueType.Array && v.ItemType == VariantValueType.Byte ? v.GetArray<byte>() : null;
    }

    /// <summary>An object path array (ao) as strings, or null.</summary>
    public static string[]? AsObjectPaths(this VariantValue value)
    {
        var v = Unwrap(value);
        return v.Type == VariantValueType.Array && v.ItemType == VariantValueType.ObjectPath
            ? v.GetArray<ObjectPath>().Select(p => p.ToString()).ToArray()
            : null;
    }

    /// <summary>Item <paramref name="index"/> of a struct, or null if it is not a struct that long.</summary>
    public static VariantValue? Field(this VariantValue value, int index)
    {
        var v = Unwrap(value);
        if (v.Type == VariantValueType.Struct && index < v.Count)
            return v.GetItem(index);
        return null;
    }

    /// <summary>A string-keyed dictionary (a{sv} or a{ss}…), or null.</summary>
    public static Dictionary<string, VariantValue>? AsDictionary(this VariantValue value)
    {
        var v = Unwrap(value);
        if (v.Type != VariantValueType.Dictionary || v.KeyType != VariantValueType.String)
            return null;
        var result = new Dictionary<string, VariantValue>();
        for (int i = 0; i < v.Count; i++)
        {
            var entry = v.GetDictionaryEntry(i);
            result[entry.Key.GetString()] = entry.Value;
        }
        return result;
    }

    /// <summary>A string cut to <paramref name="max"/> characters (peers can send megabytes).</summary>
    public static string Clip(this string s, int max) => s.Length <= max ? s : s[..max];
}
