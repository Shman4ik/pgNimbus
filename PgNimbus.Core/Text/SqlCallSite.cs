namespace PgNimbus.Core.Text;

/// <summary>
/// The function call the caret is inside: its (possibly schema-qualified)
/// name, which argument the caret is in (0-based), that argument's name when
/// it is written as a named argument (<c>arg =&gt; …</c> / <c>arg := …</c>),
/// and where the call's <c>(</c> is.
/// </summary>
public sealed record SqlCallSite(IReadOnlyList<string> Name, int ArgumentIndex, string? ArgumentName, int OpenParen)
{
    /// <summary>
    /// The innermost call around <paramref name="caret"/>, or null outside
    /// any call (or inside a string or comment). Read through the shared
    /// <see cref="SqlLexer"/>, so a comma inside a string literal, a nested
    /// call, a subquery or an <c>ARRAY[…]</c> never moves the argument index;
    /// a caret inside such a group still reports the enclosing call's argument.
    /// A parenthesis after a keyword (<c>IN (…)</c>, <c>VALUES (…)</c>,
    /// <c>EXISTS (…)</c>) is not a call.
    /// </summary>
    public static SqlCallSite? At(string sql, int caret)
    {
        caret = Math.Clamp(caret, 0, sql.Length);
        var (statementStart, _) = SqlCompletionContext.CompletionStatementSpan(sql, caret);
        var tokens = SqlLexer.Tokenize(sql, statementStart, caret);
        if (tokens.Count > 0 && tokens[^1] is { IsProse: true } last && (last.IsIncomplete || last.Kind == SqlTokenKind.LineComment))
        {
            return null;
        }

        var frames = new List<Frame>();
        SqlToken? previous = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.IsTrivia)
            {
                continue;
            }

            switch (token.Kind)
            {
                case SqlTokenKind.OpenParen:
                    frames.Add(new Frame(IsCall(sql, previous) ? NameBefore(sql, tokens, i) : null, token.Start, i + 1));
                    break;
                case SqlTokenKind.OpenBracket:
                    frames.Add(new Frame(null, token.Start, i + 1));
                    break;
                case SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket when frames.Count > 0:
                    frames.RemoveAt(frames.Count - 1);
                    break;
                case SqlTokenKind.Comma when frames.Count > 0:
                    frames[^1].Commas++;
                    frames[^1].ArgumentStart = i + 1;
                    break;
            }

            previous = token;
        }

        for (var f = frames.Count - 1; f >= 0; f--)
        {
            if (frames[f].Name is { } name)
            {
                return new SqlCallSite(name, frames[f].Commas, ArgumentNameOf(sql, tokens, frames[f].ArgumentStart), frames[f].Open);
            }
        }

        return null;
    }

    private sealed class Frame(IReadOnlyList<string>? name, int open, int argumentStart)
    {
        public IReadOnlyList<string>? Name { get; } = name;

        public int Open { get; } = open;

        public int Commas { get; set; }

        public int ArgumentStart { get; set; } = argumentStart;
    }

    // Words a "(" can follow without the pair being a function call.
    private static readonly HashSet<string> NotCallWords = new(StringComparer.Ordinal)
    {
        "in", "exists", "values", "as", "on", "using", "from", "join", "where", "and", "or", "not", "select",
        "when", "then", "else", "over", "filter", "within", "lateral", "any", "all", "some", "returning",
        "conflict", "set", "into", "table", "by", "having", "is", "with", "materialized", "distinct", "union",
        "intersect", "except", "array", "row", "case", "do", "update", "insert", "delete", "recursive",
        "partition", "group", "order", "limit", "offset", "like", "ilike", "between", "window",
    };

    // A "(" right after a name (whitespace allowed, as the server allows it).
    private static bool IsCall(string sql, SqlToken? previous) => previous is { } p && p.Kind switch
    {
        SqlTokenKind.QuotedIdentifier => true,
        SqlTokenKind.Word => !NotCallWords.Contains(SqlLexer.FoldCase(sql.AsSpan(p.Start, p.Length))),
        _ => false,
    };

    // "schema.func" / "func" — the name chain right before token `open`.
    private static IReadOnlyList<string> NameBefore(string sql, List<SqlToken> tokens, int open)
    {
        var parts = new List<string>();
        var i = PreviousSignificant(tokens, open);
        while (i >= 0 && tokens[i].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier)
        {
            parts.Add(SqlLexer.IdentifierName(sql, tokens[i]));
            var dot = PreviousSignificant(tokens, i);
            if (dot < 0 || tokens[dot].Kind != SqlTokenKind.Dot)
            {
                break;
            }

            i = PreviousSignificant(tokens, dot);
        }

        parts.Reverse();
        return parts;
    }

    private static int PreviousSignificant(List<SqlToken> tokens, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (!tokens[i].IsTrivia)
            {
                return i;
            }
        }

        return -1;
    }

    // "name => …" / "name := …" at the start of the current argument.
    private static string? ArgumentNameOf(string sql, List<SqlToken> tokens, int start)
    {
        var significant = tokens.Skip(start).Where(t => !t.IsTrivia).Take(3).ToList();
        if (significant.Count < 3 || significant[0].Kind is not (SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier))
        {
            return null;
        }

        var first = sql[significant[1].Start];
        var second = sql[significant[2].Start];
        var named = (first == '=' && second == '>') || (significant[1].Kind == SqlTokenKind.Operator && first == ':' && second == '=');
        return named && significant[1].End == significant[2].Start ? SqlLexer.IdentifierName(sql, significant[0]) : null;
    }
}

/// <summary>One parameter of a callable, as <c>pg_get_function_identity_arguments</c> spells it.</summary>
public sealed record SqlParameter(string Text, string? Name, bool IsVariadic);

/// <summary>Reads a function's argument list into its parameters.</summary>
public static class SqlParameters
{
    // First words of multi-word type names: "double precision x" never
    // happens, so a leading one of these means the parameter has no name.
    private static readonly HashSet<string> TypeLeadWords = new(StringComparer.Ordinal)
    {
        "double", "character", "char", "bit", "timestamp", "time", "interval", "national", "varchar", "numeric",
        "decimal", "int", "integer", "bigint", "smallint", "text", "boolean", "real", "float",
    };

    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "in", "out", "inout", "variadic" };

    /// <summary>
    /// Splits an argument list (<c>x integer, VARIADIC arr text[]</c>) on its
    /// top-level commas — not the ones inside a type's parentheses or a
    /// quoted name — and reads each parameter's mode and name.
    /// </summary>
    public static IReadOnlyList<SqlParameter> Parse(string arguments)
    {
        var result = new List<SqlParameter>();
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return result;
        }

        var depth = 0;
        var start = 0;
        var tokens = SqlLexer.Tokenize(arguments);
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case SqlTokenKind.OpenParen or SqlTokenKind.OpenBracket:
                    depth++;
                    break;
                case SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket:
                    depth--;
                    break;
                case SqlTokenKind.Comma when depth == 0:
                    result.Add(ParseOne(arguments, start, token.Start));
                    start = token.End;
                    break;
            }
        }

        result.Add(ParseOne(arguments, start, arguments.Length));
        return result;
    }

    private static SqlParameter ParseOne(string arguments, int start, int end)
    {
        var text = arguments[start..end].Trim();
        var words = SqlLexer.Tokenize(text).Where(t => !t.IsTrivia).ToList();
        var i = 0;
        var variadic = false;
        if (words.Count > 1 && words[0].Kind == SqlTokenKind.Word && Modes.Contains(SqlLexer.FoldCase(text.AsSpan(words[0].Start, words[0].Length))))
        {
            variadic = SqlLexer.FoldCase(text.AsSpan(words[0].Start, words[0].Length)) == "variadic";
            i = 1;
        }

        string? name = null;
        if (words.Count - i >= 2 && words[i].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier
            && words[i + 1].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier
            && !(words[i].Kind == SqlTokenKind.Word && TypeLeadWords.Contains(SqlLexer.FoldCase(text.AsSpan(words[i].Start, words[i].Length)))))
        {
            name = SqlLexer.IdentifierName(text, words[i]);
        }

        return new SqlParameter(text, name, variadic);
    }
}
