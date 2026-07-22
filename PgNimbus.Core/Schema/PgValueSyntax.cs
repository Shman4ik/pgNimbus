using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

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
    /// Error message for a value that isn't well-formed JSON, or null when it
    /// parses (or is blank — a blank cell defers to the server / column default).
    /// Postgres remains the real parser via the statement's <c>CAST(… AS jsonb)</c>;
    /// this only front-runs the obvious mistakes (a stray comma, an unclosed
    /// brace) so they surface in the editor instead of as a failed statement.
    /// json/jsonb both accept a bare scalar (<c>42</c>, <c>"hi"</c>, <c>true</c>,
    /// <c>null</c>), so this validates any JSON value, not just objects/arrays.
    /// </summary>
    public static string? ValidateJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            // AllowTrailingCommas stays off on purpose: Postgres rejects them too,
            // so catching them here keeps the client check honest.
            using var _ = JsonDocument.Parse(text);
            return null;
        }
        catch (JsonException ex)
        {
            // JsonException carries 0-based line/byte positions; present 1-based
            // and only when known (they're null for some low-level failures).
            var where = ex.LineNumber is { } line && ex.BytePositionInLine is { } pos
                ? $" (line {line + 1}, position {pos + 1})"
                : string.Empty;
            return $"Not valid JSON{where}.";
        }
    }

    /// <summary>
    /// Cheap client-side type check for a hand-typed scalar value against its
    /// column's declared Postgres type — the numeric and uuid families where an
    /// obviously wrong value (letters in an integer, a malformed UUID) is worth
    /// catching in the editor instead of as a server error after INSERT. Returns
    /// an error message, or null when the value is fine, blank, or the type has
    /// no client-side check (text, json, ranges, inet, … — Postgres stays the
    /// real parser via the statement's CAST). <paramref name="dataType"/> is the
    /// column's declared type as <c>format_type</c> renders it (e.g. "integer",
    /// "numeric(10,2)", "uuid"); for a domain column, pass its resolved base type.
    /// </summary>
    public static string? ValidateScalar(string dataType, string text)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        // Strip a length/precision modifier ("numeric(10,2)" → "numeric") and
        // any schema qualifier, then normalize. Array types ("integer[]") reach
        // this only through a broken classification — they have their own
        // editor/validator — so defer rather than validate the element type.
        if (dataType.Contains('['))
        {
            return null;
        }

        var type = dataType;
        var paren = type.IndexOf('(');
        if (paren >= 0)
        {
            type = type[..paren];
        }

        var dot = type.LastIndexOf('.');
        if (dot >= 0)
        {
            type = type[(dot + 1)..];
        }

        type = type.Trim().ToLowerInvariant();

        return type switch
        {
            "smallint" or "int2" => ValidateInteger(value, short.MinValue, short.MaxValue, "smallint"),
            "integer" or "int" or "int4" => ValidateInteger(value, int.MinValue, int.MaxValue, "integer"),
            "bigint" or "int8" => ValidateInteger(value, long.MinValue, long.MaxValue, "bigint"),
            "real" or "float4" => ValidateFloatingPoint(value, "real"),
            "double precision" or "float8" => ValidateFloatingPoint(value, "double precision"),
            "numeric" or "decimal" => ValidateFloatingPoint(value, "numeric"),
            "uuid" => Guid.TryParse(value, out _) ? null : $"'{value}' is not a valid UUID.",
            _ => null,
        };
    }

    private static string? ValidateInteger(string value, long min, long max, string label)
    {
        if (!BigInteger.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            return $"'{value}' is not a valid {label} — a whole number is expected.";
        }

        if (parsed < min || parsed > max)
        {
            return $"{value} is out of range for {label} ({min} to {max}).";
        }

        return null;
    }

    // Special numeric inputs Postgres accepts across the float/numeric family.
    private static readonly string[] SpecialNumericValues =
        ["nan", "inf", "-inf", "+inf", "infinity", "-infinity", "+infinity"];

    private static string? ValidateFloatingPoint(string value, string label)
    {
        if (Array.Exists(SpecialNumericValues, s => s.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        // double.TryParse validates the *syntax* (sign, decimal point, exponent);
        // it may round a high-precision numeric, but that never matters here —
        // the actual value is still parsed exactly by Postgres via the CAST.
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return $"'{value}' is not a valid {label} number.";
        }

        return null;
    }

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

    /// <summary>
    /// Renders an hstore value (Npgsql materializes it as a
    /// <c>Dictionary&lt;string,string&gt;</c>, whose default ToString is the CLR
    /// type name) in Postgres's own literal syntax — <c>"k"=&gt;"v", "k2"=&gt;NULL</c>
    /// — so the grid shows a readable value everywhere, not only in browse mode's
    /// text-format path. Both key and value are always double-quoted (the form
    /// Postgres itself emits); a null value is the bare keyword <c>NULL</c>.
    /// </summary>
    public static string FormatHstore(System.Collections.IDictionary map)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            if (!first)
            {
                sb.Append(", ");
            }

            first = false;
            AppendHstoreString(sb, entry.Key.ToString() ?? string.Empty);
            sb.Append("=>");
            if (entry.Value is null)
            {
                sb.Append("NULL");
            }
            else
            {
                AppendHstoreString(sb, entry.Value.ToString() ?? string.Empty);
            }
        }

        return sb.ToString();
    }

    private static void AppendHstoreString(StringBuilder sb, string text) =>
        sb.Append('"').Append(text.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');

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
