using System.Text;

namespace oopshell.Services.Launcher;

/// <summary>
/// Folds text for matching: lower case, Latin accents removed (é → e, ß → ss), full-width ASCII to ASCII
/// (ｆｉｒｅ → fire), hiragana to katakana (ふぁいる → ファイル). Works the same with
/// <c>InvariantGlobalization</c>, where <see cref="string.Normalize()"/> does nothing.
/// </summary>
public static class TextFold
{
    static readonly Dictionary<char, string> Latin = Build(
        ("àáâãäåāăąǎǻ", "a"), ("æǽ", "ae"), ("çćĉċč", "c"), ("ďđð", "d"), ("èéêëēĕėęě", "e"), ("ĝğġģ", "g"),
        ("ĥħ", "h"), ("ìíîïĩīĭįıǐ", "i"), ("ĳ", "ij"), ("ĵ", "j"), ("ķ", "k"), ("ĺļľŀł", "l"), ("ñńņňŉŋ", "n"),
        ("òóôõöøōŏőǒǿ", "o"), ("œ", "oe"), ("ŕŗř", "r"), ("śŝşšș", "s"), ("ß", "ss"), ("ţťŧț", "t"), ("þ", "th"),
        ("ùúûüũūŭůűųǔǖǘǚǜ", "u"), ("ŵ", "w"), ("ýÿŷ", "y"), ("źżž", "z"));

    static Dictionary<char, string> Build(params (string Chars, string To)[] groups)
    {
        var map = new Dictionary<char, string>();
        foreach (var (chars, to) in groups)
            foreach (char c in chars)
                map[c] = to;
        return map;
    }

    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char raw in text)
        {
            char c = raw;
            if (c is >= '！' and <= '～')
                c = (char)(c - 0xFEE0);            // full-width ASCII
            else if (c == '　')
                c = ' ';                           // ideographic space
            else if (c is >= 'ぁ' and <= 'ゖ')
                c = (char)(c + 0x60);              // hiragana → katakana
            c = char.ToLowerInvariant(c);
            if (c < 0x80)
                sb.Append(c);
            else if (Latin.TryGetValue(c, out var folded))
                sb.Append(folded);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}
