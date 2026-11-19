using System.Text;

namespace oopshell.Fuzz;

/// <summary>Mutates seed inputs: flips, inserts, deletes, duplicates, splices format tokens, truncates.</summary>
internal sealed class Mutator(int seed, IReadOnlyList<string> tokens)
{
    public readonly Random Random = new(seed);

    private static readonly string[] s_generic =
    [
        "\n", "\r\n", "\0", "\\", "\"", "'", "=", ";", "[", "]", "%", "%f", "%F", "%u", "%U", "%i", "%c", "%k", "%%", "$", "$(", "`",
        "..", "/", "~", " ", "\t", "é", "日本", "😀", "\ud800", "\udc00", "‮", "﻿", "-", "--", "*", "?",
        "0", "-1", "4294967295", "9999999999999999999", "NaN", "true", "{", "}", ":", ",", "#",
    ];

    public string Mutate(string input)
    {
        var s = new StringBuilder(input);
        int rounds = 1 + Random.Next(6);
        for (int r = 0; r < rounds; r++)
        {
            int at = s.Length == 0 ? 0 : Random.Next(s.Length + 1);
            switch (Random.Next(9))
            {
                case 0 when s.Length > 0: s[Math.Min(at, s.Length - 1)] = (char)Random.Next(0x10000); break;
                case 1: s.Insert(at, Token()); break;
                case 2 when s.Length > 0: s.Remove(Math.Min(at, s.Length - 1), Math.Min(1 + Random.Next(32), s.Length - Math.Min(at, s.Length - 1))); break;
                case 3 when s.Length > 0:
                    int start = Random.Next(s.Length), len = Math.Min(1 + Random.Next(64), s.Length - start);
                    s.Insert(at, s.ToString(start, len));
                    break;
                case 4: s.Insert(at, new string(Token()[0], Random.Next(5000))); break; // long runs
                case 5 when s.Length > 0: s.Length = Random.Next(s.Length); break;
                case 6: s.Insert(at, (char)Random.Next(0x20)); break;
                case 7 when s.Length > 0: s[Math.Min(at, s.Length - 1)] = (char)(s[Math.Min(at, s.Length - 1)] ^ (1 << Random.Next(16))); break;
                default: s.Insert(at, Token()); break;
            }
        }
        return s.ToString();
    }

    private string Token() => Random.Next(3) == 0 ? s_generic[Random.Next(s_generic.Length)] : tokens.Count > 0 ? tokens[Random.Next(tokens.Count)] : "x";

    public byte[] MutateBytes(byte[] input)
    {
        var b = new List<byte>(input);
        for (int r = 0, rounds = 1 + Random.Next(6); r < rounds; r++)
        {
            int at = b.Count == 0 ? 0 : Random.Next(b.Count + 1);
            switch (Random.Next(5))
            {
                case 0 when b.Count > 0: b[Math.Min(at, b.Count - 1)] ^= (byte)(1 << Random.Next(8)); break;
                case 1: b.InsertRange(at, Encoding.UTF8.GetBytes(Token())); break;
                case 2 when b.Count > 0: b.RemoveRange(Math.Min(at, b.Count - 1), Math.Min(1 + Random.Next(16), b.Count - Math.Min(at, b.Count - 1))); break;
                case 3: b.InsertRange(at, Enumerable.Range(0, Random.Next(64)).Select(_ => (byte)Random.Next(256))); break;
                default: b.Insert(at, (byte)Random.Next(256)); break;
            }
        }
        return b.ToArray();
    }
}
