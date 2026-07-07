using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace PgNimbus.Core.Export;

/// <summary>
/// Writes query result rows as CSV or JSON. Values come back from Npgsql as
/// arbitrary CLR types (int, string, DateTime, byte[], arrays, ...), so JSON
/// output is built with Utf8JsonWriter by hand rather than via
/// JsonSerializer's reflection-based object-graph serialization - that
/// keeps this trim/NativeAOT-safe, matching the rest of Core.
/// </summary>
public static class ResultExporter
{
    public static void WriteCsv(TextWriter writer, IReadOnlyList<string> columns, IEnumerable<object?[]> rows)
    {
        writer.Write(string.Join(',', columns.Select(EscapeCsvField)));
        writer.Write("\r\n");

        foreach (var row in rows)
        {
            writer.Write(string.Join(',', row.Select(v => EscapeCsvField(FormatCsvValue(v)))));
            writer.Write("\r\n");
        }
    }

    /// <summary>
    /// Tab-separated rows with a header line — the spreadsheet-friendly shape for a plain clipboard copy.
    /// Tabs and newlines inside a value are collapsed to spaces so the row/column grid stays intact on paste.
    /// </summary>
    public static void WriteTsv(TextWriter writer, IReadOnlyList<string> columns, IEnumerable<object?[]> rows)
    {
        writer.Write(string.Join('\t', columns.Select(SanitizeTsv)));
        writer.Write('\n');

        foreach (var row in rows)
        {
            writer.Write(string.Join('\t', row.Select(v => SanitizeTsv(FormatCsvValue(v)))));
            writer.Write('\n');
        }
    }

    /// <summary>A GitHub-flavored Markdown table (header, separator row, then data), pipes and newlines escaped.</summary>
    public static void WriteMarkdown(TextWriter writer, IReadOnlyList<string> columns, IEnumerable<object?[]> rows)
    {
        writer.Write("| ");
        writer.Write(string.Join(" | ", columns.Select(EscapeMarkdown)));
        writer.Write(" |\n| ");
        writer.Write(string.Join(" | ", columns.Select(_ => "---")));
        writer.Write(" |\n");

        foreach (var row in rows)
        {
            writer.Write("| ");
            writer.Write(string.Join(" | ", row.Select(v => EscapeMarkdown(FormatCsvValue(v)))));
            writer.Write(" |\n");
        }
    }

    /// <summary>
    /// One <c>INSERT INTO table (cols) VALUES (...);</c> per row, with proper SQL literal quoting (NULL,
    /// unquoted numbers/booleans, single-quoted and '-escaped text, <c>\x…</c> bytea).
    /// </summary>
    public static void WriteInsert(TextWriter writer, string table, IReadOnlyList<string> columns, IEnumerable<object?[]> rows)
    {
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));

        foreach (var row in rows)
        {
            writer.Write("INSERT INTO ");
            writer.Write(table);
            writer.Write(" (");
            writer.Write(columnList);
            writer.Write(") VALUES (");
            writer.Write(string.Join(", ", row.Select(FormatSqlLiteral)));
            writer.Write(");\n");
        }
    }

    // Escaping is relaxed to all Unicode ranges instead of the JsonSerializer
    // default (ASCII-only, everything else \uXXXX-escaped) - non-Latin text
    // (e.g. Cyrillic) should read as itself in an exported/copied JSON file,
    // not as escape sequences.
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static void WriteJson(Stream stream, IReadOnlyList<string> columns, IEnumerable<object?[]> rows)
    {
        using var writer = new Utf8JsonWriter(stream, JsonOptions);

        writer.WriteStartArray();
        foreach (var row in rows)
        {
            writer.WriteStartObject();
            for (var i = 0; i < columns.Count; i++)
            {
                writer.WritePropertyName(columns[i]);
                WriteJsonValue(writer, row[i]);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string FormatCsvValue(object? value) => value switch
    {
        null or DBNull => string.Empty,
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        Array array => string.Join(';', array.Cast<object?>().Select(FormatCsvValue)),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string EscapeCsvField(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";

    private static string SanitizeTsv(string value) =>
        value.IndexOfAny(['\t', '\n', '\r']) < 0
            ? value
            : value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static string EscapeMarkdown(string value) =>
        value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", string.Empty).Replace("\n", "<br>");

    /// <summary>
    /// Quote a Postgres identifier only when needed: a bare lowercase identifier is left as-is, anything
    /// else is double-quoted (with <c>"</c>-doubling) so mixed-case/reserved names round-trip.
    /// </summary>
    public static string QuoteIdentifier(string name) =>
        name.Length > 0 && (char.IsLower(name[0]) || name[0] == '_') && name.All(c => char.IsLower(c) || char.IsDigit(c) || c == '_')
            ? name
            : $"\"{name.Replace("\"", "\"\"")}\"";

    private static string FormatSqlLiteral(object? value) => value switch
    {
        null or DBNull => "NULL",
        bool b => b ? "TRUE" : "FALSE",
        byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
        float or double or decimal => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        byte[] bytes => $"'\\x{Convert.ToHexString(bytes)}'",
        _ => $"'{FormatCsvValue(value).Replace("'", "''")}'",
    };

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong ul:
                writer.WriteNumberValue(ul);
                break;
            case float f:
                if (float.IsFinite(f))
                {
                    writer.WriteNumberValue(f);
                }
                else
                {
                    writer.WriteStringValue(f.ToString(CultureInfo.InvariantCulture));
                }

                break;
            case double d:
                if (double.IsFinite(d))
                {
                    writer.WriteNumberValue(d);
                }
                else
                {
                    writer.WriteStringValue(d.ToString(CultureInfo.InvariantCulture));
                }

                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture));
                break;
            case Guid g:
                writer.WriteStringValue(g);
                break;
            case byte[] bytes:
                writer.WriteStringValue(Convert.ToBase64String(bytes));
                break;
            case Array array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteJsonValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
