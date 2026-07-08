using System.Globalization;
using System.Text.Json;

namespace PgNimbus.Core.Import;

/// <summary>Parsed file contents: header names (deduplicated, never empty) and rows of nullable cell strings.</summary>
public sealed record TabularData(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows);

/// <summary>
/// Parses CSV (RFC 4180-style quoting, delimiter sniffed among comma /
/// semicolon / tab) and JSON (an array of flat objects) into one tabular
/// shape. All values come out as strings — the importer lets Postgres do the
/// real typing server-side via COPY, and <see cref="TypeInferrer"/> only
/// guesses column types for the CREATE TABLE.
/// </summary>
public static class TabularFileParser
{
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
        }

        void EndRecord()
        {
            EndField();
            // Skip blank lines (a single null field).
            if (record.Count > 1 || record[0] is not null)
            {
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

            var values = new Dictionary<int, string?>();
            foreach (var property in element.EnumerateObject())
            {
                if (!index.TryGetValue(property.Name, out var i))
                {
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
