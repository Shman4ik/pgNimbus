using PgNimbus.Core.Text;

namespace PgNimbus.Core.Query;

public static partial class SqlStatementInspector
{
    // Leading words of the statements that never produce a result set. An
    // allowlist on purpose: anything not named here (SELECT, WITH, VALUES,
    // TABLE, SHOW, EXPLAIN, FETCH, CALL, EXECUTE, COPY, a word nobody thought
    // of) is treated as one that may.
    private static readonly HashSet<string> NoRowsWords = new(StringComparer.Ordinal)
    {
        "insert", "update", "delete", "merge",
        "create", "alter", "drop", "truncate", "comment", "grant", "revoke",
        "reassign", "security", "import", "refresh", "cluster", "reindex",
        "vacuum", "analyze", "analyse", "checkpoint", "lock", "do",
        "set", "reset", "discard", "load",
        "begin", "start", "commit", "end", "rollback", "abort",
        "savepoint", "release", "prepare", "deallocate", "close",
        "listen", "unlisten", "notify",
    };

    /// <summary>
    /// True when <paramref name="statement"/> (one statement) can be told, by its
    /// words alone, never to return rows: a leading word from a fixed list of
    /// commands that produce none, and no <c>RETURNING</c> anywhere in it (read
    /// lexically, so a word inside a string, dollar body, quoted identifier or
    /// comment doesn't count). False for anything else.
    /// </summary>
    /// <remarks>
    /// This decides one thing only: whether the engine may skip the describe of a
    /// statement it would not retry anyway (a script's second statement onwards,
    /// or one inside the user's transaction), because the describe's other job
    /// there, finding the columns that must come back as text, has no columns to
    /// find. A wrong "true" costs a <c>&lt;unreadable …&gt;</c> placeholder in
    /// place of a composite's literal. It never decides whether a statement runs,
    /// or how often (see the type's remarks).
    /// </remarks>
    public static bool CannotReturnRows(string statement)
    {
        var tokens = SqlLexer.Tokenize(statement);
        var leadingSeen = false;
        foreach (var token in tokens)
        {
            if (token.IsTrivia)
            {
                continue;
            }

            if (!leadingSeen)
            {
                if (token.Kind != SqlTokenKind.Word
                    || !NoRowsWords.Contains(SqlLexer.FoldCase(statement.AsSpan(token.Start, token.Length))))
                {
                    return false;
                }

                leadingSeen = true;
                continue;
            }

            if (token.Kind == SqlTokenKind.Word
                && token.Length == "returning".Length
                && statement.AsSpan(token.Start, token.Length).Equals("returning", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return leadingSeen;
    }
}
