using Microsoft.CodeAnalysis.CSharp;

namespace oopshell.Scanner;

/// <summary>Maps protocol snake_case names to C# names.</summary>
public static class Names
{
    /// <summary><c>wl_surface</c> → <c>WlSurface</c>; <c>90</c> → <c>_90</c>.</summary>
    public static string Pascal(string snake)
    {
        var parts = snake.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
        string result = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
        return result.Length > 0 && char.IsDigit(result[0]) ? "_" + result : result;
    }

    /// <summary><c>buffer_scale</c> → <c>bufferScale</c>, escaping C# keywords.</summary>
    public static string Camel(string snake)
    {
        string pascal = Pascal(snake);
        string camel = char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
        if (camel == "args")
            return "args_";
        return SyntaxFacts.GetKeywordKind(camel) != SyntaxKind.None ? "@" + camel : camel;
    }
}
