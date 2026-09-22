using System.Text.RegularExpressions;

namespace PgNimbus.Core.Text;

/// <summary>Where the caret sits grammatically, as far as completion cares.</summary>
public enum SqlClause
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
/// questions the App's completion provider asks at the caret:
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
public static partial class SqlCompletionContext
{
    /// <summary>A table reference parsed out of a FROM/JOIN clause, with its alias if one was given.</summary>
    public readonly record struct TableRef(string Schema, string Table, string? Alias);

    /// <summary>
    /// What surrounds the caret: literal/comment state plus the governing clause.
    /// <see cref="InQuotedIdentifier"/> is kept apart from
    /// <see cref="InStringOrComment"/> on purpose — inside an unterminated
    /// <c>"Order I|</c> the user is typing a <i>name</i>, which is exactly where
    /// completion should help, while inside a string it must stay out of the way.
    /// </summary>
    public readonly record struct CaretContext(bool InStringOrComment, SqlClause Clause)
    {
        public bool InQuotedIdentifier { get; init; }
    }

    /// <summary>
    /// Reads the text before the caret (excluding the word being typed — that's
    /// the completion filter, not context) through the shared
    /// <see cref="SqlLexer"/>, tracking the last clause keyword of the current
    /// statement. Parentheses keep a stack of clauses, so a closed subquery
    /// hands the caret back to the clause it interrupted: after
    /// <c>WHERE id IN (SELECT … FROM t) AND |</c> the caret is in the outer
    /// predicate again, not in the subquery's FROM.
    /// </summary>
    public static CaretContext GetCaretContext(string sql, int caret)
    {
        caret = Math.Clamp(caret, 0, sql.Length);
        var tokens = SqlLexer.Tokenize(sql, 0, caret);

        if (tokens.Count > 0)
        {
            var last = tokens[^1];
            // A line comment runs to the newline, which is never inside the
            // lexed range when the caret is still on the comment's line.
            if (last.Kind == SqlTokenKind.LineComment || (last.IsProse && last.IsIncomplete))
            {
                return new CaretContext(true, ClauseBefore(sql, tokens, last.Start));
            }

            if (last.Kind == SqlTokenKind.QuotedIdentifier && last.IsIncomplete)
            {
                return new CaretContext(false, ClauseBefore(sql, tokens, last.Start)) { InQuotedIdentifier = true };
            }
        }

        return new CaretContext(false, ClauseBefore(sql, tokens, WordStart(sql, caret)));
    }

    // The clause governing position `end`, from the tokens that finish before it.
    private static SqlClause ClauseBefore(string sql, List<SqlToken> tokens, int end)
    {
        var clause = SqlClause.None;
        var stack = new Stack<SqlClause>();
        foreach (var token in tokens)
        {
            if (token.End > end)
            {
                break;
            }

            switch (token.Kind)
            {
                case SqlTokenKind.Semicolon:
                    clause = SqlClause.None;
                    stack.Clear();
                    break;
                case SqlTokenKind.OpenParen:
                    stack.Push(clause);
                    // "INSERT INTO t (" — the parenthesised list is columns, not more
                    // tables. (A "FROM (" subquery also lands here, and its own SELECT
                    // re-sets the clause the moment it's typed.)
                    if (clause is SqlClause.TableRef or SqlClause.FromTableRef)
                    {
                        clause = SqlClause.ColumnRef;
                    }

                    break;
                case SqlTokenKind.CloseParen:
                    if (stack.Count > 0)
                    {
                        clause = stack.Pop();
                    }

                    break;
                case SqlTokenKind.Word:
                    clause = ClassifyKeyword(sql.AsSpan(token.Start, token.Length), clause);
                    break;
            }
        }

        return clause;
    }

    // Start of the (possibly empty) unquoted word ending at the caret.
    private static int WordStart(string sql, int caret)
    {
        var i = caret;
        while (i > 0 && IsIdentPart(sql[i - 1]))
        {
            i--;
        }

        return i;
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

    // Blanks comments and string literals (every form the shared lexer knows)
    // to spaces, length-preserved so every offset stays valid — the regex
    // heuristics below can't be made quote/comment-aware one by one, so they run
    // over this masked text instead. Quoted identifiers are *kept*: they're names
    // the heuristics need. Returns the original string when there's nothing to
    // mask.
    private static string MaskCommentsAndStrings(string sql)
    {
        char[]? masked = null;
        foreach (var token in SqlLexer.Tokenize(sql))
        {
            if (!token.IsProse)
            {
                continue;
            }

            masked ??= sql.ToCharArray();
            Array.Fill(masked, ' ', token.Start, token.Length);
        }

        return masked is null ? sql : new string(masked);
    }

    /// <summary>
    /// True when the caret — ignoring the identifier being typed under it —
    /// sits where a new statement begins: nothing before it in the buffer but
    /// whitespace, comments, and the previous statement's <c>;</c>. That is the
    /// one position where only a leading keyword (SELECT, INSERT, WITH …) is
    /// grammatical, so completion floats those above the catalog there instead
    /// of pre-selecting whatever column happens to share the typed prefix
    /// (typing "se" at the start of an empty editor must preselect SELECT, not
    /// a <c>search</c> column three schemas away).
    /// </summary>
    public static bool IsAtStatementStart(string sql, int caret)
    {
        var end = WordStart(sql, Math.Clamp(caret, 0, sql.Length));
        var last = SqlTokenKind.Semicolon;
        foreach (var token in SqlLexer.Tokenize(sql, 0, end))
        {
            if (!token.IsTrivia)
            {
                last = token.Kind;
            }
        }

        return last == SqlTokenKind.Semicolon;
    }

    /// <summary>
    /// The span completion treats as "the statement": everything between the
    /// real <c>;</c> tokens around the caret, including text to its right — a
    /// FROM typed after the select list still names the list's sources. A
    /// caret right after a <c>;</c> belongs to the (possibly empty) statement
    /// that follows it, never to the one it closed. That is deliberately not
    /// <see cref="Query.SqlScriptSplitter.StatementSpanAt"/>'s rule, which picks
    /// the previous statement from a trailing gap so Run/Format still have
    /// something to act on.
    /// </summary>
    public static (int Start, int End) CompletionStatementSpan(string sql, int caret)
    {
        caret = Math.Clamp(caret, 0, sql.Length);
        var start = 0;
        var end = sql.Length;
        foreach (var token in SqlLexer.Tokenize(sql))
        {
            if (token.Kind != SqlTokenKind.Semicolon)
            {
                continue;
            }

            if (token.End <= caret)
            {
                start = token.End;
            }
            else
            {
                end = token.Start;
                break;
            }
        }

        return (start, end);
    }

    /// <summary>One part of a dotted name, already folded/unquoted to what Postgres would look up.</summary>
    public readonly record struct NamePart(string Name, bool Quoted);

    /// <summary>
    /// The whole qualifier chain before the member being typed: <c>[u]</c> for
    /// <c>u.na|</c>, <c>[public, users]</c> for <c>public.users.|</c>. Names are
    /// case-folded unless quoted, the way the server resolves them, so
    /// <c>"Users"</c> and <c>users</c> stay two different things. Empty when the
    /// caret isn't in a member position. An unterminated quoted member
    /// (<c>u."na|</c>) is stepped over, so its qualifier still resolves.
    /// </summary>
    public static IReadOnlyList<NamePart> GetQualifierChainBeforeCaret(string sql, int caret)
    {
        caret = Math.Clamp(caret, 0, sql.Length);
        var i = WordStart(sql, caret);
        if (GetCaretContext(sql, caret).InQuotedIdentifier)
        {
            i = SqlLexer.Tokenize(sql, 0, caret)[^1].Start;
        }

        var chain = new List<NamePart>();
        while (i > 0 && sql[i - 1] == '.')
        {
            if (ReadNameBackward(sql, i - 1) is not { } part)
            {
                break;
            }

            chain.Add(part.Part);
            i = part.Start;
        }

        chain.Reverse();
        return chain;
    }

    // The identifier ending just before exclusive `end`, and where it starts.
    private static (NamePart Part, int Start)? ReadNameBackward(string sql, int end)
    {
        if (end <= 0)
        {
            return null;
        }

        if (sql[end - 1] == '"')
        {
            // Walk back to the opening quote, stepping over "" escapes.
            var j = end - 2;
            while (j >= 0)
            {
                if (sql[j] == '"')
                {
                    if (j > 0 && sql[j - 1] == '"')
                    {
                        j -= 2;
                        continue;
                    }

                    break;
                }

                j--;
            }

            return j < 0
                ? null
                : (new NamePart(sql.Substring(j + 1, end - 1 - (j + 1)).Replace("\"\"", "\""), true), j);
        }

        var start = end;
        while (start > 0 && IsIdentPart(sql[start - 1]))
        {
            start--;
        }

        return start == end || char.IsAsciiDigit(sql[start])
            ? null
            : (new NamePart(SqlLexer.FoldCase(sql.AsSpan(start, end - start)), false), start);
    }

    /// <summary>
    /// If the caret sits in the member position of a <c>qualifier.partial</c>
    /// expression (e.g. the caret in <c>u.na|</c> or right after <c>u.|</c>),
    /// returns the qualifier — the alias/table/schema immediately before the dot,
    /// unquoted. Returns null for a bare identifier with no dot to its left.
    /// </summary>
    public static string? GetQualifierBeforeCaret(string sql, int caret) =>
        GetQualifierChainBeforeCaret(sql, caret) is { Count: > 0 } chain ? chain[^1].Name : null;

    /// <summary>
    /// True when the word immediately before the caret's (possibly-in-progress)
    /// word is the <c>ON</c> keyword — used to offer the FK join condition as
    /// soon as a JOIN's <c>ON</c> is typed, distinct from WHERE/HAVING/BY/USING
    /// which share <see cref="SqlClause.Predicate"/> but don't get that treatment.
    /// </summary>
    public static bool IsAfterOnKeyword(string sql, int caret) => IsAfterKeyword(sql, caret, "on");

    /// <summary>
    /// True when the word right before the caret's (possibly in-progress) word
    /// is <paramref name="keyword"/> — e.g. <c>CALL</c>, after which only a
    /// procedure can follow.
    /// </summary>
    public static bool IsAfterKeyword(string sql, int caret, string keyword)
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

        return end > i && sql.AsSpan(i, end - i).Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The columns a <c>JOIN … USING (…)</c> in the statement merges into one
    /// output column each, and whether it has a <c>NATURAL JOIN</c> (which
    /// merges every shared name). Such a name is legal unqualified even though
    /// two sources carry it, so completion must not treat it as ambiguous.
    /// </summary>
    public static IReadOnlySet<string> ExtractUsingColumns(string sql, out bool hasNaturalJoin)
    {
        var masked = MaskCommentsAndStrings(sql);
        hasNaturalJoin = NaturalJoinRegex().IsMatch(masked);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in UsingListRegex().Matches(masked))
        {
            foreach (var column in match.Groups["cols"].Value.Split(','))
            {
                if (Unquote(column) is { Length: > 0 } name)
                {
                    columns.Add(name);
                }
            }
        }

        return columns;
    }

    [GeneratedRegex(@"\busing\s*\((?<cols>[^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsingListRegex();

    [GeneratedRegex(@"\bnatural\s+(?:\w+\s+)*?join\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NaturalJoinRegex();

    /// <summary>
    /// True when the caret sits right after a JOIN's table reference (and its
    /// alias, if any) plus at least one trailing space — i.e. the table/alias is
    /// done and the only sensible next tokens are <c>ON</c>/<c>USING</c>, not
    /// another catalog dump. Requires the trailing space specifically so a table
    /// name or alias still being typed (no space yet, and possibly a prefix of
    /// "on" itself, e.g. an alias like <c>o</c>) doesn't trip this early.
    /// </summary>
    public static bool IsAfterCompleteJoinTarget(string sql, int caret)
    {
        caret = Math.Clamp(caret, 0, sql.Length);
        if (IsJoinTargetCompleteAt(sql, caret, requireAlias: false))
        {
            return true;
        }

        // A word already under way after "JOIN customers c ": the alias is
        // written, so that word can only be the condition's keyword ("o" of
        // ON). Without an alias it could as well be the alias being typed, and
        // stays unclaimed (found live 2026-09-22: "c o" + Tab wrote a table).
        var wordStart = WordStart(sql, caret);
        return wordStart < caret && IsJoinTargetCompleteAt(sql, wordStart, requireAlias: true);
    }

    private static bool IsJoinTargetCompleteAt(string sql, int end, bool requireAlias)
    {
        // Masked so a "join" inside a comment or string literal before the
        // caret can't pose as the JOIN whose target we're checking.
        var before = MaskCommentsAndStrings(sql[..end]);
        var joins = JoinKeywordRegex().Matches(before);
        if (joins.Count == 0)
        {
            return false;
        }

        var last = joins[^1];
        // CROSS JOIN takes no join condition at all and NATURAL JOIN derives its
        // own, so ON/USING would be a syntax error after either — the target
        // being complete there means another clause is next, not a condition.
        if (last.Value.StartsWith("cross", StringComparison.OrdinalIgnoreCase)
            || last.Value.StartsWith("natural", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segment = before[(last.Index + last.Length)..];
        var match = JoinTargetCompleteRegex().Match(segment);
        if (!match.Success)
        {
            return false;
        }

        // "JOIN orders AS " or "JOIN orders INNER " — the regex would swallow
        // the keyword as the alias; those mean the target is *not* complete
        // (an alias is still coming / another JOIN is being typed). Same
        // keyword-pseudo-capture filter AddTableRef applies.
        var (schema, table) = SplitQualified(match.Groups["table"].Value);
        if (schema.Length == 0 && ReservedAfterTable.Contains(table))
        {
            return false;
        }

        if (!match.Groups["alias"].Success)
        {
            return !requireAlias;
        }

        return !ReservedAfterTable.Contains(Unquote(match.Groups["alias"].Value));
    }

    /// <summary>
    /// True when the word at the caret (possibly empty) follows a finished FROM
    /// item: a table name, an optional alias, then whitespace, as in
    /// <c>FROM users u wh|</c>. No other relation can go there, so the words
    /// worth offering are the clause keywords that can come next
    /// (<c>WHERE</c>, <c>JOIN</c>, <c>ORDER</c> …). Unlike
    /// <see cref="IsAfterCompleteJoinTarget"/> this reads the text up to the
    /// start of the word being typed, not up to the caret, since the popup
    /// usually opens on that word's first letter. A subquery or function item
    /// is not recognised; those keep the ordinary table-position list.
    /// </summary>
    public static bool IsAfterCompleteFromItem(string sql, int caret)
    {
        var end = WordStart(sql, Math.Clamp(caret, 0, sql.Length));
        var before = MaskCommentsAndStrings(sql[..end]);
        var froms = FromKeywordRegex().Matches(before);
        if (froms.Count == 0)
        {
            return false;
        }

        var last = froms[^1];
        var segment = JoinSplitRegex().Split(before[(last.Index + last.Length)..])[^1];
        var match = JoinTargetCompleteRegex().Match(segment);
        if (!match.Success)
        {
            return false;
        }

        // "FROM users AS " / "FROM users WHERE " — a keyword read as the table or
        // alias means the item isn't the last thing written.
        var (schema, table) = SplitQualified(match.Groups["table"].Value);
        if (schema.Length == 0 && ReservedAfterTable.Contains(table))
        {
            return false;
        }

        return !match.Groups["alias"].Success || !ReservedAfterTable.Contains(Unquote(match.Groups["alias"].Value));
    }

    [GeneratedRegex(@"\bfrom\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromKeywordRegex();

    /// <summary>
    /// Extracts the table references the statement operates on: every FROM clause
    /// (scoped to the FROM…(WHERE/GROUP/…) span so commas in a SELECT list aren't
    /// mistaken for table separators) plus <c>UPDATE</c> / <c>INSERT INTO</c> /
    /// <c>SELECT … INTO</c> targets, so their columns complete in SET lists and
    /// column lists too.
    /// </summary>
    public static IReadOnlyList<TableRef> ExtractTables(string sql)
    {
        // Masked first: a FROM/JOIN/keyword inside a comment or string literal
        // must not spawn phantom tables or cut a real FROM body short.
        sql = MaskCommentsAndStrings(sql);
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
        foreach (Match match in CteNameRegex().Matches(MaskCommentsAndStrings(sql)))
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

    // The name a written identifier denotes: a quoted one unquoted and
    // unescaped, a bare one case-folded — the same thing the server looks up,
    // so "Users" and Users (= users) stay distinct.
    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"'
            ? s[1..^1].Replace("\"\"", "\"")
            : SqlLexer.FoldCase(s);
    }

    private static bool IsIdentPart(char c) => SqlLexer.IsIdentPart(c);

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
    // clause keyword or statement end. Runs over MaskCommentsAndStrings output,
    // so comments and string literals are already blanked; quoted identifiers
    // survive the mask and are consumed atomically here so a keyword *inside*
    // one ("Order Items") can't cut the body short. [\s\S] so the body spans
    // newlines.
    [GeneratedRegex(
        """\bfrom\b(?<body>(?:"[^"]*"|[\s\S])*?)(?=\b(?:where|group|having|order|limit|offset|union|intersect|except|returning|window|for|into|values|set)\b|;|$)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromClauseRegex();

    // Splits a FROM body into individual table-ref segments on commas and any
    // flavour of JOIN (INNER/LEFT/RIGHT/FULL/CROSS/NATURAL/OUTER).
    [GeneratedRegex(
        @",|\b(?:cross\s+|natural\s+|inner\s+|left\s+|right\s+|full\s+|outer\s+)*join\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JoinSplitRegex();

    // A bare JOIN keyword (with its optional flavour prefix), used to find where
    // the current JOIN's table reference starts — unlike JoinSplitRegex this
    // doesn't also match commas, since IsAfterCompleteJoinTarget only cares
    // about the most recent JOIN.
    [GeneratedRegex(
        @"\b(?:cross\s+|natural\s+|inner\s+|left\s+|right\s+|full\s+|outer\s+)*join\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JoinKeywordRegex();

    // A complete table ref (optionally schema-qualified, optionally aliased)
    // spanning the whole segment after JOIN, with mandatory trailing whitespace —
    // that trailing space is what proves the alias/table isn't still mid-typing.
    [GeneratedRegex(
        """^\s*(?<table>(?:"[^"]+"|[\w$]+)(?:\s*\.\s*(?:"[^"]+"|[\w$]+))?)(?:\s+(?:as\s+)?(?<alias>"[^"]+"|[\w$]+))?\s+$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JoinTargetCompleteRegex();

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
    // that closes the previous CTE — with an optional declared column list
    // (captured: it's the CTE's output shape, see ExtractCteDefinitions), then
    // AS (. The match ends at the body's opening paren.
    [GeneratedRegex(
        """(?:\bwith\s+(?:recursive\s+)?|\)\s*,\s*)(?<name>"[^"]+"|[\w$]+)\s*(?<cols>\([^)]*\))?\s+as\s*(?:not\s+)?(?:materialized\s+)?\(""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CteNameRegex();
}
