using System.Text.RegularExpressions;

namespace PgNimbus.App.Completion;

/// <summary>Where the caret sits grammatically, as far as completion cares.</summary>
internal enum SqlClause
{
    /// <summary>No clause identified — offer the full catalog.</summary>
    None,
    /// <summary>A table/view name goes here (after INTO, UPDATE, TABLE, TRUNCATE…).</summary>
    TableRef,
    /// <summary>A table/view name goes here, specifically after FROM — same list as <see cref="TableRef"/>, but a table accepted here can also take an auto-alias (INTO/TRUNCATE targets can't).</summary>
    FromTableRef,
    /// <summary>A table/view name goes here, specifically after JOIN — FK-connected tables float to the top, and an accepted table can take an auto-alias.</summary>
    JoinTableRef,
    /// <summary>A column/expression goes here (after SELECT, SET, RETURNING…) — the full catalog, current tables' columns floated up.</summary>
    ColumnRef,
    /// <summary>A row-scoped column reference (after WHERE, ON, HAVING, GROUP/ORDER BY, USING) — only the FROM-clause tables' columns can go here, not the whole schema.</summary>
    Predicate,
}

/// <summary>
/// A lightweight, regex-based read of the SQL being edited — just enough to make
/// completion context-aware without pulling in a full parser. It answers the
/// questions <see cref="SqlCompletionProvider"/> asks at the caret:
/// <list type="number">
/// <item>Is this a <c>qualifier.partial</c> member access, and if so what is the
/// qualifier (the alias/table/schema before the dot)?</item>
/// <item>Which tables — with their aliases — does the surrounding statement pull
/// <c>FROM</c>/<c>JOIN</c>, <c>UPDATE</c>, or <c>INSERT INTO</c>?</item>
/// <item>Is the caret inside a string/comment (suppress), and which clause is it
/// in (tables after FROM, columns after WHERE, …)?</item>
/// </list>
/// All are heuristics: they handle the shapes real queries actually take
/// (schema-qualified names, <c>AS</c>/implicit aliases, comma and JOIN lists)
/// and quietly give up on the exotic (correlated subqueries) rather than
/// guess wrong.
/// </summary>
internal static partial class SqlCompletionContext
{
    /// <summary>A table reference parsed out of a FROM/JOIN clause, with its alias if one was given.</summary>
    public readonly record struct TableRef(string Schema, string Table, string? Alias);

    /// <summary>What surrounds the caret: literal/comment state plus the governing clause.</summary>
    public readonly record struct CaretContext(bool InStringOrComment, SqlClause Clause);

    /// <summary>
    /// Scans the text before the caret (excluding the word being typed — that's
    /// the completion filter, not context) tracking string/comment state and the
    /// last clause keyword of the current statement. One forward pass, no regex,
    /// so it's cheap enough to run per keystroke.
    /// </summary>
    public static CaretContext GetCaretContext(string sql, int caret)
    {
        var end = Math.Clamp(caret, 0, sql.Length);
        while (end > 0 && IsIdentPart(sql[end - 1]))
        {
            end--;
        }

        var clause = SqlClause.None;
        var i = 0;
        while (i < end)
        {
            var c = sql[i];

            if (c == '-' && i + 1 < end && sql[i + 1] == '-')
            {
                var eol = sql.IndexOf('\n', i + 2, end - (i + 2));
                if (eol < 0)
                {
                    return new CaretContext(true, clause);
                }

                i = eol + 1;
                continue;
            }

            if (c == '/' && i + 1 < end && sql[i + 1] == '*')
            {
                // Postgres block comments nest.
                var depth = 1;
                i += 2;
                while (i < end && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < end && sql[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (sql[i] == '*' && i + 1 < end && sql[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }

                if (depth > 0)
                {
                    return new CaretContext(true, clause);
                }

                continue;
            }

            if (c == '\'' || c == '"')
            {
                // A '' (or "") escape reads as close+reopen — same net state.
                var close = sql.IndexOf(c, i + 1, end - (i + 1));
                if (close < 0)
                {
                    return new CaretContext(true, clause);
                }

                i = close + 1;
                continue;
            }

            if (c == '$' && TrySkipDollarQuote(sql, i, end, ref i))
            {
                if (i < 0)
                {
                    return new CaretContext(true, clause);
                }

                continue;
            }

            if (c == ';')
            {
                clause = SqlClause.None;
                i++;
                continue;
            }

            if (c == '(')
            {
                // "INSERT INTO t (" — the parenthesised list is columns, not more
                // tables. (A "FROM (" subquery also lands here, and its own SELECT
                // will re-set the clause the moment it's typed.)
                if (clause is SqlClause.TableRef or SqlClause.FromTableRef)
                {
                    clause = SqlClause.ColumnRef;
                }

                i++;
                continue;
            }

            if (IsIdentPart(c))
            {
                var start = i;
                while (i < end && IsIdentPart(sql[i]))
                {
                    i++;
                }

                if (!char.IsAsciiDigit(c))
                {
                    clause = ClassifyKeyword(sql.AsSpan(start, i - start), clause);
                }

                continue;
            }

            i++;
        }

        return new CaretContext(false, clause);
    }

    // The keywords that move the caret into table position or column position;
    // everything else leaves the clause as-is.
    private static SqlClause ClassifyKeyword(ReadOnlySpan<char> word, SqlClause current)
    {
        // JOIN and FROM split out from TableClauseKeywords: FK-aware ranking
        // only kicks in after an actual JOIN, and auto-aliasing only after
        // FROM/JOIN (an INTO/TRUNCATE target can't legally take a bare alias).
        if (word.Equals("join", StringComparison.OrdinalIgnoreCase))
        {
            return SqlClause.JoinTableRef;
        }

        if (word.Equals("from", StringComparison.OrdinalIgnoreCase))
        {
            return SqlClause.FromTableRef;
        }

        foreach (var kw in TableClauseKeywords)
        {
            if (word.Equals(kw, StringComparison.OrdinalIgnoreCase))
            {
                return SqlClause.TableRef;
            }
        }

        foreach (var kw in PredicateClauseKeywords)
        {
            if (word.Equals(kw, StringComparison.OrdinalIgnoreCase))
            {
                return SqlClause.Predicate;
            }
        }

        foreach (var kw in ColumnClauseKeywords)
        {
            if (word.Equals(kw, StringComparison.OrdinalIgnoreCase))
            {
                return SqlClause.ColumnRef;
            }
        }

        return current;
    }

    /// <summary>The keywords a table reference follows (besides FROM and JOIN, classified separately above). "update" also covers <c>ON CONFLICT DO UPDATE</c> — its SET flips back to columns.</summary>
    private static readonly string[] TableClauseKeywords =
        ["into", "update", "table", "truncate"];

    // Row-scoped column contexts: a predicate (WHERE/ON/HAVING) or a
    // GROUP/ORDER BY / USING list can only name columns of the tables already in
    // the statement's FROM, so completion narrows to those instead of the catalog.
    private static readonly string[] PredicateClauseKeywords =
        ["where", "on", "having", "by", "using"];

    private static readonly string[] ColumnClauseKeywords =
        ["select", "set", "returning", "values", "when", "then", "else", "distinct"];

    // Skips a $$…$$ / $tag$…$tag$ literal starting at `start`. Returns false when
    // the '$' isn't a dollar-quote opener (e.g. a $1 parameter); on true, `i` is
    // the index after the closing tag, or -1 when the literal is still open at `end`.
    private static bool TrySkipDollarQuote(string sql, int start, int end, ref int i)
    {
        var t = start + 1;
        while (t < end && (char.IsAsciiLetter(sql[t]) || sql[t] == '_'))
        {
            t++;
        }

        if (t >= end || sql[t] != '$')
        {
            return false;
        }

        var tag = sql.Substring(start, t - start + 1);
        var close = sql.IndexOf(tag, t + 1, end - (t + 1), StringComparison.Ordinal);
        i = close < 0 ? -1 : close + tag.Length;
        return true;
    }

    /// <summary>
    /// If the caret sits in the member position of a <c>qualifier.partial</c>
    /// expression (e.g. the caret in <c>u.na|</c> or right after <c>u.|</c>),
    /// returns the qualifier — the alias/table/schema immediately before the dot,
    /// unquoted. Returns null for a bare identifier with no dot to its left.
    /// </summary>
    public static string? GetQualifierBeforeCaret(string sql, int caret)
    {
        var i = Math.Clamp(caret, 0, sql.Length);

        // Skip back over the (possibly empty) word currently being typed.
        while (i > 0 && IsIdentPart(sql[i - 1]))
        {
            i--;
        }

        if (i == 0 || sql[i - 1] != '.')
        {
            return null;
        }

        // The identifier ending just before the dot is the qualifier.
        return ReadIdentifierBackward(sql, i - 1);
    }

    /// <summary>
    /// True when the word immediately before the caret's (possibly-in-progress)
    /// word is the <c>ON</c> keyword — used to offer the FK join condition as
    /// soon as a JOIN's <c>ON</c> is typed, distinct from WHERE/HAVING/BY/USING
    /// which share <see cref="SqlClause.Predicate"/> but don't get that treatment.
    /// </summary>
    public static bool IsAfterOnKeyword(string sql, int caret)
    {
        var i = Math.Clamp(caret, 0, sql.Length);
        while (i > 0 && IsIdentPart(sql[i - 1]))
        {
            i--;
        }

        while (i > 0 && char.IsWhiteSpace(sql[i - 1]))
        {
            i--;
        }

        var end = i;
        while (i > 0 && IsIdentPart(sql[i - 1]))
        {
            i--;
        }

        return end > i && sql.AsSpan(i, end - i).Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the table references the statement operates on: every FROM clause
    /// (scoped to the FROM…(WHERE/GROUP/…) span so commas in a SELECT list aren't
    /// mistaken for table separators) plus <c>UPDATE</c> / <c>INSERT INTO</c> /
    /// <c>SELECT … INTO</c> targets, so their columns complete in SET lists and
    /// column lists too.
    /// </summary>
    public static IReadOnlyList<TableRef> ExtractTables(string sql)
    {
        var tables = new List<TableRef>();

        foreach (Match clause in FromClauseRegex().Matches(sql))
        {
            var body = clause.Groups["body"].Value;

            // A FROM body is a comma/JOIN-separated list of table refs; the ON
            // predicate trailing a JOIN stays glued to its table but is ignored
            // because SingleTableRefRegex is anchored at the segment start.
            foreach (var segment in JoinSplitRegex().Split(body))
            {
                AddTableRef(tables, SingleTableRefRegex().Match(segment));
            }
        }

        foreach (Match match in UpdateIntoTargetRegex().Matches(sql))
        {
            AddTableRef(tables, match);
        }

        return tables;
    }

    /// <summary>
    /// The CTE names a <c>WITH</c> clause introduces — they complete like table
    /// names even though the catalog has never heard of them.
    /// </summary>
    public static IReadOnlyList<string> ExtractCteNames(string sql)
    {
        var names = new List<string>();
        foreach (Match match in CteNameRegex().Matches(sql))
        {
            names.Add(Unquote(match.Groups["name"].Value));
        }

        return names;
    }

    // Appends the table (and alias) captured by a SingleTableRefRegex-shaped
    // match, filtering out keyword false-positives on both.
    private static void AddTableRef(List<TableRef> tables, Match match)
    {
        if (!match.Success)
        {
            return;
        }

        var (schema, table) = SplitQualified(match.Groups["table"].Value);
        // "UPDATE" in ON CONFLICT DO UPDATE SET has no table after it — the regex
        // then swallows the next keyword as the "table".
        if (string.IsNullOrEmpty(table) || (schema.Length == 0 && ReservedAfterTable.Contains(table)))
        {
            return;
        }

        var alias = match.Groups["alias"].Success ? Unquote(match.Groups["alias"].Value) : null;
        // A trailing keyword (ON, WHERE, …) can look like an alias — it isn't.
        if (alias is not null && ReservedAfterTable.Contains(alias))
        {
            alias = null;
        }

        tables.Add(new TableRef(schema, table, alias));
    }

    // Reads the identifier that ends just before exclusive index `end` (walking
    // left), returning its unquoted text, or null if there's no identifier there.
    private static string? ReadIdentifierBackward(string sql, int end)
    {
        if (end <= 0)
        {
            return null;
        }

        if (sql[end - 1] == '"')
        {
            var open = end - 2;
            while (open >= 0 && sql[open] != '"')
            {
                open--;
            }

            return open < 0 ? null : sql.Substring(open + 1, end - 1 - (open + 1)).Replace("\"\"", "\"");
        }

        var start = end;
        while (start > 0 && IsIdentPart(sql[start - 1]))
        {
            start--;
        }

        return start == end ? null : sql.Substring(start, end - start);
    }

    private static (string Schema, string Table) SplitQualified(string raw)
    {
        var trimmed = raw.Trim();

        // First dot that isn't inside a quoted section separates schema from table.
        var inQuote = false;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == '.' && !inQuote)
            {
                return (Unquote(trimmed[..i]), Unquote(trimmed[(i + 1)..]));
            }
        }

        return ("", Unquote(trimmed));
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"'
            ? s[1..^1].Replace("\"\"", "\"")
            : s;
    }

    private static bool IsIdentPart(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '$';

    // Words that can legally follow a table reference but are never an alias, so
    // they must not be swallowed as one.
    private static readonly HashSet<string> ReservedAfterTable = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "using", "where", "group", "order", "having", "limit", "offset",
        "join", "inner", "left", "right", "full", "outer", "cross", "natural",
        "and", "or", "union", "intersect", "except", "returning", "window",
        "for", "as", "tablesample", "set", "values", "select",
    };

    // FROM (or a comma/JOIN list under it), captured up to the next top-level
    // clause keyword or statement end. [\s\S] so the body spans newlines.
    [GeneratedRegex(
        @"\bfrom\b(?<body>[\s\S]*?)(?=\b(?:where|group|having|order|limit|offset|union|intersect|except|returning|window|for|into|values|set)\b|;|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromClauseRegex();

    // Splits a FROM body into individual table-ref segments on commas and any
    // flavour of JOIN (INNER/LEFT/RIGHT/FULL/CROSS/NATURAL/OUTER).
    [GeneratedRegex(
        @",|\b(?:cross\s+|natural\s+|inner\s+|left\s+|right\s+|full\s+|outer\s+)*join\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JoinSplitRegex();

    // A single table ref at the start of a segment: an optionally schema-qualified,
    // optionally quoted name, then an optional (AS) alias.
    [GeneratedRegex(
        """^\s*(?<table>(?:"[^"]+"|[\w$]+)(?:\s*\.\s*(?:"[^"]+"|[\w$]+))?)(?:\s+(?:as\s+)?(?<alias>"[^"]+"|[\w$]+))?""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleTableRefRegex();

    // The write target after UPDATE / (INSERT|SELECT …) INTO — same table+alias
    // shape as SingleTableRefRegex; keyword pseudo-captures ("SET" as the alias,
    // or as the table in ON CONFLICT DO UPDATE) are filtered by AddTableRef.
    [GeneratedRegex(
        """\b(?:update|into)\s+(?<table>(?:"[^"]+"|[\w$]+)(?:\s*\.\s*(?:"[^"]+"|[\w$]+))?)(?:[ \t]+(?:as\s+)?(?<alias>"[^"]+"|[\w$]+))?""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpdateIntoTargetRegex();

    // A CTE header: the name right after WITH [RECURSIVE] — or after the ") ,"
    // that closes the previous CTE — with an optional column list, then AS (.
    [GeneratedRegex(
        """(?:\bwith\s+(?:recursive\s+)?|\)\s*,\s*)(?<name>"[^"]+"|[\w$]+)\s*(?:\([^)]*\))?\s+as\s*(?:not\s+)?(?:materialized\s+)?\(""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CteNameRegex();
}
