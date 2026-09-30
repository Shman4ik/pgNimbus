using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PgNimbus.Core.Json;

/// <summary>
/// Re-renders JSON text, indented or on one line, for people to read and edit.
///
/// <para>It writes the tokens itself rather than going through
/// <c>Utf8JsonWriter</c>, because every encoder that writer accepts escapes more
/// than JSON requires. The one the cell inspector used showed
/// <c>"Ann &lt;ann@example.com&gt;"</c> as <c>"Ann <ann@example.com>"</c>,
/// and <c>'</c>, <c>&amp;</c>, <c>+</c> the same way; even
/// <c>UnsafeRelaxedJsonEscaping</c> writes an emoji as two <c>\uXXXX</c>
/// surrogates. Here a string escapes only what JSON cannot hold raw: the quote,
/// the backslash and the control characters. Everything else is written as the
/// character it is, and a number keeps its exact source text.</para>
///
/// <para>Pure <c>System.Text.Json</c> reading, no reflection, so it is safe under
/// NativeAOT (see the constraints in CLAUDE.md).</para>
/// </summary>
public static class JsonText
{
    /// <summary>
    /// How deep a document may nest before it is treated as not JSON. Postgres
    /// accepts deeper jsonb than <c>JsonDocument</c>'s default of 64, and a value
    /// that stopped parsing there fell back to plain text. The same bound as
    /// <c>ExplainService.MaxJsonDepth</c>; <see cref="JsonTree"/> reads to it too.
    /// </summary>
    public const int MaxDepth = 256;

    /// <summary>The indent unit for <see cref="TryFormat"/>'s indented form.</summary>
    public const string Indent = "  ";

    internal static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = MaxDepth };

    internal static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = MaxDepth };

    /// <summary>
    /// Re-renders <paramref name="text"/>: indented with <see cref="Indent"/> and
    /// <c>\n</c> line breaks, or on one line with no spaces. False (and the text
    /// unchanged) when it isn't a single well-formed JSON value.
    /// </summary>
    public static bool TryFormat(string? text, bool indented, out string result)
    {
        result = text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            result = Format(Encoding.UTF8.GetBytes(text), indented);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // A string holding a lone surrogate escape ("\uD800") reads as a token
            // but can't become a .NET string. Postgres's jsonb refuses it too.
            return false;
        }
    }

    /// <summary>The element re-rendered the way <see cref="TryFormat"/> would render it.</summary>
    public static string Format(JsonElement element, bool indented) =>
        Format(Encoding.UTF8.GetBytes(element.GetRawText()), indented);

    /// <summary>
    /// A string written as a JSON string literal, quotes included, escaping only
    /// what JSON requires.
    /// </summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        AppendQuoted(sb, value);
        return sb.ToString();
    }

    private static string Format(ReadOnlySpan<byte> utf8, bool indented)
    {
        var reader = new Utf8JsonReader(utf8, ReaderOptions);
        var sb = new StringBuilder(utf8.Length + (indented ? utf8.Length / 4 : 0));

        // Whether the container being written has had an element yet (for the
        // comma and for the empty "{}"/"[]" form), and whether the next value
        // is a property's, which sits on the key's line.
        var hasElement = new Stack<bool>();
        var afterPropertyName = false;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    BeginValue();
                    sb.Append(reader.TokenType == JsonTokenType.StartObject ? '{' : '[');
                    hasElement.Push(false);
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (hasElement.Pop() && indented)
                    {
                        NewLine(hasElement.Count);
                    }

                    sb.Append(reader.TokenType == JsonTokenType.EndObject ? '}' : ']');
                    break;

                case JsonTokenType.PropertyName:
                    BeginElement();
                    AppendQuoted(sb, reader.GetString()!);
                    sb.Append(indented ? ": " : ":");
                    afterPropertyName = true;
                    break;

                case JsonTokenType.String:
                    BeginValue();
                    AppendQuoted(sb, reader.GetString()!);
                    break;

                case JsonTokenType.Number:
                    // The source text, byte for byte: 1.10 stays 1.10 and a
                    // 30-digit integer isn't rounded through a double.
                    BeginValue();
                    sb.Append(Encoding.UTF8.GetString(reader.ValueSpan));
                    break;

                case JsonTokenType.True:
                    BeginValue();
                    sb.Append("true");
                    break;

                case JsonTokenType.False:
                    BeginValue();
                    sb.Append("false");
                    break;

                case JsonTokenType.Null:
                    BeginValue();
                    sb.Append("null");
                    break;
            }
        }

        return sb.ToString();

        void BeginValue()
        {
            if (afterPropertyName)
            {
                afterPropertyName = false;
                return;
            }

            BeginElement();
        }

        void BeginElement()
        {
            if (hasElement.Count == 0)
            {
                return;
            }

            if (hasElement.Peek())
            {
                sb.Append(',');
            }
            else
            {
                hasElement.Pop();
                hasElement.Push(true);
            }

            if (indented)
            {
                NewLine(hasElement.Count);
            }
        }

        void NewLine(int depth)
        {
            sb.Append('\n');
            for (var i = 0; i < depth; i++)
            {
                sb.Append(Indent);
            }
        }
    }

    private static void AppendQuoted(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
