using System.Text;

namespace PgNimbus.Core.Security;

/// <summary>
/// Strips password literals out of SQL before it can be persisted. A safety
/// net, not a formatter: <c>PgNimbus.Core.Query.QueryHistoryStore</c> writes
/// executed statements to disk in the clear and
/// <c>PgNimbus.Core.Diagnostics.CrashLog</c> can capture statement text, so a
/// <c>CREATE ROLE … PASSWORD 'hunter2'</c> that reaches either one has put a
/// live credential in a plain file. This runs on the way to disk, independent
/// of whichever call site produced the statement — the redaction belongs next
/// to the statement inspector in Core, not bolted onto one App call site that
/// the next feature will bypass.
///
/// <para>Hand-rolled scanner rather than a regular expression, deliberately.
/// A regex over SQL string literals has to model doubled <c>''</c> escapes,
/// <c>E''</c> backslash escapes and dollar quoting all at once, and it has to
/// avoid matching the word PASSWORD when that word is itself inside a literal
/// or a quoted identifier. That is precisely the class of expression that looks
/// right, passes its examples, and silently fails to match the one input that
/// mattered. A scanner that tracks what it is inside of cannot make that
/// mistake.</para>
///
/// <para>The bias is toward over-redacting: a false positive costs a mangled
/// history entry, a false negative writes a password to disk. An unterminated
/// literal is therefore swallowed to end-of-input rather than given the benefit
/// of the doubt.</para>
///
/// <para>Known limit: the word PASSWORD inside a comment is not treated as a
/// keyword, so a commented-out statement keeps its literal. Comments are not
/// executed, so nothing this app runs takes that path.</para>
/// </summary>
public static class SecretRedactor
{
    /// <summary>What a redacted literal is replaced with, quotes included.</summary>
    public const string Replacement = "'<redacted>'";

    /// <summary>
    /// Returns <paramref name="sql"/> with every password literal replaced by
    /// <see cref="Replacement"/>, or the input unchanged when there is nothing
    /// to redact. <c>PASSWORD NULL</c> is left alone: it is not a secret, it is
    /// the removal of one, and rewriting it would change what the history says
    /// happened.
    /// </summary>
    public static string Redact(string sql) => Scan(sql, out var redacted) ? redacted : sql;

    /// <summary>True when <see cref="Redact"/> would change something.</summary>
    public static bool ContainsSecret(string sql) => Scan(sql, out _);

    /// <summary>
    /// The one pass both entry points share. Returns whether a secret was
    /// found; <paramref name="redacted"/> is only meaningful when it was.
    /// </summary>
    private static bool Scan(string sql, out string redacted)
    {
        redacted = sql;
        if (string.IsNullOrEmpty(sql))
        {
            return false;
        }

        StringBuilder? output = null;
        var copied = 0;
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (TrySkipComment(sql, i, out var afterComment))
            {
                i = afterComment;
                continue;
            }

            if (c == '"')
            {
                i = SkipQuotedIdentifier(sql, i);
                continue;
            }

            if (TryReadStringLiteral(sql, i, out var afterLiteral))
            {
                i = afterLiteral;
                continue;
            }

            if (TryReadDollarQuoted(sql, i, out var afterDollar))
            {
                i = afterDollar;
                continue;
            }

            if (IsWordStart(c))
            {
                var wordEnd = ReadWord(sql, i);
                var isPassword = string.Compare(sql, i, "PASSWORD", 0, 8, StringComparison.OrdinalIgnoreCase) == 0
                                 && wordEnd - i == 8;

                if (!isPassword)
                {
                    i = wordEnd;
                    continue;
                }

                // The keyword is preceded by ENCRYPTED / UNENCRYPTED in some
                // dialects and grammars; that changes nothing here, because the
                // literal always follows PASSWORD itself.
                var valueStart = SkipTrivia(sql, wordEnd);

                if (valueStart < sql.Length && StartsWithWord(sql, valueStart, "NULL"))
                {
                    i = wordEnd;
                    continue;
                }

                int valueEnd;
                if (!TryReadStringLiteral(sql, valueStart, out valueEnd)
                    && !TryReadDollarQuoted(sql, valueStart, out valueEnd))
                {
                    // Not a literal — a parameter placeholder, a variable, or a
                    // truncated statement. Nothing to strip.
                    i = wordEnd;
                    continue;
                }

                output ??= new StringBuilder(sql.Length);
                output.Append(sql, copied, valueStart - copied).Append(Replacement);
                copied = valueEnd;
                i = valueEnd;
                continue;
            }

            i++;
        }

        if (output is null)
        {
            return false;
        }

        output.Append(sql, copied, sql.Length - copied);
        redacted = output.ToString();
        return true;
    }

    /// <summary>Whitespace and comments between the keyword and its value.</summary>
    private static int SkipTrivia(string sql, int i)
    {
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i]))
            {
                i++;
                continue;
            }

            if (TrySkipComment(sql, i, out var after))
            {
                i = after;
                continue;
            }

            break;
        }

        return i;
    }

    private static bool TrySkipComment(string sql, int i, out int end)
    {
        end = i;

        if (i + 1 >= sql.Length)
        {
            return false;
        }

        if (sql[i] == '-' && sql[i + 1] == '-')
        {
            var newline = sql.IndexOf('\n', i + 2);
            end = newline < 0 ? sql.Length : newline + 1;
            return true;
        }

        if (sql[i] == '/' && sql[i + 1] == '*')
        {
            // Postgres block comments nest, so a depth counter, not a search
            // for the first "*/".
            var depth = 1;
            var p = i + 2;
            while (p + 1 < sql.Length && depth > 0)
            {
                if (sql[p] == '/' && sql[p + 1] == '*')
                {
                    depth++;
                    p += 2;
                }
                else if (sql[p] == '*' && sql[p + 1] == '/')
                {
                    depth--;
                    p += 2;
                }
                else
                {
                    p++;
                }
            }

            end = depth > 0 ? sql.Length : p;
            return true;
        }

        return false;
    }

    private static int SkipQuotedIdentifier(string sql, int i)
    {
        var p = i + 1;
        while (p < sql.Length)
        {
            if (sql[p] == '"')
            {
                if (p + 1 < sql.Length && sql[p + 1] == '"')
                {
                    p += 2;
                    continue;
                }

                return p + 1;
            }

            p++;
        }

        return sql.Length;
    }

    /// <summary>
    /// A single-quoted literal starting at <paramref name="i"/>, including an
    /// <c>E</c>/<c>e</c> or <c>U&amp;</c> prefix if one is there. Doubled
    /// <c>''</c> stays inside the literal; inside an E-string a backslash
    /// escapes the next character. An unterminated literal runs to the end of
    /// the input.
    /// </summary>
    private static bool TryReadStringLiteral(string sql, int i, out int end)
    {
        end = i;
        if (i >= sql.Length)
        {
            return false;
        }

        var quote = i;
        var backslashEscapes = false;

        if ((sql[i] == 'E' || sql[i] == 'e') && i + 1 < sql.Length && sql[i + 1] == '\'')
        {
            quote = i + 1;
            backslashEscapes = true;
        }
        else if ((sql[i] == 'U' || sql[i] == 'u') && i + 2 < sql.Length && sql[i + 1] == '&' && sql[i + 2] == '\'')
        {
            quote = i + 2;
        }
        else if (sql[i] != '\'')
        {
            return false;
        }

        var p = quote + 1;
        while (p < sql.Length)
        {
            var c = sql[p];

            if (backslashEscapes && c == '\\')
            {
                p += 2;
                continue;
            }

            if (c == '\'')
            {
                if (p + 1 < sql.Length && sql[p + 1] == '\'')
                {
                    p += 2;
                    continue;
                }

                end = p + 1;
                return true;
            }

            p++;
        }

        end = sql.Length;
        return true;
    }

    /// <summary>
    /// A dollar-quoted literal — <c>$$…$$</c> or <c>$tag$…$tag$</c>. A tag has
    /// to look like an unquoted identifier, which is what keeps <c>$1</c> a
    /// positional parameter rather than the start of a quote.
    /// </summary>
    private static bool TryReadDollarQuoted(string sql, int i, out int end)
    {
        end = i;
        if (i >= sql.Length || sql[i] != '$')
        {
            return false;
        }

        var p = i + 1;
        while (p < sql.Length && (char.IsLetterOrDigit(sql[p]) || sql[p] == '_'))
        {
            p++;
        }

        if (p >= sql.Length || sql[p] != '$')
        {
            return false;
        }

        var tagLength = p - (i + 1);
        if (tagLength > 0 && char.IsDigit(sql[i + 1]))
        {
            return false;
        }

        var delimiter = sql.Substring(i, tagLength + 2);
        var close = sql.IndexOf(delimiter, p + 1, StringComparison.Ordinal);
        end = close < 0 ? sql.Length : close + delimiter.Length;
        return true;
    }

    private static bool StartsWithWord(string sql, int i, string word)
    {
        if (string.Compare(sql, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        var after = i + word.Length;
        return after >= sql.Length || !IsWordChar(sql[after]);
    }

    private static int ReadWord(string sql, int i)
    {
        var p = i;
        while (p < sql.Length && IsWordChar(sql[p]))
        {
            p++;
        }

        return p;
    }

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
