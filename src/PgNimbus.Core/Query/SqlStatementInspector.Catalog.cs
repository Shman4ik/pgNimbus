using PgNimbus.Core.Text;

namespace PgNimbus.Core.Query;

public static partial class SqlStatementInspector
{
    // Leading words of the statements that create, change or remove catalog
    // objects completion reads (relations, columns, functions, types, schemas).
    private static readonly HashSet<string> CatalogChangingWords = new(StringComparer.Ordinal)
    {
        "create", "alter", "drop", "import",
    };

    /// <summary>
    /// True when any statement of <paramref name="sql"/> can change what the
    /// completion catalog holds: CREATE / ALTER / DROP / IMPORT FOREIGN SCHEMA,
    /// or a <c>SELECT … INTO</c> (which creates a table). Read lexically, so a
    /// keyword inside a string or comment never counts. Deliberately
    /// generous: a refresh too many costs a few catalog queries, one too few
    /// leaves completion offering a table that no longer exists.
    /// </summary>
    public static bool ChangesCatalog(string sql)
    {
        foreach (var statement in SqlScriptSplitter.Split(sql))
        {
            var tokens = SqlLexer.Tokenize(statement).Where(t => !t.IsTrivia).ToList();
            if (tokens.Count == 0 || tokens[0].Kind != SqlTokenKind.Word)
            {
                continue;
            }

            var leading = SqlLexer.FoldCase(statement.AsSpan(tokens[0].Start, tokens[0].Length));
            if (CatalogChangingWords.Contains(leading))
            {
                return true;
            }

            if (leading is "select" or "with"
                && SqlScopeModel.Parse(statement).Root?.Branches.Any(b => b.Clauses.Any(c => c.Keyword == "into")) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a statement of <paramref name="sql"/> changes the session's
    /// search_path: <c>SET [SESSION | LOCAL] search_path …</c>, <c>RESET search_path</c>
    /// or <c>RESET ALL</c>, or a <c>set_config('search_path', …)</c> call.
    /// </summary>
    public static bool SetsSearchPath(string sql)
    {
        foreach (var statement in SqlScriptSplitter.Split(sql))
        {
            var words = SqlLexer.Tokenize(statement)
                .Where(t => !t.IsTrivia)
                .Select(t => t.Kind == SqlTokenKind.Word ? SqlLexer.FoldCase(statement.AsSpan(t.Start, t.Length))
                    : t.Kind == SqlTokenKind.String ? statement.Substring(t.Start, t.Length).Trim('\'').ToLowerInvariant()
                    : "")
                .ToList();
            if (words.Count == 0)
            {
                continue;
            }

            var i = 1;
            if (words[0] == "set" && i < words.Count && words[i] is "session" or "local")
            {
                i++;
            }

            if ((words[0] is "set" or "reset") && i < words.Count && words[i] is "search_path" || words[0] == "reset" && words.ElementAtOrDefault(1) == "all")
            {
                return true;
            }

            for (var j = 0; j + 2 < words.Count; j++)
            {
                if (words[j] == "set_config" && words[j + 2] == "search_path")
                {
                    return true;
                }
            }
        }

        return false;
    }
}
