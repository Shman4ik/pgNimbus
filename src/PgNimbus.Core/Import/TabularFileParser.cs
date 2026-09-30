using System.Globalization;
using System.Text.Json;

namespace PgNimbus.Core.Import;

/// <summary>Parsed file contents: header names (deduplicated, never empty) and rows of nullable cell strings.</summary>
public sealed record TabularData(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows);

/// <summary>An import file past one of <see cref="TabularFileParser"/>'s caps; the message says which, for the user.</summary>
public sealed class ImportLimitException(string message) : FormatException(message);

/// <summary>
/// Parses CSV (RFC 4180-style quoting, delimiter sniffed among comma /
/// semicolon / tab) and JSON (an array of flat objects) into one tabular
/// shape. All values come out as strings — the importer lets Postgres do the
/// real typing server-side via COPY, and <see cref="TypeInferrer"/> only
/// guesses column types for the CREATE TABLE.
/// </summary>
public static class TabularFileParser
{
    // Caps on what an import reads (security audit 2026-09, finding 18). The
    // file is held in memory whole and becomes a rows × columns matrix, so a
    // file with 20,000 objects each carrying its own keys was 400M cells and an
    // out-of-memory crash. Past a cap the import stops with a message, before
    // the matrix is built.

    /// <summary>The largest file an import reads: 512 MiB.</summary>
    public const long MaxFileBytes = 512L * 1024 * 1024;

    /// <summary>The most data rows an import takes.</summary>
    public const int MaxRows = 1_000_000;

    /// <summary>The most columns an import takes (Postgres allows 1,600 per table).</summary>
    public const int MaxColumns = 1_000;

    /// <summary>
    /// The most cells (rows × columns) an import builds. Each row is padded to
    /// the full width, so a wide header over many short rows, or sparse JSON
    /// objects, can pass both caps above and still not fit in memory.
    /// </summary>
    public const long MaxCells = 50_000_000;

    /// <summary>
    /// Reads a file for import, refusing one larger than
    /// <paramref name="maxBytes"/> before any of it is held: at once when the
    /// stream knows its length, otherwise as soon as the read passes the cap.
    /// The encoding comes from a byte-order mark, UTF-8 otherwise.
    /// </summary>
    public static async Task<string> ReadTextAsync(Stream stream, long maxBytes = MaxFileBytes, CancellationToken ct = default)
    {
        if (stream.CanSeek && stream.Length - stream.Position > maxBytes)
        {
            throw TooLarge(maxBytes);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw TooLarge(maxBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer);
        return await reader.ReadToEndAsync(ct);
    }

    private static ImportLimitException TooLarge(long maxBytes) => new(
        $"The file is larger than {maxBytes / (1024 * 1024):N0} MB, the most pgNimbus imports at once. Split it into smaller files, or load it with psql's \\copy.");

    private static ImportLimitException TooManyRows() => new(
        $"The file has more than {MaxRows:N0} rows, the most pgNimbus imports at once. Split it into smaller files.");

    private static ImportLimitException TooManyColumns() => new(
        $"The file has more than {MaxColumns:N0} columns, the most pgNimbus imports into one table.");

    private static ImportLimitException TooManyCells(long cells) => new(
        $"The file would make {cells:N0} cells (rows × columns), more than the {MaxCells:N0} pgNimbus imports at once. Import fewer rows or columns at a time.");

    private static void CheckCells(int width, int rows)
    {
        var cells = (long)width * rows;
        if (cells > MaxCells)
        {
            throw TooManyCells(cells);
        }
    }

    public static TabularData ParseCsv(string text)
    {
        var delimiter = SniffDelimiter(text);
        var rows = new List<string?[]>();
        var record = new List<string?>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        var fieldWasQuoted = false;

        void EndField()
        {
            // Unquoted empty = NULL (matching COPY csv semantics); quoted empty = empty string.
            var value = field.ToString();
            record.Add(value.Length == 0 && !fieldWasQuoted ? null : value);
            field.Clear();
            fieldWasQuoted = false;
            if (record.Count > MaxColumns)
            {
                throw TooManyColumns();
            }
        }

        void EndRecord()
        {
            EndField();
            // Skip blank lines (a single null field).
            if (record.Count > 1 || record[0] is not null)
            {
                // The header is a row here too, hence the + 1.
                if (rows.Count == MaxRows + 1)
                {
                    throw TooManyRows();
                }

                rows.Add([.. record]);
            }

            record.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
                fieldWasQuoted = true;
            }
            else if (c == delimiter)
            {
                EndField();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                EndRecord();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || fieldWasQuoted || record.Count > 0)
        {
            EndRecord();
        }

        if (rows.Count == 0)
        {
            return new TabularData([], []);
        }

        var columns = MakeColumnNames(rows[0]);
        var width = columns.Count;
        CheckCells(width, rows.Count - 1);
        var data = rows.Skip(1)
            .Select(r => r.Length == width ? r : [.. r.Take(width).Concat(Enumerable.Repeat<string?>(null, Math.Max(0, width - r.Length)))])
            .ToList();
        return new TabularData(columns, data);
    }

    /// <summary>An array of flat objects; columns are the union of keys in first-seen order, nested values kept as raw JSON.</summary>
    public static TabularData ParseJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Expected a JSON array of objects.");
        }

        var columns = new List<string>();
        var index = new Dictionary<string, int>();
        var objects = new List<Dictionary<int, string?>>();

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Expected every array element to be a JSON object.");
            }

            if (objects.Count == MaxRows)
            {
                throw TooManyRows();
            }

            var values = new Dictionary<int, string?>();
            foreach (var property in element.EnumerateObject())
            {
                if (!index.TryGetValue(property.Name, out var i))
                {
                    if (columns.Count == MaxColumns)
                    {
                        throw TooManyColumns();
                    }

                    i = columns.Count;
                    index.Add(property.Name, i);
                    columns.Add(property.Name);
                }

                values[i] = property.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => property.Value.GetRawText(),
                };
            }

            objects.Add(values);
        }

        CheckCells(columns.Count, objects.Count);
        var rows = objects
            .Select(values => Enumerable.Range(0, columns.Count).Select(i => values.GetValueOrDefault(i)).ToArray())
            .ToList();
        return new TabularData(MakeColumnNames([.. columns]), rows);
    }

    private static char SniffDelimiter(string text)
    {
        var firstLineEnd = text.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd < 0 ? text : text[..firstLineEnd];
        var best = ',';
        var bestCount = -1;
        foreach (var candidate in (char[])[',', ';', '\t'])
        {
            var count = CountOutsideQuotes(firstLine, candidate);
            if (count > bestCount)
            {
                best = candidate;
                bestCount = count;
            }
        }

        return best;
    }

    private static int CountOutsideQuotes(string line, char c)
    {
        var count = 0;
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (ch == c && !quoted)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Header cells become usable column names: trimmed, never empty ("column_N"), deduplicated ("name_2").</summary>
    private static IReadOnlyList<string> MakeColumnNames(string?[] header)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            var name = (header[i] ?? "").Trim();
            if (name.Length == 0)
            {
                name = $"column_{(i + 1).ToString(CultureInfo.InvariantCulture)}";
            }

            var unique = name;
            for (var n = 2; !seen.Add(unique); n++)
            {
                unique = $"{name}_{n}";
            }

            names.Add(unique);
        }

        return names;
    }
}
