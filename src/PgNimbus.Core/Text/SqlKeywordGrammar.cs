namespace PgNimbus.Core.Text;

/// <summary>What kind of thing may be written at the caret, as far as keywords are concerned.</summary>
public enum SqlKeywordPosition
{
    /// <summary>Nothing the grammar reads here: completion keeps its general list.</summary>
    Unknown,
    /// <summary>A new statement: only a command can start it.</summary>
    StatementStart,
    /// <summary>The start of an expression: a column, a function, a literal, or a keyword that begins one (NOT, CASE, EXISTS …).</summary>
    Operand,
    /// <summary>Right after a finished expression: an operator or the clause that comes next — never a column.</summary>
    AfterOperand,
    /// <summary>A place where only the listed keywords fit (after IS, ORDER, INSERT, UNION …).</summary>
    KeywordsOnly,
}

/// <summary>
/// The keywords that are legal at the caret, most likely first, and whether
/// anything else (a column, a function) is too. Completion ranks these above
/// the rest and drops every keyword that is not on the list
/// (docs/dev/design/sql-completion-audit-2.md §6.2 step 1, findings B02, B04, C01).
/// </summary>
/// <param name="Position">What the caret position takes.</param>
/// <param name="Keywords">The legal keywords in rank order; empty when <see cref="Position"/> is <see cref="SqlKeywordPosition.Unknown"/>.</param>
public sealed record SqlKeywordAdvice(SqlKeywordPosition Position, IReadOnlyList<string> Keywords)
{
    public static readonly SqlKeywordAdvice None = new(SqlKeywordPosition.Unknown, []);

    /// <summary>Right after <c>INSERT INTO t</c> (or its column list): where the rows come next, and a column list can too.</summary>
    public bool AfterInsertTarget { get; init; }

    /// <summary>Only keywords fit here: no column, function or relation.</summary>
    public bool KeywordsOnly => Position is SqlKeywordPosition.StatementStart or SqlKeywordPosition.AfterOperand or SqlKeywordPosition.KeywordsOnly;
}

/// <summary>
/// A reading of the tokens right before the caret that answers one question:
/// which keywords can come next? It is deliberately local — the previous
/// token, and the clause keyword governing the caret at its paren depth — so
/// it holds up on half-typed text, and says <see cref="SqlKeywordPosition.Unknown"/>
/// rather than guess where it can't tell (DDL, table positions: those are
/// read elsewhere).
/// A keyword that is never written alone is offered with the words that
/// always follow it, as one row (<c>ORDER BY</c>, <c>IS NOT NULL</c>,
/// <c>LEFT JOIN</c>, <c>DO UPDATE SET</c> — finding C02): accepting it writes
/// all of them, and its initials find it (<c>ob</c>, <c>inn</c>, <c>lj</c>)
/// because the ranker reads each word as a part of the name.
/// </summary>
public static class SqlKeywordGrammar
{
    /// <summary>The commands a statement can start with, most frequent first.</summary>
    public static readonly IReadOnlyList<string> StatementStarts =
    [
        "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP", "EXPLAIN",
        "TRUNCATE", "BEGIN", "COMMIT", "ROLLBACK", "SET", "SHOW", "ANALYZE", "VACUUM", "REFRESH",
        "COMMENT", "GRANT", "REVOKE", "COPY", "CALL", "DO", "MERGE", "VALUES", "TABLE", "LISTEN",
        "NOTIFY", "UNLISTEN", "LOCK", "REINDEX", "CLUSTER", "DISCARD", "PREPARE", "EXECUTE",
        "DEALLOCATE", "SAVEPOINT", "RELEASE", "START", "END", "ABORT", "CHECKPOINT", "IMPORT",
        "REASSIGN", "RESET", "LOAD", "DECLARE", "FETCH", "MOVE", "CLOSE", "SECURITY",
    ];

    // The two commands whose next word is fixed, offered whole too — first
    // among the equally good prefixes, so "ins" takes INSERT INTO.
    private static readonly IReadOnlyList<string> StatementStartsWithPhrases =
        [.. StatementStarts.SelectMany<string, string>(s => s switch
        {
            "INSERT" => ["INSERT INTO", s],
            "DELETE" => ["DELETE FROM", s],
            _ => [s],
        })];

    /// <summary>Keywords that begin an expression.</summary>
    public static readonly IReadOnlyList<string> OperandStarts =
    [
        "NOT", "NULL", "TRUE", "FALSE", "CASE WHEN", "CASE", "EXISTS", "INTERVAL", "CAST", "ARRAY", "ROW",
        "CURRENT_DATE", "CURRENT_TIMESTAMP", "CURRENT_TIME", "LOCALTIMESTAMP", "LOCALTIME",
        "CURRENT_USER", "SESSION_USER", "CURRENT_ROLE", "CURRENT_SCHEMA", "CURRENT_CATALOG",
    ];

    // "IS" and the two tests it is most often the start of, so "inn" finds
    // IS NOT NULL in one row.
    private static readonly string[] IsTests = ["IS", "IS NOT NULL", "IS NULL"];

    // What can continue a finished expression inside a predicate.
    private static readonly string[] PredicateOperators =
        ["AND", "OR", .. IsTests, "IN", "NOT", "LIKE", "ILIKE", "BETWEEN", "SIMILAR TO", "ISNULL", "NOTNULL", "COLLATE", "AT TIME ZONE"];

    // Operators that make sense after a value that isn't a predicate yet
    // (a select-list item, an ORDER BY key, an assigned value).
    private static readonly string[] ValueOperators = [.. IsTests, "IN", "NOT", "LIKE", "ILIKE", "BETWEEN", "COLLATE", "AT TIME ZONE", "AND", "OR"];

    private static readonly string[] SetOperations = ["UNION", "UNION ALL", "EXCEPT", "INTERSECT"];

    // A SELECT's row-locking clause.
    private static readonly string[] Locking = ["FOR UPDATE", "FOR SHARE", "FOR NO KEY UPDATE", "FOR KEY SHARE"];

    private static readonly string[] Joins =
        ["JOIN", "LEFT JOIN", "INNER JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "NATURAL JOIN", "LEFT OUTER JOIN"];

    // The words that can follow a finished expression, per governing clause.
    private static readonly Dictionary<string, string[]> AfterOperandByClause = new(StringComparer.Ordinal)
    {
        ["select"] = ["FROM", "AS", .. ValueOperators, "INTO", .. SetOperations, "ORDER BY", "LIMIT", "WHERE", "GROUP BY"],
        ["distinct"] = ["FROM", "AS", .. ValueOperators, "INTO", .. SetOperations, "ORDER BY", "LIMIT"],
        ["where"] = ["AND", "OR", "ORDER BY", "GROUP BY", "LIMIT", .. IsTests, "IN", "NOT", "LIKE", "ILIKE", "BETWEEN",
            "RETURNING", "OFFSET", "HAVING", "WINDOW", .. SetOperations, .. Locking, "FETCH", "SIMILAR TO", "ISNULL", "NOTNULL", "COLLATE", "AT TIME ZONE"],
        ["on"] = ["AND", "OR", .. Joins, "WHERE", "GROUP BY", "ORDER BY",
            "LIMIT", .. IsTests, "IN", "NOT", "LIKE", "ILIKE", "BETWEEN", .. SetOperations, "OFFSET", "WINDOW", .. Locking, "SIMILAR TO", "ISNULL", "NOTNULL"],
        ["having"] = ["AND", "OR", "ORDER BY", "LIMIT", .. IsTests, "IN", "NOT", "LIKE", "ILIKE", "BETWEEN", "OFFSET", "WINDOW", .. SetOperations, .. Locking, "FETCH"],
        ["group"] = ["HAVING", "ORDER BY", "LIMIT", "OFFSET", "WINDOW", .. SetOperations, .. Locking, "FETCH"],
        ["order"] = ["DESC", "ASC", "LIMIT", "NULLS FIRST", "NULLS LAST", "OFFSET", "FETCH", .. Locking, "USING", .. SetOperations],
        ["orderdir"] = ["LIMIT", "NULLS FIRST", "NULLS LAST", "OFFSET", "FETCH", .. Locking, .. SetOperations],
        ["nulls"] = ["FIRST", "LAST"],
        ["limit"] = ["OFFSET", .. Locking, "FETCH", .. SetOperations],
        ["offset"] = ["LIMIT", "ROWS", "ROW", "FETCH", .. Locking, .. SetOperations],
        ["set"] = ["WHERE", "FROM", "RETURNING", .. ValueOperators],
        ["returning"] = ["AS", .. ValueOperators],
        ["values"] = ["RETURNING", "ON CONFLICT", .. SetOperations, "ORDER BY", "LIMIT"],
        ["partition"] = ["ORDER BY", "ROWS", "RANGE", "GROUPS"],
        ["over"] = ["ROWS", "RANGE", "GROUPS"],
        ["when"] = ["THEN", .. PredicateOperators],
        ["conflict"] = ["DO NOTHING", "DO UPDATE SET", "WHERE"],
        ["merge"] = ["WHEN MATCHED", "WHEN NOT MATCHED", "AND", "OR", .. IsTests, "IN", "NOT", "LIKE", "BETWEEN"],
        ["then"] = ["WHEN", "ELSE", "END", .. ValueOperators],
        ["else"] = ["END", .. ValueOperators],
        ["case"] = ["WHEN", .. ValueOperators],
        ["using"] = ["WHERE", "RETURNING"],
        ["between"] = ["AND"],
        ["paren"] = [.. PredicateOperators],
        ["call"] = [.. ValueOperators, "ORDER", "FROM", "FOR", "SIMILAR", "ISNULL", "NOTNULL"],
        ["cast"] = ["AS"],
    };

    // Keywords after which exactly these keywords fit.
    private static readonly Dictionary<string, string[]> KeywordsAfter = new(StringComparer.Ordinal)
    {
        ["order"] = ["BY"],
        ["group"] = ["BY"],
        ["partition"] = ["BY"],
        ["insert"] = ["INTO"],
        ["delete"] = ["FROM"],
        ["merge"] = ["INTO"],
        ["is"] = ["NULL", "NOT NULL", "TRUE", "FALSE", "DISTINCT FROM", "NOT", "NOT DISTINCT FROM", "UNKNOWN", "JSON", "NORMALIZED"],
        ["nulls"] = ["FIRST", "LAST"],
        ["left"] = ["JOIN", "OUTER JOIN"],
        ["right"] = ["JOIN", "OUTER JOIN"],
        ["full"] = ["JOIN", "OUTER JOIN"],
        ["inner"] = ["JOIN"],
        ["cross"] = ["JOIN"],
        ["outer"] = ["JOIN"],
        ["natural"] = ["JOIN", "LEFT JOIN", "INNER JOIN", "RIGHT JOIN", "FULL JOIN"],
        ["union"] = ["SELECT", "ALL", "DISTINCT", "VALUES", "TABLE"],
        ["except"] = ["SELECT", "ALL", "DISTINCT", "VALUES", "TABLE"],
        ["intersect"] = ["SELECT", "ALL", "DISTINCT", "VALUES", "TABLE"],
        ["similar"] = ["TO"],
        ["do"] = ["NOTHING", "UPDATE SET"],
        ["conflict"] = ["DO NOTHING", "DO UPDATE SET", "ON CONSTRAINT"],
        ["matched"] = ["THEN", "AND"],
        ["within"] = ["GROUP"],
        ["explain"] = ["ANALYZE", "VERBOSE", "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "MERGE", "VALUES", "TABLE", "CREATE", "EXECUTE", "DECLARE"],
    };

    // Keywords after "IS NOT".
    private static readonly string[] AfterIsNot = ["NULL", "TRUE", "FALSE", "DISTINCT FROM", "UNKNOWN", "JSON", "NORMALIZED"];

    // Keywords after an expression's NOT (a NOT IN / NOT LIKE …).
    private static readonly string[] AfterInfixNot = ["IN", "LIKE", "ILIKE", "BETWEEN", "SIMILAR", "NULL"];

    // Words that end an operand when they are the previous token.
    private static readonly HashSet<string> ValueWords = new(StringComparer.Ordinal)
    {
        "null", "true", "false", "unknown", "current_date", "current_timestamp", "current_time", "localtimestamp",
        "localtime", "current_user", "session_user", "current_role", "current_schema", "current_catalog", "user", "end",
    };

    // Keywords after which an expression starts.
    private static readonly HashSet<string> OperandIntroducers = new(StringComparer.Ordinal)
    {
        "select", "where", "and", "or", "not", "on", "having", "when", "then", "else", "by", "returning",
        "distinct", "between", "like", "ilike", "case", "limit", "offset", "return", "all", "any", "some",
        "array", "exists", "to", "escape", "filter", "within", "over", "default",
    };

    // Clause keywords, for finding what governs a position: the last one of
    // these at the caret's depth.
    private static readonly HashSet<string> GoverningWords = new(StringComparer.Ordinal)
    {
        "select", "distinct", "where", "on", "having", "group", "order", "limit", "offset", "set", "returning",
        "values", "partition", "over", "when", "then", "else", "case", "using", "between", "from", "join", "into",
        "update", "table", "window", "fetch", "nulls", "and", "merge",
    };

    // Words a relation name can't be read as: the rest of the grammar would
    // have claimed them.
    private static readonly HashSet<string> TableWords = new(StringComparer.Ordinal)
    {
        "from", "join", "into", "update", "table", "truncate", "only", "lateral",
    };

    /// <summary>
    /// The advice for the word at <paramref name="caret"/> in
    /// <paramref name="statement"/> (the text of one statement). The word
    /// being typed is not read: it is what the list filters on.
    /// <paramref name="callKind"/> tells what a called name is, as
    /// <c>pg_proc.prokind</c> would: <c>w</c> a window function (only OVER can
    /// follow its call), <c>a</c> an aggregate (FILTER and OVER can), <c>f</c>
    /// a plain function (neither), <c>'\0'</c> unknown (both are offered, last).
    /// Without it every call is unknown.
    /// </summary>
    public static SqlKeywordAdvice At(string statement, int caret, Func<string, char>? callKind = null)
    {
        caret = Math.Clamp(caret, 0, statement.Length);
        var wordStart = caret;
        while (wordStart > 0 && SqlLexer.IsIdentPart(statement[wordStart - 1]))
        {
            wordStart--;
        }

        var tokens = new List<SqlToken>();
        foreach (var token in SqlLexer.Tokenize(statement, 0, wordStart))
        {
            if (token.IsProse && token.IsIncomplete)
            {
                return SqlKeywordAdvice.None; // inside a string or comment
            }

            if (!token.IsTrivia)
            {
                tokens.Add(token);
            }
        }

        if (tokens.Count == 0)
        {
            return new SqlKeywordAdvice(SqlKeywordPosition.StatementStart, StatementStartsWithPhrases);
        }

        var last = tokens[^1];
        var lastWord = WordOf(statement, last);
        if (last.Kind is SqlTokenKind.Dot or SqlTokenKind.DoubleColon or SqlTokenKind.QuotedIdentifier && last.IsIncomplete)
        {
            return SqlKeywordAdvice.None;
        }

        // DDL and utility statements are read by their own grammar (package N).
        if (WordOf(statement, tokens[0]) is { } first && first is not ("select" or "with" or "insert" or "update" or "delete"
            or "values" or "table" or "explain" or "merge"))
        {
            return SqlKeywordAdvice.None;
        }

        if (lastWord is not null)
        {
            // "… DO UPDATE |", "WHEN MATCHED THEN UPDATE |": the SET of the action.
            if (lastWord == "update" && tokens.Count >= 2 && WordOf(statement, tokens[^2]) is "do" or "then")
            {
                return KeywordsOnly(["SET"]);
            }

            // "INSERT … VALUES (…) ON |": the conflict clause.
            if (lastWord == "on" && WordOf(statement, tokens[0]) == "insert" && Governing(statement, tokens[..^1]) == "values")
            {
                return KeywordsOnly(["CONFLICT"]);
            }

            // "IS NOT |"
            if (lastWord == "not" && tokens.Count >= 2 && WordOf(statement, tokens[^2]) == "is")
            {
                return KeywordsOnly(AfterIsNot);
            }

            // "a NOT |": NOT IN / NOT LIKE …; a NOT that starts an operand is below.
            if (lastWord == "not" && tokens.Count >= 2 && EndsOperand(statement, tokens, tokens.Count - 2))
            {
                return KeywordsOnly(AfterInfixNot);
            }

            if (lastWord == "distinct")
            {
                var before = tokens.Count >= 2 ? WordOf(statement, tokens[^2]) : null;
                return before switch
                {
                    "is" or "not" => KeywordsOnly(["FROM"]),
                    "select" => new SqlKeywordAdvice(SqlKeywordPosition.Operand, ["ON", .. OperandStarts]),
                    "union" or "except" or "intersect" => KeywordsOnly(["SELECT", "VALUES", "TABLE"]),
                    _ => new SqlKeywordAdvice(SqlKeywordPosition.Operand, OperandStarts),
                };
            }

            if (lastWord == "all" && tokens.Count >= 2 && WordOf(statement, tokens[^2]) is "union" or "except" or "intersect")
            {
                return KeywordsOnly(["SELECT", "VALUES", "TABLE"]);
            }

            // "WHEN NOT MATCHED THEN DO |"
            if (lastWord == "do" && WordOf(statement, tokens[0]) == "merge")
            {
                return KeywordsOnly(["NOTHING"]);
            }

            if (lastWord == "do" && !HasWord(statement, tokens, "conflict"))
            {
                return SqlKeywordAdvice.None;
            }

            // "MERGE … WHEN |", "WHEN NOT |"
            if (WordOf(statement, tokens[0]) == "merge" && DepthOf(tokens) == 0 && !InCase(statement, tokens))
            {
                if (lastWord == "when")
                {
                    return KeywordsOnly(["MATCHED", "NOT MATCHED"]);
                }

                if (lastWord == "not" && tokens.Count >= 2 && WordOf(statement, tokens[^2]) == "when")
                {
                    return KeywordsOnly(["MATCHED"]);
                }
            }

            // "MERGE … WHEN [NOT] MATCHED [AND …] THEN |": what to do with the row.
            if (lastWord == "then" && WordOf(statement, tokens[0]) == "merge" && MergeWhen(statement, tokens) is { } notMatched)
            {
                return KeywordsOnly(notMatched ? ["INSERT", "DO NOTHING"] : ["UPDATE SET", "DELETE", "DO NOTHING"]);
            }

            if (lastWord == "matched" && tokens.Count >= 2 && WordOf(statement, tokens[^2]) is "when" or "not")
            {
                return KeywordsOnly(["THEN", "AND"]);
            }

            // "CASE |": a simple CASE's operand, or straight to WHEN.
            if (lastWord == "case")
            {
                return new SqlKeywordAdvice(SqlKeywordPosition.Operand, ["WHEN", .. OperandStarts]);
            }

            if (KeywordsAfter.TryGetValue(lastWord, out var only))
            {
                return KeywordsOnly(only);
            }

            if (TableWords.Contains(lastWord) || lastWord is "as" or "with" or "recursive")
            {
                return SqlKeywordAdvice.None; // a relation or a new name: read elsewhere
            }

            if (OperandIntroducers.Contains(lastWord))
            {
                return Operand(statement, tokens, lastWord);
            }
        }

        // "WITH a AS (…) |": the main statement (or a comma and the next CTE).
        if (last.Kind == SqlTokenKind.CloseParen && SqlCompletionContext.IsAfterCteBody(statement, tokens))
        {
            return KeywordsOnly(["SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "VALUES", "TABLE"]);
        }

        switch (last.Kind)
        {
            // Right after an operator: NOT and EXISTS bind looser than any
            // operator, so "a > NOT b" is a syntax error, not an expression.
            case SqlTokenKind.Operator when !IsStar(statement, tokens):
                return Operand(statement, tokens, null) is { Position: SqlKeywordPosition.Operand } afterOperator
                    ? afterOperator with { Keywords = [.. afterOperator.Keywords.Where(k => k is not ("NOT" or "EXISTS"))] }
                    : SqlKeywordAdvice.None;
            case SqlTokenKind.Comma:
                return Operand(statement, tokens, null);
            case SqlTokenKind.OpenParen:
                return AfterOpenParen(statement, tokens);
        }

        if (EndsOperand(statement, tokens, tokens.Count - 1))
        {
            return AfterOperand(statement, tokens, callKind);
        }

        return SqlKeywordAdvice.None;
    }

    /// <summary>
    /// True when the caret is in a JOIN's ON condition: the clause keyword
    /// governing it (at its paren depth, past AND/OR) is ON.
    /// </summary>
    public static bool IsInJoinCondition(string statement, int caret)
    {
        caret = Math.Clamp(caret, 0, statement.Length);
        var tokens = new List<SqlToken>();
        foreach (var token in SqlLexer.Tokenize(statement, 0, caret))
        {
            if (!token.IsTrivia)
            {
                tokens.Add(token);
            }
        }

        var governing = Governing(statement, tokens);
        return (governing == "and" ? GoverningBeyondAnd(statement, tokens) : governing) == "on";
    }

    private static SqlKeywordAdvice KeywordsOnly(IReadOnlyList<string> keywords) =>
        new(SqlKeywordPosition.KeywordsOnly, keywords);

    // The start of an expression. After SELECT, DISTINCT and ALL fit too; in
    // a clause that lists relations (FROM a, |) the grammar has no opinion.
    private static SqlKeywordAdvice Operand(string statement, List<SqlToken> tokens, string? introducer)
    {
        var governing = Governing(statement, tokens);
        if (governing is "from" or "join" or "into" or "update" or "table" or "using" && introducer is null)
        {
            return SqlKeywordAdvice.None; // "FROM a, |": another relation
        }

        if (introducer == "select")
        {
            // The star first: "SELECT *" is the most written select list there is (C03).
            return new SqlKeywordAdvice(SqlKeywordPosition.Operand, ["*", "DISTINCT", .. OperandStarts, "ALL"]);
        }

        if (governing is "set" or "values")
        {
            return new SqlKeywordAdvice(SqlKeywordPosition.Operand, [.. OperandStarts, "DEFAULT"]);
        }

        return new SqlKeywordAdvice(SqlKeywordPosition.Operand, OperandStarts);
    }

    // "(": a call's arguments, a subquery, a list, a CTE body.
    private static SqlKeywordAdvice AfterOpenParen(string statement, List<SqlToken> tokens)
    {
        var before = tokens.Count >= 2 ? tokens[^2] : default;
        var word = tokens.Count >= 2 ? WordOf(statement, before) : null;
        if (tokens.Count >= 2 && before.Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier
            && word is null or not ("in" or "exists" or "any" or "all" or "some" or "from" or "join" or "as" or "lateral"
                or "and" or "or" or "not" or "where" or "on" or "select" or "values" or "over" or "filter" or "within" or "using"
                or "into" or "table" or "conflict" or "with" or "when" or "then" or "else" or "case" or "between" or "like" or "ilike"))
        {
            // A function call: its arguments — for count, most often the star (C03).
            IReadOnlyList<string> arguments = ["DISTINCT", .. OperandStarts, "ALL"];
            return new SqlKeywordAdvice(SqlKeywordPosition.Operand, word == "count" ? ["*", .. arguments] : arguments);
        }

        if (word is "over")
        {
            return KeywordsOnly(["PARTITION BY", "ORDER BY", "ROWS", "RANGE", "GROUPS"]);
        }

        if (word is "filter")
        {
            return KeywordsOnly(["WHERE"]);
        }

        if (word is "group" && tokens.Count >= 3 && WordOf(statement, tokens[^3]) == "within")
        {
            return KeywordsOnly(["ORDER BY"]);
        }

        if (word is "as")
        {
            // A CTE's body.
            return KeywordsOnly(["SELECT", "VALUES", "WITH", "INSERT", "UPDATE", "DELETE", "TABLE"]);
        }

        if (word is "exists" or "from" or "join" or "lateral")
        {
            return new SqlKeywordAdvice(SqlKeywordPosition.KeywordsOnly, ["SELECT", "VALUES", "WITH", "TABLE"]);
        }

        if (word is "into" or "table" or "conflict" or "using" || before.Kind == SqlTokenKind.QuotedIdentifier)
        {
            return SqlKeywordAdvice.None; // a column list
        }

        // A subquery or a list: IN (…), = (…), ((…)).
        return new SqlKeywordAdvice(SqlKeywordPosition.Operand, ["SELECT", "VALUES", "WITH", .. OperandStarts]);
    }

    // Right after a finished expression.
    private static SqlKeywordAdvice AfterOperand(string statement, List<SqlToken> tokens, Func<string, char>? callKind)
    {
        var governing = Governing(statement, tokens);
        var lastWord = WordOf(statement, tokens[^1]);

        // "ORDER BY a DESC |" / "… NULLS LAST |"
        if (governing == "order" && lastWord is "asc" or "desc")
        {
            return new SqlKeywordAdvice(SqlKeywordPosition.AfterOperand, AfterOperandByClause["orderdir"]);
        }

        if (governing == "nulls")
        {
            return new SqlKeywordAdvice(SqlKeywordPosition.AfterOperand, [.. AfterOperandByClause["orderdir"].Where(k => !k.StartsWith("NULLS", StringComparison.Ordinal))]);
        }

        if (governing == "and")
        {
            governing = GoverningBeyondAnd(statement, tokens);
        }

        // "INSERT INTO t |", "INSERT INTO t (a, b) |": the rows come next.
        if (governing == "into" && WordOf(statement, tokens[0]) == "insert" && DepthOf(tokens) == 0)
        {
            return KeywordsOnly(["VALUES", "SELECT", "DEFAULT VALUES", "OVERRIDING", "WITH", "AS"]) with { AfterInsertTarget = true };
        }

        // "MERGE INTO t [alias] |": the source; "… USING s [alias] |": its join condition.
        if (WordOf(statement, tokens[0]) == "merge" && DepthOf(tokens) == 0 && governing is "into" or "using")
        {
            return KeywordsOnly(governing == "into" ? ["USING", "AS"] : ["ON", "AS"]);
        }

        // "UPDATE t |": SET (or the alias, a new name).
        if (governing == "update" && WordOf(statement, tokens[0]) == "update" && DepthOf(tokens) == 0)
        {
            return KeywordsOnly(["SET", "AS"]);
        }

        // "JOIN b USING (id) |" reads like a finished ON; DELETE … USING is its own clause.
        if (governing == "using" && WordOf(statement, tokens[0]) != "delete")
        {
            governing = "on";
        }

        if (governing == "between")
        {
            return new SqlKeywordAdvice(SqlKeywordPosition.AfterOperand, AfterOperandByClause["between"]);
        }

        // "UPDATE t SET a = 1 |", "INSERT … VALUES (…) |"
        if (governing is null or "from" or "join" or "into" or "update" or "table" or "window" or "fetch")
        {
            return SqlKeywordAdvice.None;
        }

        if (tokens[^1].Kind == SqlTokenKind.CloseParen && governing == "values" && DepthOf(tokens) == 0)
        {
            return new SqlKeywordAdvice(SqlKeywordPosition.AfterOperand, AfterOperandByClause["values"]);
        }

        // "row_number() |": a window function's call takes OVER and nothing
        // else; an aggregate's may take FILTER or OVER; a plain function's neither.
        var kind = CalledName(statement, tokens) is { } called ? callKind?.Invoke(called) ?? '\0' : 'f';
        if (kind == 'w')
        {
            return KeywordsOnly(["OVER"]);
        }

        // MERGE … ON <condition> |
        if (governing == "on" && WordOf(statement, tokens[0]) == "merge")
        {
            governing = "merge";
        }

        if (!AfterOperandByClause.TryGetValue(governing, out var keywords))
        {
            return SqlKeywordAdvice.None;
        }

        // "… WHEN MATCHED THEN UPDATE SET a = 1 |": the next WHEN.
        if (WordOf(statement, tokens[0]) == "merge" && governing is "set" or "values" && DepthOf(tokens) == 0)
        {
            keywords = ["WHEN MATCHED", "WHEN NOT MATCHED", .. keywords.Where(k => k is not ("WHERE" or "FROM" or "RETURNING" or "ON CONFLICT"))];
        }

        if (kind is 'a' or '\0')
        {
            keywords = [.. keywords, "FILTER", "OVER"];
        }

        // Inside parentheses with no clause of their own: an expression group.
        if (DepthOf(tokens) > 0 && governing is "where" or "on" or "having" && ParenIsGroup(statement, tokens))
        {
            keywords = AfterOperandByClause["paren"];
        }

        // A DML statement's WHERE ends in RETURNING, a SELECT's never does.
        if (governing == "where" && !IsDml(statement, tokens))
        {
            keywords = [.. keywords.Where(k => k != "RETURNING")];
        }

        return new SqlKeywordAdvice(SqlKeywordPosition.AfterOperand, keywords);
    }

    // True when token `index` finishes an operand: a name, a literal, a
    // closing bracket, a value keyword.
    private static bool EndsOperand(string statement, List<SqlToken> tokens, int index)
    {
        var token = tokens[index];
        switch (token.Kind)
        {
            case SqlTokenKind.Number or SqlTokenKind.String or SqlTokenKind.DollarString or SqlTokenKind.Parameter
                or SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket:
                return true;
            case SqlTokenKind.QuotedIdentifier:
                return !token.IsIncomplete;
            case SqlTokenKind.Operator:
                return IsStar(statement, tokens[..(index + 1)]);
            case SqlTokenKind.Word:
                var word = WordOf(statement, token)!;
                if (ValueWords.Contains(word) || word is "asc" or "desc" or "first" or "last")
                {
                    return true;
                }

                return !IsKeyword(word);
            default:
                return false;
        }
    }

    // A "*" that is a star (SELECT *, t.*, count(*)) rather than a product.
    private static bool IsStar(string statement, List<SqlToken> tokens)
    {
        var last = tokens[^1];
        if (last.Kind != SqlTokenKind.Operator || statement[last.Start] != '*')
        {
            return false;
        }

        if (tokens.Count < 2)
        {
            return true;
        }

        var before = tokens[^2];
        return before.Kind is SqlTokenKind.Dot or SqlTokenKind.Comma or SqlTokenKind.OpenParen
            || WordOf(statement, before) is "select" or "distinct" or "all" or "returning";
    }

    // The words the grammar treats as keywords rather than names.
    private static bool IsKeyword(string word) =>
        OperandIntroducers.Contains(word) || GoverningWords.Contains(word) || KeywordsAfter.ContainsKey(word)
        || word is "is" or "in" or "as" or "join" or "left" or "right" or "full" or "inner" or "cross" or "natural"
            or "outer" or "lateral" or "only" or "with" or "recursive" or "insert" or "delete" or "union" or "except"
            or "intersect" or "not" or "null" or "like" or "ilike" or "similar" or "escape" or "collate" or "at"
            or "zone" or "interval" or "cast" or "exists" or "rows" or "range" or "groups" or "preceding"
            or "following" or "unbounded" or "current" or "row" or "for" or "of" or "nowait" or "skip" or "locked"
            or "conflict" or "do" or "nothing" or "constraint" or "window" or "fetch" or "next" or "ties";

    private static string? WordOf(string statement, SqlToken token) =>
        token.Kind == SqlTokenKind.Word ? SqlLexer.FoldCase(statement.AsSpan(token.Start, token.Length)) : null;

    private static bool HasWord(string statement, List<SqlToken> tokens, string word) =>
        tokens.Any(t => WordOf(statement, t) == word);

    // True when the tokens end inside an unfinished CASE (more CASEs than ENDs).
    private static bool InCase(string statement, List<SqlToken> tokens)
    {
        var open = 0;
        foreach (var token in tokens)
        {
            switch (WordOf(statement, token))
            {
                case "case":
                    open++;
                    break;
                case "end" when open > 0:
                    open--;
                    break;
            }
        }

        return open > 0;
    }

    // The last WHEN of a MERGE: true for WHEN NOT MATCHED, false for WHEN
    // MATCHED, null when the last WHEN is something else (a CASE's).
    private static bool? MergeWhen(string statement, List<SqlToken> tokens)
    {
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (WordOf(statement, tokens[i]) != "when")
            {
                continue;
            }

            var next = i + 1 < tokens.Count ? WordOf(statement, tokens[i + 1]) : null;
            var after = i + 2 < tokens.Count ? WordOf(statement, tokens[i + 2]) : null;
            return next == "matched" ? false : next == "not" && after == "matched" ? true : null;
        }

        return null;
    }

    // The name whose call the last token (a ")") closes: "row_number" for
    // "row_number()", "count" for "pg_catalog.count(*)". Null when the
    // parentheses are no call — a subquery, a list, IN (…), VALUES (…).
    private static string? CalledName(string statement, List<SqlToken> tokens)
    {
        if (tokens[^1].Kind != SqlTokenKind.CloseParen)
        {
            return null;
        }

        var depth = 0;
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (tokens[i].Kind == SqlTokenKind.CloseParen)
            {
                depth++;
            }
            else if (tokens[i].Kind == SqlTokenKind.OpenParen && --depth == 0)
            {
                if (i == 0)
                {
                    return null;
                }

                var name = tokens[i - 1];
                return name.Kind switch
                {
                    SqlTokenKind.QuotedIdentifier when !name.IsIncomplete => SqlLexer.IdentifierName(statement, name),
                    SqlTokenKind.Word when WordOf(statement, name) is { } word && !IsKeyword(word) => word,
                    _ => null,
                };
            }
        }

        return null;
    }

    // The paren depth after the last token.
    private static int DepthOf(List<SqlToken> tokens)
    {
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == SqlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == SqlTokenKind.CloseParen && depth > 0)
            {
                depth--;
            }
        }

        return depth;
    }

    // The clause keyword governing the caret: the last governing word at the
    // caret's own paren depth, not inside a group closed since. ON CONFLICT
    // reads as "conflict"; a SELECT's DISTINCT as the select list.
    private static string? Governing(string statement, List<SqlToken> tokens) => Governing(statement, tokens, tokens.Count);

    // Reads tokens[..end] backwards. An expression group's "(" hands over to the
    // clause around it, which is read by looping from before the "(" — never by
    // recursing, since "SELECT (((((" nests as deep as the paste is long and the
    // grammar runs per keystroke (a hundred thousand of them was a stack overflow).
    private static string? Governing(string statement, List<SqlToken> tokens, int end)
    {
        var inGroup = false;
        while (true)
        {
            var (word, outer) = GoverningBefore(statement, tokens, end);
            if (outer < 0)
            {
                return word ?? (inGroup ? "paren" : null);
            }

            inGroup = true;
            end = outer;
        }
    }

    // The governing word of tokens[..end], or the index of an enclosing "(" whose
    // surroundings decide instead (Outer ≥ 0, Word null).
    private static (string? Word, int Outer) GoverningBefore(string statement, List<SqlToken> tokens, int end)
    {
        var depth = 0;
        var cases = 0;
        for (var i = end - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (token.Kind == SqlTokenKind.CloseParen)
            {
                depth++;
                continue;
            }

            if (token.Kind == SqlTokenKind.OpenParen)
            {
                if (depth == 0)
                {
                    return ParenGoverning(statement, tokens, i);
                }

                // "row_number() OVER (…) |": OVER (FILTER, WITHIN GROUP) governs
                // only the inside of its parentheses; out here the select list goes on.
                if (--depth == 0 && i > 0 && WordOf(statement, tokens[i - 1]) is "over" or "filter" or "group"
                    && (WordOf(statement, tokens[i - 1]) != "group" || (i > 1 && WordOf(statement, tokens[i - 2]) == "within")))
                {
                    i -= WordOf(statement, tokens[i - 1]) == "group" ? 2 : 1;
                }

                continue;
            }

            if (depth > 0 || WordOf(statement, token) is not { } word)
            {
                continue;
            }

            // A finished CASE … END is an operand; its WHEN/THEN/ELSE govern nothing out here.
            if (word == "end")
            {
                cases++;
                continue;
            }

            if (cases > 0)
            {
                if (word == "case")
                {
                    cases--;
                }

                continue;
            }

            if (word == "conflict")
            {
                return ("conflict", -1);
            }

            if (word == "distinct" && i > 0 && WordOf(statement, tokens[i - 1]) == "select")
            {
                return ("select", -1);
            }

            if (word == "set" && i > 0 && WordOf(statement, tokens[i - 1]) == "update")
            {
                return ("set", -1); // DO UPDATE SET
            }

            if (GoverningWords.Contains(word))
            {
                return (word, -1);
            }
        }

        return (null, -1);
    }

    // What governs the inside of the "(" at `open`, when no clause keyword
    // follows it: a function's arguments read as a select list would, an
    // expression group as the clause around it (Outer = open: read on from
    // before the paren; "paren" if nothing out there governs).
    private static (string? Word, int Outer) ParenGoverning(string statement, List<SqlToken> tokens, int open)
    {
        if (open == 0)
        {
            return (null, -1);
        }

        var before = WordOf(statement, tokens[open - 1]);
        if (before is "over")
        {
            return ("over", -1);
        }

        if (before is "values" or "in")
        {
            return (before == "values" ? "values" : "paren", -1);
        }

        if (before == "cast")
        {
            return ("cast", -1);
        }

        if (tokens[open - 1].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier && before is null or not ("and" or "or" or "not" or "where" or "on" or "having" or "when" or "exists"))
        {
            return ("call", -1); // a call's argument
        }

        return (null, open);
    }

    // An open "(" whose contents are an expression group (not a call, not a
    // subquery), so the clause words of the outer query can't follow yet.
    private static bool ParenIsGroup(string statement, List<SqlToken> tokens)
    {
        var depth = 0;
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (tokens[i].Kind == SqlTokenKind.CloseParen)
            {
                depth++;
            }
            else if (tokens[i].Kind == SqlTokenKind.OpenParen && depth-- == 0)
            {
                return true;
            }
        }

        return false;
    }

    // Past an AND: the clause the AND belongs to (WHERE, ON, HAVING …), or
    // "between" when the AND is still owed to a BETWEEN.
    private static string? GoverningBeyondAnd(string statement, List<SqlToken> tokens)
    {
        var depth = 0;
        var betweens = 0;
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (token.Kind == SqlTokenKind.CloseParen)
            {
                depth++;
                continue;
            }

            if (token.Kind == SqlTokenKind.OpenParen)
            {
                if (depth == 0)
                {
                    var (inner, outer) = ParenGoverning(statement, tokens, i);
                    return outer < 0 ? inner : Governing(statement, tokens, outer) ?? "paren";
                }

                depth--;
                continue;
            }

            if (depth > 0 || WordOf(statement, token) is not { } word || word == "and")
            {
                continue;
            }

            if (word == "between")
            {
                betweens++;
                continue;
            }

            if (GoverningWords.Contains(word))
            {
                return word;
            }
        }

        return betweens > 0 ? "where" : null;
    }

    private static bool IsDml(string statement, List<SqlToken> tokens) =>
        WordOf(statement, tokens[0]) is "update" or "delete" or "insert" or "merge"
        || (HasWord(statement, tokens, "update") && !HasWord(statement, tokens, "for"))
        || HasWord(statement, tokens, "delete");
}
