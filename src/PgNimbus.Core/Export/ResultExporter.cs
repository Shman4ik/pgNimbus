using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Export;

/// <summary>The file formats <see cref="ResultExporter.WriteStreamingAsync"/> writes.</summary>
public enum ExportFormat
{
    Csv,
    Json,
}

/// <summary>
/// Writes query result rows as CSV or JSON. Values come back from Npgsql as
/// arbitrary CLR types (int, string, DateTime, byte[], arrays, ...), so JSON
/// output is built with Utf8JsonWriter by hand rather than via
/// JsonSerializer's reflection-based object-graph serialization - that
/// keeps this trim/NativeAOT-safe, matching the rest of Core.
/// </summary>
public static class ResultExporter
{
    /// <param name="spreadsheetSafe">
    /// Put a <c>'</c> in front of every text cell a spreadsheet would read as a
    /// formula (see <see cref="NeutralizeFormula"/>). Off by default: the quote
    /// is a change to the data for every other reader of the file.
    /// </param>
    public static void WriteCsv(TextWriter writer, IReadOnlyList<string> columns, IEnumerable<object?[]> rows, bool spreadsheetSafe = false, IReadOnlyList<string?>? columnTypes = null)
    {
        WriteCsvHeader(writer, columns, spreadsheetSafe);

        foreach (var row in rows)
        {
            WriteCsvRow(writer, row, spreadsheetSafe, columnTypes);
        }
    }

    private static void WriteCsvHeader(TextWriter writer, IReadOnlyList<string> columns, bool spreadsheetSafe)
    {
        writer.Write(string.Join(',', columns.Select(c => EscapeCsvField(spreadsheetSafe ? NeutralizeFormula(c) : c))));
        writer.Write("\r\n");
    }

    private static void WriteCsvRow(TextWriter writer, object?[] row, bool spreadsheetSafe, IReadOnlyList<string?>? columnTypes)
    {
        writer.Write(string.Join(',', row.Select((v, i) => EscapeCsvField(FormatCell(v, TypeOf(columnTypes, i), spreadsheetSafe)))));
        writer.Write("\r\n");
    }

    // The column's wire type, when the caller passed them: a multirange and an
    // array of ranges are the same CLR value and only this tells them apart.
    private static string? TypeOf(IReadOnlyList<string?>? columnTypes, int index) =>
        columnTypes is not null && index < columnTypes.Count ? columnTypes[index] : null;

    /// <summary>
    /// The characters that make Excel, LibreOffice and Google Sheets read a cell
    /// as a formula (or, for tab and carriage return, hide one behind them): the
    /// "CSV injection" list OWASP gives.
    /// </summary>
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>
    /// <paramref name="text"/> with a <c>'</c> in front when it starts with a
    /// character a spreadsheet would take as the start of a formula, so a value
    /// such as <c>=HYPERLINK(…)</c> shows as text instead of running. Anything
    /// else comes back unchanged.
    /// </summary>
    public static string NeutralizeFormula(string text) =>
        text.Length > 0 && Array.IndexOf(FormulaTriggers, text[0]) >= 0 ? "'" + text : text;

    // A cell's text for CSV/TSV. Numbers are never prefixed: a negative number
    // starts with '-', and it is a number to the spreadsheet too, not a formula.
    private static string FormatCell(object? value, string? type, bool spreadsheetSafe)
    {
        var text = FormatCsvValue(value, type);
        return spreadsheetSafe && !IsNumber(value) ? NeutralizeFormula(text) : text;
    }

    private static bool IsNumber(object? value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or System.Numerics.BigInteger;

    /// <summary>
    /// Writes a result as it arrives, for exports too big to hold in memory: the
    /// CSV header (or JSON's opening bracket) first, then each batch, flushed to
    /// <paramref name="stream"/> before the next one is read, then the closing
    /// bracket. Memory holds one batch, never the whole result — which is the
    /// point, since the grid stops at 100,000 rows and an export must not.
    /// <paramref name="progress"/> gets the running row count after each batch.
    /// Cancelling stops between batches and leaves a partial file in
    /// <paramref name="stream"/>, which the caller must throw away.
    /// </summary>
    /// <returns>The number of rows written.</returns>
    public static async Task<long> WriteStreamingAsync(
        ExportFormat format,
        Stream stream,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<RowBatch> batches,
        Action<long>? progress,
        CancellationToken ct,
        bool spreadsheetSafe = false,
        IReadOnlyList<string?>? columnTypes = null)
    {
        long written = 0;

        if (format == ExportFormat.Csv)
        {
            await using var csv = new StreamWriter(stream, leaveOpen: true);
            WriteCsvHeader(csv, columns, spreadsheetSafe);
            await foreach (var batch in batches.WithCancellation(ct))
            {
                foreach (var row in batch.Rows)
                {
                    WriteCsvRow(csv, row, spreadsheetSafe, columnTypes);
                }

                written += batch.Rows.Count;
                progress?.Invoke(written);
            }

            await csv.FlushAsync(ct);
            return written;
        }

        await using var json = new Utf8JsonWriter(stream, JsonOptions);
        json.WriteStartArray();
        await foreach (var batch in batches.WithCancellation(ct))
        {
            foreach (var row in batch.Rows)
            {
                WriteJsonRow(json, columns, row, columnTypes);
            }

            // Utf8JsonWriter buffers until it is flushed: without this the whole
            // export would sit in memory until the closing bracket.
            await json.FlushAsync(ct);
            written += batch.Rows.Count;
            progress?.Invoke(written);
        }

        json.WriteEndArray();
        await json.FlushAsync(ct);
        return written;
    }

    /// <summary>
    /// Tab-separated rows with a header line — the spreadsheet-friendly shape for a plain clipboard copy.
    /// Tabs and newlines inside a value are collapsed to spaces so the row/column grid stays intact on paste.
    /// <paramref name="spreadsheetSafe"/> is <see cref="WriteCsv"/>'s; the formula check reads the value before
    /// its tabs are collapsed, so a leading tab still counts.
    /// </summary>
    public static void WriteTsv(TextWriter writer, IReadOnlyList<string> columns, IEnumerable<object?[]> rows, bool spreadsheetSafe = false, IReadOnlyList<string?>? columnTypes = null)
    {
        writer.Write(string.Join('\t', columns.Select(c => SanitizeTsv(spreadsheetSafe ? NeutralizeFormula(c) : c))));
        writer.Write('\n');

        foreach (var row in rows)
        {
            writer.Write(string.Join('\t', row.Select((v, i) => SanitizeTsv(FormatCell(v, TypeOf(columnTypes, i), spreadsheetSafe)))));
            writer.Write('\n');
        }
    }

    /// <summary>A GitHub-flavored Markdown table (header, separator row, then data), pipes and newlines escaped.</summary>
    public static void WriteMarkdown(TextWriter writer, IReadOnlyList<string> columns, IEnumerable<object?[]> rows, IReadOnlyList<string?>? columnTypes = null)
    {
        writer.Write("| ");
        writer.Write(string.Join(" | ", columns.Select(EscapeMarkdown)));
        writer.Write(" |\n| ");
        writer.Write(string.Join(" | ", columns.Select(_ => "---")));
        writer.Write(" |\n");

        foreach (var row in rows)
        {
            writer.Write("| ");
            writer.Write(string.Join(" | ", row.Select((v, i) => EscapeMarkdown(FormatCsvValue(v, TypeOf(columnTypes, i))))));
            writer.Write(" |\n");
        }
    }

    /// <summary>
    /// One <c>INSERT INTO table (cols) VALUES (...);</c> per row, with proper SQL literal quoting (NULL,
    /// unquoted numbers/booleans, single-quoted and '-escaped text, <c>\x…</c> bytea, an array, range or
    /// multirange as the literal its column reads).
    /// </summary>
    public static void WriteInsert(TextWriter writer, string table, IReadOnlyList<string> columns, IEnumerable<object?[]> rows, IReadOnlyList<string?>? columnTypes = null)
    {
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));

        foreach (var row in rows)
        {
            writer.Write("INSERT INTO ");
            writer.Write(table);
            writer.Write(" (");
            writer.Write(columnList);
            writer.Write(") VALUES (");
            writer.Write(string.Join(", ", row.Select((v, i) => FormatSqlLiteral(v, TypeOf(columnTypes, i)))));
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

    public static void WriteJson(Stream stream, IReadOnlyList<string> columns, IEnumerable<object?[]> rows, IReadOnlyList<string?>? columnTypes = null)
    {
        using var writer = new Utf8JsonWriter(stream, JsonOptions);

        writer.WriteStartArray();
        foreach (var row in rows)
        {
            WriteJsonRow(writer, columns, row, columnTypes);
        }

        writer.WriteEndArray();
    }

    private static void WriteJsonRow(Utf8JsonWriter writer, IReadOnlyList<string> columns, object?[] row, IReadOnlyList<string?>? columnTypes)
    {
        writer.WriteStartObject();
        for (var i = 0; i < columns.Count; i++)
        {
            writer.WritePropertyName(columns[i]);
            WriteJsonValue(writer, row[i], TypeOf(columnTypes, i));
        }

        writer.WriteEndObject();
    }

    private static string FormatCsvValue(object? value, string? type = null) => value switch
    {
        null or DBNull => string.Empty,
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        // The invariant culture is US-shaped too (07/20/2026); "O" is ISO.
        DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        // A multirange is one value, not an array: its literal, {[1,3),[5,7)}.
        Array array when PgValueSyntax.IsMultirangeType(type)
            && PgValueSyntax.FormatMultirange(array, FormatBound) is { } multirange => multirange,
        Array array => string.Join(';', array.Cast<object?>().Select(v => FormatCsvValue(v))),
        // hstore comes back as a Dictionary<string,string>; emit its Postgres
        // literal ("k"=>"v") rather than the CLR type name.
        System.Collections.IDictionary map => PgValueSyntax.FormatHstore(map),
        // A range's own ToString writes its bounds in the process culture
        // (07/22/2026 19:56:13, a decimal comma); here each bound is written
        // the way a scalar cell of its type is.
        _ when PgValueSyntax.FormatRange(value, FormatBound) is { } range => range,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string FormatBound(object bound) => FormatCsvValue(bound);

    // An element inside a Postgres array literal: bytea as \x-hex (the literal's
    // own form, where a CSV cell takes base64), anything else as its cell text,
    // which the literal then quotes.
    private static string FormatArrayElement(object element) =>
        element is byte[] bytes ? "\\x" + Convert.ToHexString(bytes) : FormatCsvValue(element);

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

    /// <summary>
    /// Render a CLR value as a SQL literal: NULL, unquoted numbers/booleans,
    /// single-quoted and <c>''</c>-escaped text, <c>\x…</c> bytea. Shared by the
    /// INSERT exporter and FK-follow filter composition — the latter is
    /// executed, as the browse page's WHERE, so like <see cref="SqlLiteral"/>
    /// this relies on every session forcing <c>standard_conforming_strings</c>
    /// on (<see cref="Connections.ConnectionProfile.StandardStringsSessionOption"/>):
    /// only then is the doubled quote the whole escape.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="type">
    /// The column's wire type, when known: it is what tells a multirange from an
    /// array of ranges (<see cref="PgValueSyntax.IsMultirangeType"/>).
    /// </param>
    public static string FormatSqlLiteral(object? value, string? type = null) => value switch
    {
        null or DBNull => "NULL",
        bool b => b ? "TRUE" : "FALSE",
        byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
        float or double or decimal => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        byte[] bytes => $"'\\x{Convert.ToHexString(bytes)}'",
        // An array as its CSV text ('1;2') is no array literal; write {1,2}.
        Array array when !PgValueSyntax.IsMultirangeType(type) =>
            Query.SqlLiteral.Quote(PgValueSyntax.FormatArray(array, FormatArrayElement)),
        _ => Query.SqlLiteral.Quote(FormatCsvValue(value, type)),
    };

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value, string? type = null)
    {
        // A range or a multirange has no JSON shape of its own: it is written as
        // its literal, the way the CSV writes it.
        if ((value is Array && PgValueSyntax.IsMultirangeType(type))
            || (value is not null && PgValueSyntax.FormatRange(value, FormatBound) is not null))
        {
            writer.WriteStringValue(FormatCsvValue(value, type));
            return;
        }

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
            case DateOnly or TimeOnly:
                writer.WriteStringValue(FormatCsvValue(value));
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
            // hstore (Dictionary<string,string>) is naturally a JSON object —
            // faithful and machine-readable, matching how arrays serialize
            // structurally above rather than as a literal string.
            case System.Collections.IDictionary map:
                writer.WriteStartObject();
                foreach (System.Collections.DictionaryEntry entry in map)
                {
                    writer.WritePropertyName(entry.Key.ToString() ?? string.Empty);
                    WriteJsonValue(writer, entry.Value);
                }

                writer.WriteEndObject();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
