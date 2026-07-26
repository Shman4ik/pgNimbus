namespace PgNimbus.Core.Text;

/// <summary>
/// Comment/uncomment a block of SQL lines, VS Code style: if every non-blank
/// line is already commented the block is uncommented, otherwise every line is
/// commented at the block's common indentation so the SQL keeps its shape.
/// Pure text in, pure text out — the editor only does the document surgery.
/// </summary>
public static class LineCommenter
{
    private const string Marker = "--";

    /// <summary>
    /// The block with its comment state flipped. Blank lines are left alone
    /// when commenting (a lone "--" on an empty line is just noise) but don't
    /// stop the block from counting as fully commented.
    /// </summary>
    public static IReadOnlyList<string> Toggle(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return lines;
        }

        return AllCommented(lines) ? Uncomment(lines) : Comment(lines);
    }

    private static bool AllCommented(IReadOnlyList<string> lines)
    {
        var sawContent = false;
        foreach (var line in lines)
        {
            if (IsBlank(line))
            {
                continue;
            }

            sawContent = true;
            if (!line.AsSpan().TrimStart().StartsWith(Marker))
            {
                return false;
            }
        }

        // An all-blank selection has nothing to uncomment — comment it instead
        // so the gesture still does something visible.
        return sawContent;
    }

    private static IReadOnlyList<string> Comment(IReadOnlyList<string> lines)
    {
        // One shared column so a nested block doesn't get a ragged left edge.
        var column = lines
            .Where(l => !IsBlank(l))
            .Select(l => l.Length - l.AsSpan().TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();

        return lines
            .Select(line => IsBlank(line)
                ? line
                : string.Concat(line.AsSpan(0, column), Marker, " ", line.AsSpan(column)))
            .ToList();
    }

    private static IReadOnlyList<string> Uncomment(IReadOnlyList<string> lines) =>
        lines.Select(RemoveMarker).ToList();

    private static string RemoveMarker(string line)
    {
        var indent = line.Length - line.AsSpan().TrimStart().Length;
        if (indent >= line.Length || !line.AsSpan(indent).StartsWith(Marker))
        {
            return line;
        }

        var rest = indent + Marker.Length;

        // Drop the single space this class adds when commenting, so a
        // comment/uncomment round trip returns the original text exactly.
        if (rest < line.Length && line[rest] == ' ')
        {
            rest++;
        }

        return string.Concat(line.AsSpan(0, indent), line.AsSpan(rest));
    }

    private static bool IsBlank(string line) => line.AsSpan().IsWhiteSpace();
}
