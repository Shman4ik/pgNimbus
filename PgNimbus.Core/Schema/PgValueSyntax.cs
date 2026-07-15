using System.Globalization;
using System.Text;

namespace PgNimbus.Core.Schema;

/// <summary>
/// Cheap client-side syntax checks for the two Postgres literal shapes users
/// type by hand — arrays (<c>{1,2,3}</c>) and composites (<c>(1,abc)</c>) — so
/// a structurally malformed value is caught in the editor instead of surfacing
/// as a server error after the statement fires. Postgres remains the real
/// parser: these verify only the delimiter/quote structure, never element
/// types or counts.
/// </summary>
public static class PgValueSyntax
{
    /// <summary>Error message for a malformed array literal, or null when the structure is fine.</summary>
    public static string? ValidateArray(string text)
    {
        var trimmed = text.Trim();

        // An optional dimension prefix ("[1:3]={…}") is legal input — skip it.
        if (trimmed.StartsWith('['))
        {
            var eq = trimmed.IndexOf('=');
            if (eq < 0)
            {
                return "An array dimension prefix ('[…]') must be followed by '='.";
            }

            trimmed = trimmed[(eq + 1)..].TrimStart();
        }

        return Validate(trimmed, '{', '}', "An array");
    }

    /// <summary>Error message for a malformed composite (row) literal, or null when the structure is fine.</summary>
    public static string? ValidateComposite(string text) => Validate(text.Trim(), '(', ')', "A composite");

    /// <summary>
    /// Renders a CLR array (what Npgsql materializes an array column as) in
    /// Postgres's own literal syntax — <c>{a,b,c}</c>, elements quoted by the
    /// server's rules — so the grid shows a readable, *editable* value instead
    /// of "System.String[]", and an F2 edit round-trips through
    /// <c>CAST(text AS type[])</c> unchanged.
    /// </summary>
    public static string FormatArray(Array array)
    {
        var sb = new StringBuilder();
        AppendArray(sb, array);
        return sb.ToString();
    }

    private static void AppendArray(StringBuilder sb, Array array)
    {
        sb.Append('{');
        var first = true;
        foreach (var item in array)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            AppendElement(sb, item);
        }

        sb.Append('}');
    }

    private static void AppendElement(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                sb.Append("NULL");
                return;
            case Array nested:
                AppendArray(sb, nested);
                return;
        }

        var text = value switch
        {
            bool b => b ? "t" : "f",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        // Postgres quotes an element when the bare form would be ambiguous:
        // empty, the word NULL, or containing a delimiter/quote/backslash/space.
        var needsQuoting = text.Length == 0
            || text.Equals("NULL", StringComparison.OrdinalIgnoreCase)
            || text.AsSpan().ContainsAny(QuotedElementChars);

        if (needsQuoting)
        {
            sb.Append('"').Append(text.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        }
        else
        {
            sb.Append(text);
        }
    }

    private static readonly System.Buffers.SearchValues<char> QuotedElementChars =
        System.Buffers.SearchValues.Create("{},\"\\ \t\n\r");

    private static string? Validate(string trimmed, char open, char close, string kind)
    {
        if (trimmed.Length == 0 || trimmed[0] != open)
        {
            return $"{kind} literal must start with '{open}' (e.g. {(open == '{' ? "{1,2,3}" : "(1,abc)")}).";
        }

        var depth = 0;
        var inQuotes = false;

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];

            if (inQuotes)
            {
                // Inside a double-quoted element: backslash escapes the next
                // character, "" is an embedded quote, a lone " closes it.
                if (ch == '\\')
                {
                    i++;
                }
                else if (ch == '"')
                {
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == '"')
                    {
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }

                continue;
            }

            if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == open)
            {
                depth++;
            }
            else if (ch == close)
            {
                depth--;
                if (depth == 0 && i != trimmed.Length - 1)
                {
                    return $"Unexpected text after the closing '{close}'.";
                }

                if (depth < 0)
                {
                    return $"Unbalanced '{close}'.";
                }
            }
        }

        if (inQuotes)
        {
            return "Unterminated double-quoted section.";
        }

        if (depth != 0)
        {
            return $"{kind} literal must end with '{close}'.";
        }

        return null;
    }
}
