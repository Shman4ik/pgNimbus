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
    /// <c>CAST(text AS type[])</c> unchanged. That needs the value's shape kept:
    /// a multi-dimensional array (a CLR <c>T[,]</c>) is written dimension by
    /// dimension, <c>{{1,2},{3,4}}</c>, and a <c>bytea[]</c> (a <c>byte[][]</c>)
    /// element by element as <c>\x</c>-hex.
    /// </summary>
    /// <param name="array">The array.</param>
    /// <param name="formatElement">
    /// How one non-null element is written before it is quoted, when the
    /// caller has its own spelling for some types (the grid writes dates the
    /// way Postgres prints them). Null, or a null answer, falls back to
    /// <see cref="InvariantText"/>.
    /// </param>
    /// <param name="maxLength">
    /// The most characters to write. The result is then the literal's first
    /// characters, never a different text, and the elements past them are not
    /// formatted at all: the grid asks for one more character than a cell
    /// shows, which is how it tells a whole literal from a longer one.
    /// </param>
    public static string FormatArray(Array array, Func<object, string?>? formatElement = null, int maxLength = int.MaxValue)
    {
        var sb = new LiteralBuilder(maxLength);
        AppendArray(sb, array, formatElement);
        return sb.ToString();
    }

    private static void AppendArray(LiteralBuilder sb, Array array, Func<object, string?>? formatElement)
    {
        // Npgsql reads multidimensional PostgreSQL arrays as rectangular CLR
        // arrays. Their enumerator yields scalar elements, not nested arrays:
        // retain each dimension instead of flattening them into one list.
        if (array.Rank > 1 && array.Length > 0)
        {
            AppendArrayDimension(sb, array, array.GetEnumerator(), 0, formatElement);
            return;
        }

        sb.Append('{');
        var first = true;
        foreach (var item in array)
        {
            if (sb.IsFull)
            {
                break;
            }

            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            if (!sb.IsFull)
            {
                AppendElement(sb, item, formatElement);
            }
        }

        sb.Append('}');
    }

    private static void AppendArrayDimension(
        LiteralBuilder sb,
        Array array,
        System.Collections.IEnumerator elements,
        int dimension,
        Func<object, string?>? formatElement)
    {
        sb.Append('{');
        for (var i = 0; i < array.GetLength(dimension); i++)
        {
            // As in the one-dimensional loop: past the cap nothing more would be
            // written, so nothing more is formatted. Every enclosing level stops
            // here too, so the shared enumerator left behind is never read again.
            if (sb.IsFull)
            {
                break;
            }

            if (i > 0)
            {
                sb.Append(',');
            }

            if (dimension + 1 < array.Rank)
            {
                AppendArrayDimension(sb, array, elements, dimension + 1, formatElement);
            }
            else
            {
                elements.MoveNext();
                if (!sb.IsFull)
                {
                    AppendElement(sb, elements.Current, formatElement);
                }
            }
        }

        sb.Append('}');
    }

    private static void AppendElement(LiteralBuilder sb, object? value, Func<object, string?>? formatElement)
    {
        switch (value)
        {
            case null or DBNull:
                sb.Append("NULL");
                return;
            // bytea[] arrives as byte[][], and each byte[] is one bytea value,
            // not a nested array of numbers: written as one, it read
            // {{222,173,190,239}}, which casts back as a 2-D bytea[] of the digit
            // strings.
            case Array nested when nested is not byte[]:
                AppendArray(sb, nested, formatElement);
                return;
        }

        // A bytea element is \x-hex, as a bytea cell shows it, which the quoting
        // below wraps and escapes as the server does: {"\\xDEADBEEF"}.
        var text = value is byte[] bytes
            ? "\\x" + Convert.ToHexString(bytes)
            : (value is bool ? null : formatElement?.Invoke(value)) ?? InvariantText(value);

        // Postgres quotes an element when the bare form would be ambiguous:
        // empty, the word NULL, or containing a delimiter/quote/backslash/space.
        // Scan the whole element even for a prefix: a delimiter past the cap
        // still changes its opening quote. Only escaping is bounded here.
        var needsQuoting = text.Length == 0
            || text.Equals("NULL", StringComparison.OrdinalIgnoreCase)
            || text.AsSpan().ContainsAny(QuotedElementChars);

        if (needsQuoting)
        {
            AppendQuoted(sb, text);
        }
        else
        {
            sb.Append(text);
        }
    }

    private static readonly System.Buffers.SearchValues<char> QuotedElementChars =
        System.Buffers.SearchValues.Create("{},\"\\ \t\n\r");

    /// <summary>
    /// A value's text in no culture at all: <c>t</c>/<c>f</c> for a boolean (the
    /// array element spelling), a range as its literal, anything formattable
    /// with the invariant culture. What every literal writer here falls back to,
    /// so a decimal comma or a US date never reaches text Postgres has to read.
    /// </summary>
    public static string InvariantText(object value) => value switch
    {
        bool b => b ? "t" : "f",
        // "O" would write infinity as a finite 9999-12-31T23:59:59.9999999.
        _ when TemporalInfinity(value) is { } infinity => infinity,
        // The invariant culture writes dates US-style (07/20/2026); ISO reads
        // back whatever the server's DateStyle.
        DateTime or DateTimeOffset or DateOnly or TimeOnly => ((IFormattable)value).ToString("O", CultureInfo.InvariantCulture),
        _ when FormatRange(value, InvariantText) is { } range => range,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// <c>infinity</c> or <c>-infinity</c> for the values Npgsql reads those as
    /// (the largest and smallest <see cref="DateTime"/>, <see cref="DateOnly"/> or
    /// <see cref="DateTimeOffset"/>), null for any other value. Written any other
    /// way, <see cref="DateTime.MaxValue"/> is <c>9999-12-31T23:59:59.9999999</c>,
    /// which Postgres reads as a finite timestamp. Npgsql cannot tell
    /// <c>-infinity</c> from <c>0001-01-01 00:00:00</c>: both are the smallest
    /// value, and this answers <c>-infinity</c> for both.
    /// </summary>
    public static string? TemporalInfinity(object value) => value switch
    {
        DateTime stamp when stamp == DateTime.MaxValue => "infinity",
        DateTime stamp when stamp == DateTime.MinValue => "-infinity",
        DateOnly date when date == DateOnly.MaxValue => "infinity",
        DateOnly date when date == DateOnly.MinValue => "-infinity",
        DateTimeOffset stamp when stamp == DateTimeOffset.MaxValue => "infinity",
        DateTimeOffset stamp when stamp == DateTimeOffset.MinValue => "-infinity",
        _ => null,
    };

    /// <summary>
    /// Whether a column of this wire type is a multirange. Npgsql hands a
    /// multirange over as an array of <see cref="NpgsqlTypes.NpgsqlRange{T}"/>,
    /// exactly as it hands over an array of ranges, and the two have different
    /// literals (<c>{[1,3),[5,7)}</c> against <c>{"[1,3)","[5,7)"}</c>), so only
    /// the column's type can say which one a value is.
    /// </summary>
    public static bool IsMultirangeType(string? dataTypeName) =>
        dataTypeName is not null && dataTypeName.EndsWith("multirange", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A range (what Npgsql materializes a range column as) in Postgres's own
    /// literal syntax, <c>[lower,upper)</c>, <c>(,6)</c> or <c>empty</c>, each
    /// bound written by <paramref name="formatBound"/> and quoted the way the
    /// server quotes one (<see cref="QuoteRangeBound"/>). Null when
    /// <paramref name="value"/> is not a range.
    /// <para>
    /// This exists because <c>NpgsqlRange&lt;T&gt;.ToString</c> writes each bound
    /// in the process culture: an export of a <c>tstzrange</c> read
    /// <c>[07/22/2026 19:56:13,07/25/2026 19:56:13)</c>, the fraction of a second
    /// and the zone gone, and a <c>numrange</c> under a decimal comma could not be
    /// read back at all. The subtypes are listed rather than found by reflection,
    /// which NativeAOT would not keep: they are the ones Npgsql maps a range to
    /// (the built-in ranges, and a user range over a numeric or temporal type).
    /// </para>
    /// </summary>
    public static string? FormatRange(object value, Func<object, string> formatBound) => value switch
    {
        NpgsqlTypes.NpgsqlRange<int> r => FormatRange(r, formatBound),
        NpgsqlTypes.NpgsqlRange<long> r => FormatRange(r, formatBound),
        NpgsqlTypes.NpgsqlRange<short> r => FormatRange(r, formatBound),
        NpgsqlTypes.NpgsqlRange<decimal> r => FormatRange(r, formatBound),
        NpgsqlTypes.NpgsqlRange<double> r => FormatRange(r, formatBound),
        NpgsqlTypes.NpgsqlRange<float> r => FormatRange(r, formatBound),
        NpgsqlTypes.NpgsqlRange<DateOnly> r => FormatRange(r, formatBound),
        NpgsqlTypes.NpgsqlRange<DateTime> r => FormatRange(r, formatBound),
        NpgsqlTypes.NpgsqlRange<DateTimeOffset> r => FormatRange(r, formatBound),
        NpgsqlTypes.NpgsqlRange<TimeOnly> r => FormatRange(r, formatBound),
        NpgsqlTypes.NpgsqlRange<TimeSpan> r => FormatRange(r, formatBound),
        _ => null,
    };

    /// <summary>
    /// A multirange (an array of ranges, see <see cref="IsMultirangeType"/>) as
    /// its literal, <c>{[1,3),[5,7)}</c>: the ranges unquoted, unlike the
    /// elements of a range array. Null when an element is not a range.
    /// </summary>
    /// <param name="ranges">The array of ranges.</param>
    /// <param name="formatBound">How one bound is written before it is quoted.</param>
    /// <param name="maxLength">
    /// The most characters to write, as for <see cref="FormatArray"/>: the
    /// result is the literal's first characters. It is still null whenever the
    /// whole literal would be, which may mean reading past the cap.
    /// </param>
    public static string? FormatMultirange(Array ranges, Func<object, string> formatBound, int maxLength = int.MaxValue)
    {
        var sb = new LiteralBuilder(maxLength);
        sb.Append('{');
        // One element that is not a range makes the whole value null (the
        // caller falls back to the array literal), so a prefix may stop at the
        // cap only once no later element can be anything else: in an array of
        // one range struct type, which holds no null and no other type. An
        // object[] or a Nullable<T>[] is read to the end.
        var elementType = ranges.GetType().GetElementType();
        var restAreRanges = false;
        var first = true;
        foreach (var item in ranges)
        {
            if (restAreRanges && sb.IsFull)
            {
                break;
            }

            if (item is null || FormatRange(item, formatBound) is not { } range)
            {
                return null;
            }

            restAreRanges = elementType is { IsValueType: true } && item.GetType() == elementType;
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append(range);
        }

        return sb.Append('}').ToString();
    }

    private static string FormatRange<T>(NpgsqlTypes.NpgsqlRange<T> range, Func<object, string> formatBound)
    {
        if (range.IsEmpty)
        {
            return "empty";
        }

        var sb = new StringBuilder();
        sb.Append(range.LowerBoundIsInclusive ? '[' : '(');
        if (!range.LowerBoundInfinite && range.LowerBound is { } lower)
        {
            sb.Append(QuoteRangeBound(formatBound(lower)));
        }

        sb.Append(',');
        if (!range.UpperBoundInfinite && range.UpperBound is { } upper)
        {
            sb.Append(QuoteRangeBound(formatBound(upper)));
        }

        sb.Append(range.UpperBoundIsInclusive ? ']' : ')');
        return sb.ToString();
    }

    /// <summary>
    /// One range bound as the server writes it (<c>range_bound_escape</c>):
    /// double-quoted when it is empty or holds whitespace, a quote, a backslash,
    /// a comma, a parenthesis or a bracket, with quotes and backslashes doubled.
    /// </summary>
    public static string QuoteRangeBound(string text)
    {
        var needsQuoting = text.Length == 0 || text.AsSpan().ContainsAny(QuotedRangeBoundChars);
        return needsQuoting
            ? "\"" + text.Replace("\\", "\\\\").Replace("\"", "\"\"") + "\""
            : text;
    }

    private static readonly System.Buffers.SearchValues<char> QuotedRangeBoundChars =
        System.Buffers.SearchValues.Create("\"\\()[], \t\n\r\v\f");

    /// <summary>
    /// Renders an hstore value (Npgsql materializes it as a
    /// <c>Dictionary&lt;string,string&gt;</c>, whose default ToString is the CLR
    /// type name) in Postgres's own literal syntax — <c>"k"=&gt;"v", "k2"=&gt;NULL</c>
    /// — so the grid shows a readable value everywhere, not only in browse mode's
    /// text-format path. Both key and value are always double-quoted (the form
    /// Postgres itself emits); a null value is the bare keyword <c>NULL</c>.
    /// </summary>
    /// <param name="map">The hstore value.</param>
    /// <param name="maxLength">
    /// The most characters to write, as for <see cref="FormatArray"/>: the
    /// result is the literal's first characters, and the pairs past them are
    /// not formatted.
    /// </param>
    public static string FormatHstore(System.Collections.IDictionary map, int maxLength = int.MaxValue)
    {
        var sb = new LiteralBuilder(maxLength);
        var first = true;
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            if (sb.IsFull)
            {
                break;
            }

            if (!first)
            {
                sb.Append(", ");
            }

            first = false;
            if (sb.IsFull)
            {
                break;
            }

            AppendQuoted(sb, entry.Key.ToString() ?? string.Empty);
            sb.Append("=>");
            if (sb.IsFull)
            {
                break;
            }

            if (entry.Value is null)
            {
                sb.Append("NULL");
            }
            else
            {
                AppendQuoted(sb, entry.Value.ToString() ?? string.Empty);
            }
        }

        return sb.ToString();
    }

    // A double-quoted array element or hstore key or value, a backslash before
    // each quote and backslash. Written a run at a time between the characters
    // that need one, so the scan stays vectorized: a loop per character made a
    // 4 MB element five times slower to export.
    private static void AppendQuoted(LiteralBuilder sb, string text)
    {
        sb.Append('"');
        var rest = text.AsSpan();
        while (!rest.IsEmpty && !sb.IsFull)
        {
            var escape = rest.IndexOfAny('\\', '"');
            if (escape < 0)
            {
                sb.Append(rest);
                break;
            }

            sb.Append(rest[..escape]).Append('\\').Append(rest[escape]);
            rest = rest[(escape + 1)..];
        }

        sb.Append('"');
    }

    // The same writer serves Full and its prefix: never allocate a whole
    // escaped string just to discard everything after the grid's budget.
    private sealed class LiteralBuilder
    {
        private readonly StringBuilder builder = new();
        private readonly int maxLength;

        public LiteralBuilder(int maxLength)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
            this.maxLength = maxLength;
        }

        public bool IsFull => builder.Length == maxLength;

        public LiteralBuilder Append(char value)
        {
            if (!IsFull)
            {
                builder.Append(value);
            }

            return this;
        }

        public LiteralBuilder Append(string value) => Append(value.AsSpan());

        public LiteralBuilder Append(ReadOnlySpan<char> value)
        {
            builder.Append(value[..Math.Min(value.Length, maxLength - builder.Length)]);
            return this;
        }

        public override string ToString() => builder.ToString();
    }

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
