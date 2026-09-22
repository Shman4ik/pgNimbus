namespace PgNimbus.Core.Text;

/// <summary>What a <see cref="SqlToken"/> is, at the lexical level only.</summary>
public enum SqlTokenKind
{
    Whitespace,
    /// <summary><c>-- …</c> up to (not including) the end of the line.</summary>
    LineComment,
    /// <summary><c>/* … */</c>, nesting as Postgres does.</summary>
    BlockComment,
    /// <summary>An unquoted identifier or keyword — the lexer does not tell them apart.</summary>
    Word,
    /// <summary><c>"…"</c> or <c>U&amp;"…"</c>, with <c>""</c> as the escaped quote.</summary>
    QuotedIdentifier,
    /// <summary><c>'…'</c>, <c>E'…'</c>, <c>B'…'</c>, <c>X'…'</c>, <c>N'…'</c>, <c>U&amp;'…'</c>.</summary>
    String,
    /// <summary><c>$$…$$</c> / <c>$tag$…$tag$</c>.</summary>
    DollarString,
    Number,
    /// <summary>A positional parameter, <c>$1</c>.</summary>
    Parameter,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    Comma,
    Semicolon,
    Dot,
    /// <summary>The <c>::</c> cast operator.</summary>
    DoubleColon,
    /// <summary>Any other operator character (one per token).</summary>
    Operator,
    /// <summary>Anything the lexer has no name for (a lone <c>$</c>, a stray <c>\</c>) — one character.</summary>
    Other,
}

/// <summary>
/// One lexical token. Offsets are UTF-16 indexes into the text that was
/// tokenized, exactly like AvaloniaEdit's. <paramref name="IsIncomplete"/>
/// marks a literal, quoted identifier or block comment still open at the end
/// of the input — the ordinary state of something being typed, not an error.
/// </summary>
public readonly record struct SqlToken(SqlTokenKind Kind, int Start, int Length, bool IsIncomplete = false)
{
    public int End => Start + Length;

    /// <summary>Whitespace or a comment: text with no meaning to the statement.</summary>
    public bool IsTrivia => Kind is SqlTokenKind.Whitespace or SqlTokenKind.LineComment or SqlTokenKind.BlockComment;

    /// <summary>A string literal or a comment — prose, where the editor must not offer SQL.</summary>
    public bool IsProse => Kind is SqlTokenKind.String or SqlTokenKind.DollarString
        or SqlTokenKind.LineComment or SqlTokenKind.BlockComment;
}

/// <summary>
/// The one tokenizer every SQL consumer in Core is meant to share, so that
/// the statement splitter, completion and the rest agree on where a string,
/// a quoted identifier or a comment starts and ends. They used to each carry
/// their own scanner, and those disagreed: <c>E'can\'t;stop'</c> split into two
/// statements, and completion opened inside <c>$tag1$…$tag1$</c> because its
/// dollar-quote reader stopped at the digit.
/// </summary>
/// <remarks>
/// Rules follow the PostgreSQL lexical structure: unquoted identifiers start
/// with a letter (any script), <c>_</c> or a non-ASCII character and continue
/// with those plus digits and <c>$</c>; a dollar-quote tag follows the same
/// rules minus the <c>$</c>; block comments nest; <c>E'…'</c> strings take
/// backslash escapes. Plain <c>'…'</c> strings take them too only when
/// <c>standardConformingStrings</c> is off — the server default has been on
/// since 9.1, so that is the default here as well.
/// Every branch consumes at least one character, so tokenizing always
/// terminates and the tokens exactly tile the input range.
/// </remarks>
public static class SqlLexer
{
    public static List<SqlToken> Tokenize(string sql, bool standardConformingStrings = true) =>
        Tokenize(sql, 0, sql.Length, standardConformingStrings);

    /// <summary>Tokenizes <c>sql[start..end)</c>; offsets stay relative to <paramref name="sql"/>.</summary>
    public static List<SqlToken> Tokenize(string sql, int start, int end, bool standardConformingStrings = true)
    {
        end = Math.Clamp(end, 0, sql.Length);
        var tokens = new List<SqlToken>();
        var i = Math.Clamp(start, 0, end);
        while (i < end)
        {
            var token = Next(sql, i, end, standardConformingStrings);
            tokens.Add(token);
            i = token.End;
        }

        return tokens;
    }

    /// <summary>First character of an unquoted identifier (or of a dollar-quote tag).</summary>
    public static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c >= '';

    /// <summary>A continuing character of an unquoted identifier.</summary>
    public static bool IsIdentPart(char c) => IsIdentStart(c) || char.IsAsciiDigit(c) || c == '$';

    /// <summary>
    /// How Postgres folds an unquoted identifier: ASCII letters to lower case,
    /// everything else untouched (the server leaves non-ASCII alone in UTF-8).
    /// </summary>
    public static string FoldCase(ReadOnlySpan<char> word)
    {
        Span<char> buffer = word.Length <= 256 ? stackalloc char[word.Length] : new char[word.Length];
        for (var i = 0; i < word.Length; i++)
        {
            buffer[i] = char.IsAsciiLetterUpper(word[i]) ? (char)(word[i] | 0x20) : word[i];
        }

        return new string(buffer);
    }

    /// <summary>
    /// The name an identifier token denotes: folded for a bare word, the
    /// unescaped content for a quoted one (an unterminated quote yields what
    /// has been typed so far).
    /// </summary>
    public static string IdentifierName(string sql, SqlToken token)
    {
        var text = sql.AsSpan(token.Start, token.Length);
        if (token.Kind != SqlTokenKind.QuotedIdentifier)
        {
            return FoldCase(text);
        }

        var open = text.IndexOf('"');
        var inner = text[(open + 1)..];
        if (!token.IsIncomplete && inner.Length > 0)
        {
            inner = inner[..^1];
        }

        return inner.ToString().Replace("\"\"", "\"");
    }

    private static SqlToken Next(string sql, int i, int end, bool scs)
    {
        var c = sql[i];

        if (char.IsWhiteSpace(c))
        {
            var j = i + 1;
            while (j < end && char.IsWhiteSpace(sql[j]))
            {
                j++;
            }

            return new SqlToken(SqlTokenKind.Whitespace, i, j - i);
        }

        if (c == '-' && Peek(sql, i + 1, end) == '-')
        {
            var eol = sql.IndexOf('\n', i + 2, end - (i + 2));
            var stop = eol < 0 ? end : eol;
            return new SqlToken(SqlTokenKind.LineComment, i, stop - i);
        }

        if (c == '/' && Peek(sql, i + 1, end) == '*')
        {
            return BlockComment(sql, i, end);
        }

        switch (c)
        {
            case '\'':
                return QuotedRun(sql, i, i, end, '\'', backslashEscapes: !scs, SqlTokenKind.String);
            case '"':
                return QuotedRun(sql, i, i, end, '"', backslashEscapes: false, SqlTokenKind.QuotedIdentifier);
            case '$':
                return Dollar(sql, i, end);
            case '(':
                return new SqlToken(SqlTokenKind.OpenParen, i, 1);
            case ')':
                return new SqlToken(SqlTokenKind.CloseParen, i, 1);
            case '[':
                return new SqlToken(SqlTokenKind.OpenBracket, i, 1);
            case ']':
                return new SqlToken(SqlTokenKind.CloseBracket, i, 1);
            case ',':
                return new SqlToken(SqlTokenKind.Comma, i, 1);
            case ';':
                return new SqlToken(SqlTokenKind.Semicolon, i, 1);
            case ':' when Peek(sql, i + 1, end) == ':':
                return new SqlToken(SqlTokenKind.DoubleColon, i, 2);
            case '.' when !char.IsAsciiDigit(Peek(sql, i + 1, end)):
                return new SqlToken(SqlTokenKind.Dot, i, 1);
        }

        if (char.IsAsciiDigit(c) || c == '.')
        {
            return Number(sql, i, end);
        }

        if (IsIdentStart(c))
        {
            // String-literal prefixes glued to the quote: E'…', B'…', X'…',
            // N'…', U&'…' and U&"…". Only a one-letter word qualifies — "life'"
            // is an identifier followed by a string.
            var next = Peek(sql, i + 1, end);
            if (next == '\'' && c is 'E' or 'e' or 'B' or 'b' or 'X' or 'x' or 'N' or 'n')
            {
                return QuotedRun(sql, i, i + 1, end, '\'', backslashEscapes: c is 'E' or 'e' || !scs, SqlTokenKind.String);
            }

            if (next == '&' && c is 'U' or 'u' && Peek(sql, i + 2, end) is '\'' or '"')
            {
                var quote = sql[i + 2];
                return QuotedRun(sql, i, i + 2, end, quote, backslashEscapes: false,
                    quote == '"' ? SqlTokenKind.QuotedIdentifier : SqlTokenKind.String);
            }

            var j = i + 1;
            while (j < end && IsIdentPart(sql[j]))
            {
                j++;
            }

            return new SqlToken(SqlTokenKind.Word, i, j - i);
        }

        return IsOperatorChar(c)
            ? new SqlToken(SqlTokenKind.Operator, i, 1)
            : new SqlToken(SqlTokenKind.Other, i, 1);
    }

    private static char Peek(string sql, int i, int end) => i < end ? sql[i] : '\0';

    private static bool IsOperatorChar(char c) => c is '+' or '-' or '*' or '/' or '<' or '>' or '='
        or '~' or '!' or '@' or '#' or '%' or '^' or '&' or '|' or '`' or '?' or ':';

    private static SqlToken BlockComment(string sql, int i, int end)
    {
        var depth = 1;
        var j = i + 2;
        while (j < end && depth > 0)
        {
            if (sql[j] == '/' && Peek(sql, j + 1, end) == '*')
            {
                depth++;
                j += 2;
            }
            else if (sql[j] == '*' && Peek(sql, j + 1, end) == '/')
            {
                depth--;
                j += 2;
            }
            else
            {
                j++;
            }
        }

        return new SqlToken(SqlTokenKind.BlockComment, i, j - i, depth > 0);
    }

    // A quote-delimited run starting at `start` whose opening quote sits at
    // `quoteAt` (after any prefix). A doubled quote is an escape; with
    // backslash escapes on, \x consumes the next character too.
    private static SqlToken QuotedRun(string sql, int start, int quoteAt, int end, char quote, bool backslashEscapes, SqlTokenKind kind)
    {
        var j = quoteAt + 1;
        while (j < end)
        {
            var c = sql[j];
            if (backslashEscapes && c == '\\')
            {
                j = Math.Min(j + 2, end);
                continue;
            }

            if (c == quote)
            {
                if (Peek(sql, j + 1, end) == quote)
                {
                    j += 2;
                    continue;
                }

                return new SqlToken(kind, start, j + 1 - start);
            }

            j++;
        }

        return new SqlToken(kind, start, end - start, IsIncomplete: true);
    }

    // $1 is a parameter; $tag$ / $$ opens a dollar-quoted string; anything
    // else is a stray '$'.
    private static SqlToken Dollar(string sql, int i, int end)
    {
        var j = i + 1;
        if (j < end && char.IsAsciiDigit(sql[j]))
        {
            while (j < end && char.IsAsciiDigit(sql[j]))
            {
                j++;
            }

            return new SqlToken(SqlTokenKind.Parameter, i, j - i);
        }

        if (j < end && IsIdentStart(sql[j]))
        {
            j++;
            while (j < end && (IsIdentStart(sql[j]) || char.IsAsciiDigit(sql[j])))
            {
                j++;
            }
        }

        if (j >= end || sql[j] != '$')
        {
            return new SqlToken(SqlTokenKind.Other, i, 1);
        }

        var tagLength = j + 1 - i;
        var close = sql.IndexOf(sql.Substring(i, tagLength), j + 1, end - (j + 1), StringComparison.Ordinal);
        return close < 0
            ? new SqlToken(SqlTokenKind.DollarString, i, end - i, IsIncomplete: true)
            : new SqlToken(SqlTokenKind.DollarString, i, close + tagLength - i);
    }

    private static SqlToken Number(string sql, int i, int end)
    {
        var j = i;
        while (j < end && (char.IsAsciiDigit(sql[j]) || sql[j] == '_'))
        {
            j++;
        }

        if (j < end && sql[j] == '.' && Peek(sql, j + 1, end) != '.')
        {
            j++;
            while (j < end && (char.IsAsciiDigit(sql[j]) || sql[j] == '_'))
            {
                j++;
            }
        }

        if (j < end && sql[j] is 'e' or 'E')
        {
            var k = j + 1;
            if (k < end && sql[k] is '+' or '-')
            {
                k++;
            }

            if (k < end && char.IsAsciiDigit(sql[k]))
            {
                j = k;
                while (j < end && char.IsAsciiDigit(sql[j]))
                {
                    j++;
                }
            }
        }

        return new SqlToken(SqlTokenKind.Number, i, Math.Max(j - i, 1));
    }
}
