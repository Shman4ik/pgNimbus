using System.Text;

namespace PgNimbus.Core.Query;

/// <summary>One identifier the reconciler would rewrite: the bare token as typed, and the quoted catalog form it should become.</summary>
public readonly record struct IdentifierFix(string Original, string Replacement);

/// <summary>
/// Reconciles the unquoted identifiers in a hand-typed query against the live
/// catalog and proposes the correctly-quoted spelling. Postgres folds an
/// unquoted identifier to lowercase, so <c>games.spells</c> looks for
/// <c>spells</c> and fails when the real table is <c>"Spells"</c>. This finds
/// each token that would fail that way and rewrites it to the real, quoted name.
/// </summary>
/// <remarks>
/// The rewrite rule is deliberately conservative — a wrong rewrite is worse than
/// the original error, so a token is only touched when both hold:
/// <list type="number">
/// <item>its lowercase-folded form matches <b>no</b> real catalog object (i.e. it
/// would fail as written), and</item>
/// <item>exactly <b>one</b> real catalog object matches it case-insensitively.</item>
/// </list>
/// That leaves legitimately-lowercase names, aliases, functions, and keywords
/// alone (none have a case-differing catalog twin), and refuses to guess when a
/// name is ambiguous. It is a lexer, not a parser: it skips string literals,
/// quoted identifiers, dollar-quoted bodies, and comments (the same lexical
/// contexts <see cref="SqlScriptSplitter"/> respects), but does not track scope,
/// so it is only ever offered as an explicit fix after a query has already
/// failed — never applied silently.
/// </remarks>
public sealed class IdentifierReconciler
{
    private readonly HashSet<string> _realNames;
    private readonly ILookup<string, string> _byFoldedName;

    /// <param name="catalogNames">Every real (case-preserved) schema, relation, and column name reachable from the connection.</param>
    public IdentifierReconciler(IEnumerable<string> catalogNames)
    {
        _realNames = new HashSet<string>(catalogNames, StringComparer.Ordinal);
        _byFoldedName = _realNames.ToLookup(n => n.ToLowerInvariant(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Scans <paramref name="sql"/> and, if any unquoted identifiers can be
    /// unambiguously mapped to a case-differing catalog name, returns the rewritten
    /// SQL. Returns false (and leaves <paramref name="fixedSql"/> equal to the
    /// input) when nothing safe can be changed.
    /// </summary>
    public bool TryReconcile(string sql, out string fixedSql, out IReadOnlyList<IdentifierFix> fixes)
    {
        var builder = new StringBuilder(sql.Length + 16);
        var found = new List<IdentifierFix>();
        var n = sql.Length;
        var i = 0;

        while (i < n)
        {
            var c = sql[i];
            switch (c)
            {
                case '\'':
                case '"':
                    var closeQuote = SkipQuoted(sql, i, c);
                    builder.Append(sql, i, closeQuote - i);
                    i = closeQuote;
                    break;
                case '-' when i + 1 < n && sql[i + 1] == '-':
                    var endLine = SkipLineComment(sql, i);
                    builder.Append(sql, i, endLine - i);
                    i = endLine;
                    break;
                case '/' when i + 1 < n && sql[i + 1] == '*':
                    var endBlock = SkipBlockComment(sql, i);
                    builder.Append(sql, i, endBlock - i);
                    i = endBlock;
                    break;
                case '$':
                    var endDollar = SkipDollarQuote(sql, i);
                    if (endDollar > i)
                    {
                        builder.Append(sql, i, endDollar - i);
                        i = endDollar;
                    }
                    else
                    {
                        builder.Append(c);
                        i++;
                    }

                    break;
                default:
                    if (char.IsAsciiLetter(c) || c == '_')
                    {
                        var end = i + 1;
                        while (end < n && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_' || sql[end] == '$'))
                        {
                            end++;
                        }

                        var token = sql[i..end];
                        if (TryMap(token, out var replacement))
                        {
                            builder.Append(replacement);
                            found.Add(new IdentifierFix(token, replacement));
                        }
                        else
                        {
                            builder.Append(token);
                        }

                        i = end;
                    }
                    else
                    {
                        builder.Append(c);
                        i++;
                    }

                    break;
            }
        }

        fixes = found;
        if (found.Count == 0)
        {
            fixedSql = sql;
            return false;
        }

        fixedSql = builder.ToString();
        return true;
    }

    // A bare token maps to a quoted catalog name only when it currently resolves
    // to nothing (its lowercase form is not a real name) yet exactly one real name
    // matches it case-insensitively. Any such target necessarily needs quoting: if
    // it were bare-safe it would equal its own lowercase form and the token would
    // already resolve.
    private bool TryMap(string token, out string replacement)
    {
        replacement = string.Empty;
        var folded = token.ToLowerInvariant();

        if (_realNames.Contains(folded))
        {
            return false; // resolves as written
        }

        string? single = null;
        foreach (var candidate in _byFoldedName[folded])
        {
            if (single is null)
            {
                single = candidate;
            }
            else if (!string.Equals(single, candidate, StringComparison.Ordinal))
            {
                return false; // ambiguous — refuse to guess
            }
        }

        if (single is null)
        {
            return false; // no catalog twin
        }

        replacement = SqlIdentifier.Quote(single);
        return true;
    }

    // The lexical-skip helpers below mirror SqlScriptSplitter's semantics: they
    // find the extent of a string/comment/dollar-quote so its contents are copied
    // through verbatim and never mistaken for identifiers.

    private static int SkipQuoted(string sql, int i, char quote)
    {
        var n = sql.Length;
        var j = i + 1;
        while (j < n)
        {
            if (sql[j] == quote)
            {
                if (j + 1 < n && sql[j + 1] == quote)
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            j++;
        }

        return n;
    }

    private static int SkipLineComment(string sql, int i)
    {
        var n = sql.Length;
        var j = i + 2;
        while (j < n && sql[j] != '\n')
        {
            j++;
        }

        return j;
    }

    private static int SkipBlockComment(string sql, int i)
    {
        var n = sql.Length;
        var j = i + 2;
        var depth = 1;
        while (j < n && depth > 0)
        {
            if (j + 1 < n && sql[j] == '/' && sql[j + 1] == '*')
            {
                depth++;
                j += 2;
            }
            else if (j + 1 < n && sql[j] == '*' && sql[j + 1] == '/')
            {
                depth--;
                j += 2;
            }
            else
            {
                j++;
            }
        }

        return j;
    }

    // Returns the index past a dollar-quoted string's close tag, or i when sql[i]
    // does not open one (a bare '$' or a `$1` parameter).
    private static int SkipDollarQuote(string sql, int i)
    {
        var n = sql.Length;
        var j = i + 1;
        while (j < n && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_'))
        {
            j++;
        }

        if (j >= n || sql[j] != '$')
        {
            return i;
        }

        if (j > i + 1 && char.IsDigit(sql[i + 1]))
        {
            return i;
        }

        var tag = sql[i..(j + 1)];
        var close = sql.IndexOf(tag, j + 1, StringComparison.Ordinal);
        return close < 0 ? n : close + tag.Length;
    }
}
