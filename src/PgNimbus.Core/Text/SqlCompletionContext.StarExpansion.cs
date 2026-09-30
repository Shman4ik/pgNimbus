using PgNimbus.Core.Query;

namespace PgNimbus.Core.Text;

public static partial class SqlCompletionContext
{
    /// <summary>
    /// A resolved <c>SELECT *</c> expansion: replace <paramref name="Length"/>
    /// characters at <paramref name="Start"/> (the statement's select list)
    /// with <paramref name="Replacement"/> (the same list with every star
    /// spelled out as explicit columns).
    /// </summary>
    public readonly record struct StarExpansion(int Start, int Length, string Replacement);

    /// <summary>
    /// Expands the <c>*</c> / <c>alias.*</c> items in the select list of the
    /// statement under <paramref name="caret"/> into explicit column lists,
    /// resolving each FROM/JOIN table's columns through
    /// <paramref name="columnsFor"/> (schema, table → column names; null when
    /// unknown — the caller owns the catalog and can also answer for CTEs).
    /// All-or-nothing: if any table a star covers can't be resolved, returns
    /// null rather than a semantically different partial list. Columns are
    /// qualified with the table's alias (or name) when more than one table is
    /// in scope; a qualified star keeps its qualifier as typed. Only the
    /// outer select list expands — CTE bodies are out of scope here (each CTE
    /// can be expanded from inside its own statement… once subquery editing
    /// lands; today the caret's statement is the unit).
    /// </summary>
    public static StarExpansion? ExpandSelectStar(
        string sql,
        int caret,
        Func<string, string, IReadOnlyList<string>?> columnsFor) =>
        ExpandSelectStar(sql, caret, columnsFor, out _);

    /// <summary>
    /// <see cref="ExpandSelectStar(string, int, Func{string, string, IReadOnlyList{string}?})"/>,
    /// plus the one-line reason when it declines, for the status line.
    /// Declining is the safe answer whenever the spelled-out list could name a
    /// different set of columns than the star does: a <c>JOIN … USING</c> or
    /// <c>NATURAL JOIN</c> outputs each merged column once (and for an outer
    /// join, from whichever side has it), and a derived table or function in
    /// FROM has no catalog columns to read — expanding either used to change
    /// the result's shape silently.
    /// </summary>
    public static StarExpansion? ExpandSelectStar(
        string sql,
        int caret,
        Func<string, string, IReadOnlyList<string>?> columnsFor,
        out string? refusal)
    {
        refusal = null;
        if (SqlScriptSplitter.StatementSpanAt(sql, caret) is not { } stmt)
        {
            refusal = "No statement at the caret.";
            return null;
        }

        var statement = sql[stmt.Start..stmt.End];
        // Masked twice: literals/comments first (as everywhere), then CTE
        // bodies — so the select list, FROM tables, and star items found
        // below all belong to the *outer* query, not a WITH body.
        var masked = MaskCteBodies(MaskCommentsAndStrings(statement));

        if (FindSelectListSpan(masked) is not { } span)
        {
            refusal = "No SELECT list in this statement.";
            return null;
        }

        var (listStart, listEnd) = span;
        while (listStart < listEnd && char.IsWhiteSpace(masked[listStart]))
        {
            listStart++;
        }

        while (listEnd > listStart && char.IsWhiteSpace(masked[listEnd - 1]))
        {
            listEnd--;
        }

        if (listStart >= listEnd)
        {
            refusal = "No SELECT list in this statement.";
            return null;
        }

        var tables = ExtractFromTables(masked, out var fromFullyRead);
        var mergesColumns = MergedJoinRegex().IsMatch(masked);
        var items = new List<string>();
        var expandedAny = false;

        foreach (var (itemStart, itemEnd) in SplitTopLevelSpans(masked, listStart, listEnd))
        {
            var s = itemStart;
            var e = itemEnd;
            while (s < e && char.IsWhiteSpace(masked[s]))
            {
                s++;
            }

            while (e > s && char.IsWhiteSpace(masked[e - 1]))
            {
                e--;
            }

            var item = masked[s..e];
            if (item == "*")
            {
                if (mergesColumns)
                {
                    refusal = "Can't expand *: USING / NATURAL JOIN merges columns, so an explicit list would change the result.";
                    return null;
                }

                if (tables.Count == 0 || !fromFullyRead)
                {
                    refusal = "Can't expand *: FROM has a subquery or function whose columns aren't known.";
                    return null;
                }

                if (!TryExpandBareStar(tables, columnsFor, items, out var unknown))
                {
                    refusal = $"Can't expand *: the columns of {unknown} aren't known.";
                    return null;
                }

                expandedAny = true;
                continue;
            }

            if (item.EndsWith(".*", StringComparison.Ordinal) && IsPureChain(item.AsSpan(0, item.Length - 2)))
            {
                if (!TryExpandQualifiedStar(item[..^2], tables, columnsFor, items))
                {
                    refusal = $"Can't expand {item}: its columns aren't known.";
                    return null;
                }

                expandedAny = true;
                continue;
            }

            // Any other item survives verbatim — from the *original* text:
            // the masked copy has string literals blanked out.
            items.Add(sql.Substring(stmt.Start + s, e - s));
        }

        if (!expandedAny)
        {
            refusal = "No * in the select list.";
            return null;
        }

        return new StarExpansion(stmt.Start + listStart, listEnd - listStart, string.Join(", ", items));
    }

    // "*" covers every FROM/JOIN table, in FROM order; columns are qualified
    // by alias-or-name when the join makes bare names ambiguous.
    private static bool TryExpandBareStar(
        IReadOnlyList<TableRef> tables,
        Func<string, string, IReadOnlyList<string>?> columnsFor,
        List<string> items,
        out string? unknown)
    {
        unknown = null;
        var qualify = tables.Count > 1;
        foreach (var table in tables)
        {
            if (columnsFor(table.Schema, table.Table) is not { Count: > 0 } columns)
            {
                unknown = table.Schema.Length == 0 ? table.Table : $"{table.Schema}.{table.Table}";
                return false;
            }

            var qualifier = qualify ? SqlIdentifier.QuoteIfNeeded(table.Alias ?? table.Table) : null;
            AppendColumns(items, qualifier, columns);
        }

        return true;
    }

    // "q.*" covers the one table q names — a FROM alias, a bare table name,
    // or (when the table has no alias) a schema-qualified "schema.table" —
    // and keeps q as the qualifier so the expansion resolves exactly like
    // the star did.
    private static bool TryExpandQualifiedStar(
        string qualifierAsTyped,
        IReadOnlyList<TableRef> tables,
        Func<string, string, IReadOnlyList<string>?> columnsFor,
        List<string> items)
    {
        var (qSchema, qTable) = SplitQualified(qualifierAsTyped);
        foreach (var table in tables)
        {
            var matches = table.Alias is not null
                ? qSchema.Length == 0 && string.Equals(table.Alias, qTable, StringComparison.Ordinal)
                : string.Equals(table.Table, qTable, StringComparison.Ordinal)
                    && (qSchema.Length == 0 || string.Equals(table.Schema, qSchema, StringComparison.Ordinal));
            if (!matches)
            {
                continue;
            }

            if (columnsFor(table.Schema, table.Table) is not { Count: > 0 } columns)
            {
                return false;
            }

            AppendColumns(items, qualifierAsTyped.Trim(), columns);
            return true;
        }

        return false;
    }

    private static void AppendColumns(List<string> items, string? qualifier, IReadOnlyList<string> columns)
    {
        foreach (var column in columns)
        {
            var quoted = SqlIdentifier.QuoteIfNeeded(column);
            items.Add(qualifier is null ? quoted : $"{qualifier}.{quoted}");
        }
    }

    // True when the span is a bare identifier chain ("o", "public.orders",
    // "\"Order Items\"") — the only thing that can legally qualify a star.
    private static bool IsPureChain(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!IsIdentPart(c) && c != '.' && c != '"')
            {
                return false;
            }
        }

        return true;
    }

    // The FROM/JOIN tables only — ExtractTables minus the UPDATE / INSERT
    // INTO targets, because "INSERT INTO t SELECT * FROM s" expands to s's
    // columns, never t's. Expects already-masked input. `fullyRead` is false
    // when some FROM item isn't a plain (schema.)table ref — a derived table,
    // a function call, LATERAL — whose columns a bare "*" would also cover.
    private static List<TableRef> ExtractFromTables(string maskedSql, out bool fullyRead)
    {
        fullyRead = true;
        var tables = new List<TableRef>();
        foreach (System.Text.RegularExpressions.Match clause in FromClauseRegex().Matches(maskedSql))
        {
            foreach (var segment in JoinSplitRegex().Split(clause.Groups["body"].Value))
            {
                var match = SingleTableRefRegex().Match(segment);
                var count = tables.Count;
                AddTableRef(tables, match);
                if (tables.Count == count
                    || segment.AsSpan(match.Groups["table"].Index + match.Groups["table"].Length).TrimStart().StartsWith("(")
                    || tables[^1].Table.Equals("lateral", StringComparison.Ordinal))
                {
                    fullyRead = false;
                }
            }
        }

        return tables;
    }

    // A join that merges same-named columns into one output column.
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(?:using\s*\(|natural\s)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MergedJoinRegex();

    // Blanks the interior of every CTE body (the parens survive, keeping
    // depth intact) so the outer statement's SELECT/FROM are the only ones
    // at paren depth 0. Length-preserving, like MaskCommentsAndStrings.
    private static string MaskCteBodies(string masked)
    {
        char[]? buffer = null;
        foreach (System.Text.RegularExpressions.Match match in CteNameRegex().Matches(masked))
        {
            var bodyStart = match.Index + match.Length;
            var bodyEnd = FindBalancedClose(masked, bodyStart);
            if (bodyEnd <= bodyStart)
            {
                continue;
            }

            buffer ??= masked.ToCharArray();
            for (var i = bodyStart; i < bodyEnd; i++)
            {
                buffer[i] = ' ';
            }
        }

        return buffer is null ? masked : new string(buffer);
    }

    // The [start, end) spans of the top-level comma-separated items inside
    // s[from..to] — SplitTopLevel with offsets instead of substrings.
    private static List<(int Start, int End)> SplitTopLevelSpans(string s, int from, int to)
    {
        var spans = new List<(int, int)>();
        var depth = 0;
        var start = from;
        var i = from;
        while (i < to)
        {
            var c = s[i];
            if (c == '"')
            {
                var close = s.IndexOf('"', i + 1);
                i = close < 0 || close >= to ? to : close + 1;
                continue;
            }

            if (c == '(' || c == '[')
            {
                depth++;
            }
            else if (c == ')' || c == ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                spans.Add((start, i));
                start = i + 1;
            }

            i++;
        }

        spans.Add((start, to));
        return spans;
    }
}
