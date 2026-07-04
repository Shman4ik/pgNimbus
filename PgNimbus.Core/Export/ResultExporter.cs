using System.Globalization;
using System.Text.Json;

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

    public static void WriteJson(Stream stream, IReadOnlyList<string> columns, IEnumerable<object?[]> rows)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

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
