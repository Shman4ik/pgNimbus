using System.Text;
using PgNimbus.Core.Text;

namespace PgNimbus.Core.Security;

/// <summary>
/// Strips password literals out of SQL before it is written to disk.
/// <c>PgNimbus.Core.Query.QueryHistoryStore</c> passes every entry through
/// this on save (and scrubs entries written before it existed on load), and
/// <c>PgNimbus.Core.Settings.WorkspaceStore</c> passes every tab's text through
/// it on the way into <c>workspace.json</c>. Those are the two files that hold
/// statement text the user never asked to save, so the redaction lives in the
/// stores themselves, not in whichever call site feeds them. A saved query or
/// a <c>.sql</c> file the user saves on purpose is written as is.
///
/// <para>A lexer pass rather than a regular expression, deliberately. A regex
/// over SQL string literals has to model doubled <c>''</c> escapes, <c>E''</c>
/// backslash escapes and dollar quoting all at once, and it has to avoid
/// matching the word PASSWORD when that word is itself inside a literal or a
/// quoted identifier. That is precisely the class of expression that looks
/// right, passes its examples, and silently fails to match the one input that
/// mattered. The statement is read with the shared <see cref="SqlLexer"/>.</para>
///
/// <para>What it finds, at the top level: the literal after the keyword
/// PASSWORD (<c>CREATE ROLE … PASSWORD 'p'</c>, <c>OPTIONS (password 'p')</c>)
/// and after a named argument ending in <c>password</c>
/// (<c>f(password =&gt; 'p')</c>). Then it looks <em>inside</em> every string
/// literal, dollar-quoted body and comment, since that is where the
/// secret sits in the shapes the 2026-09 security audit (finding 8) found in
/// <c>history.json</c>: a <c>DO $$ … CREATE ROLE … PASSWORD 'p' … $$</c>
/// migration, an <c>EXECUTE 'ALTER ROLE … PASSWORD ''p'''</c>, a conninfo
/// <c>password=p</c> in <c>CREATE SUBSCRIPTION … CONNECTION '…'</c> or
/// <c>dblink_connect('…')</c>, and a commented-out
/// <c>-- ALTER ROLE x PASSWORD 'old'</c>. Inside those it reads the text again
/// as SQL (a string's escapes decoded first, and the replacement encoded back),
/// and also scans it more loosely, as prose: any word PASSWORD followed by a
/// literal, any <c>…password=value</c> pair, and the password part of a
/// <c>scheme://user:password@host</c> URI. A string whose text ends in a
/// PASSWORD with nothing after it (<c>'… PASSWORD '</c>, or a
/// <c>format('… PASSWORD %L', …)</c> placeholder) means the secret is being
/// glued on from elsewhere, so every later literal in the same statement is
/// redacted whole.</para>
///
/// <para>The bias is toward over-redacting: a false positive costs a mangled
/// history entry, a false negative writes a password to disk. An unterminated
/// literal is therefore swallowed to end-of-input rather than given the benefit
/// of the doubt, and the loose scan inside literals and comments will now and
/// then redact something that was not a secret. What stays untouched is the
/// ordinary SQL around a secret: <c>SET password_encryption</c>, a column named
/// <c>password</c>, <c>SELECT 'password'</c>.</para>
///
/// <para>Known limit: a secret passed as an ordinary argument
/// (<c>pgp_sym_encrypt(data, 'key')</c>) or kept in a variable is not
/// recognised. There is no word next to it that says what it is.</para>
/// </summary>
public static class SecretRedactor
{
    /// <summary>
    /// What a redacted literal is replaced with. The cast is what makes it safe
    /// to run again: a password slot (<c>PASSWORD</c>, a user mapping's
    /// <c>OPTIONS</c>) takes only a string constant, so a restored or
    /// history-opened <c>ALTER ROLE x PASSWORD '&lt;redacted&gt;'::redacted</c> is a
    /// syntax error, where the bare literal used to set the password to the text
    /// <c>&lt;redacted&gt;</c> (review of the 2026-09 audit fixes). A literal
    /// already followed by the cast is left alone, which keeps redaction
    /// idempotent; a bare marker from before is given the cast.
    /// </summary>
    public const string Replacement = "'<redacted>'::redacted";

    /// <summary>What a redacted conninfo or URI value is replaced with, no quotes: it sits inside a string already.</summary>
    public const string ValueReplacement = "<redacted>";

    // How many literals-inside-literals are read again as SQL. Each level
    // re-tokenizes its contents, so this bounds the work on a pathological
    // input (a thousand nested comments); the linear prose scans still run at
    // every level past it.
    private const int MaxNesting = 4;

    private readonly record struct Edit(int Start, int End, string Text);

    /// <summary>
    /// Returns <paramref name="sql"/> with every secret it recognises replaced,
    /// or the input itself when there is nothing to redact. <c>PASSWORD NULL</c>
    /// is left alone: it is not a secret, it is the removal of one, and
    /// rewriting it would change what the history says happened. Redacting an
    /// already redacted text changes nothing.
    /// </summary>
    public static string Redact(string sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return sql;
        }

        var edits = new List<Edit>();
        CollectStatement(sql, 0, edits);
        if (edits.Count == 0)
        {
            return sql;
        }

        var redacted = Apply(sql, edits);
        return string.Equals(redacted, sql, StringComparison.Ordinal) ? sql : redacted;
    }

    // Replaces a secret literal with the marker, unless the marker is already
    // there: redaction must be idempotent, or the history's load-time scrub
    // would rewrite the file on every launch.
    private static void AddLiteralEdit(string text, int start, int end, List<Edit> edits)
    {
        if (!text.AsSpan(start).StartsWith(Replacement, StringComparison.Ordinal))
        {
            edits.Add(new Edit(start, end, Replacement));
        }
    }

    /// <summary>True when <see cref="Redact"/> would change something.</summary>
    public static bool ContainsSecret(string sql) => !string.Equals(Redact(sql), sql, StringComparison.Ordinal);

    // Reads text as SQL: the literal after PASSWORD (or a named argument
    // ending in "password"), then the insides of every literal and comment.
    // Returns nothing; edits are in `text`'s coordinates.
    private static void CollectStatement(string text, int depth, List<Edit> edits)
    {
        var tokens = SqlLexer.Tokenize(text);
        // Set by a string that leaves a PASSWORD hanging; every later literal
        // in the statement is then taken for the secret.
        var redactLiterals = false;

        for (var t = 0; t < tokens.Count; t++)
        {
            var token = tokens[t];
            switch (token.Kind)
            {
                case SqlTokenKind.Semicolon:
                    redactLiterals = false;
                    break;

                case SqlTokenKind.Word when TryFindSecretLiteral(text, tokens, t, out var literal):
                    for (var k = t + 1; k < literal; k++)
                    {
                        if (tokens[k].Kind is SqlTokenKind.LineComment or SqlTokenKind.BlockComment)
                        {
                            CollectComment(text, tokens[k], depth, edits);
                        }
                    }

                    AddLiteralEdit(text, tokens[literal].Start, tokens[literal].End, edits);
                    t = literal;
                    break;

                case SqlTokenKind.String or SqlTokenKind.DollarString:
                    if (redactLiterals && !IsBitString(text, token))
                    {
                        AddLiteralEdit(text, token.Start, token.End, edits);
                    }
                    else if (CollectLiteral(text, token, depth, edits))
                    {
                        redactLiterals = true;
                    }

                    break;

                case SqlTokenKind.LineComment or SqlTokenKind.BlockComment:
                    CollectComment(text, token, depth, edits);
                    break;
            }
        }
    }

    // PASSWORD <literal>, or <…password> => <literal> / := <literal>.
    // Comments and whitespace may sit in between.
    private static bool TryFindSecretLiteral(string text, List<SqlToken> tokens, int word, out int literal)
    {
        literal = -1;
        var token = tokens[word];
        var name = text.AsSpan(token.Start, token.Length);

        if (!name.EndsWith("password", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var next = NextSignificant(tokens, word + 1);
        // PASSWORD NULL is a Word, so it never counts as a literal.
        if (name.Length == 8 && IsSecretLiteral(text, tokens, next))
        {
            literal = next;
            return true;
        }

        if (next >= 0 && next + 1 < tokens.Count
            && tokens[next].Kind == SqlTokenKind.Operator && tokens[next + 1].Kind == SqlTokenKind.Operator
            && (IsChars(text, tokens[next], tokens[next + 1], '=', '>') || IsChars(text, tokens[next], tokens[next + 1], ':', '=')))
        {
            literal = NextSignificant(tokens, next + 2);
            return IsSecretLiteral(text, tokens, literal);
        }

        return false;
    }

    private static bool IsSecretLiteral(string text, List<SqlToken> tokens, int index) =>
        index >= 0 && tokens[index].Kind is SqlTokenKind.String or SqlTokenKind.DollarString
                   && !IsBitString(text, tokens[index]);

    private static bool IsChars(string text, SqlToken first, SqlToken second, char a, char b) =>
        text[first.Start] == a && text[second.Start] == b && second.Start == first.End;

    private static int NextSignificant(List<SqlToken> tokens, int from)
    {
        for (var i = from; i < tokens.Count; i++)
        {
            if (!tokens[i].IsTrivia)
            {
                return i;
            }
        }

        return -1;
    }

    // B'…' and X'…' hold bits, not text: nothing to look inside, nothing to hide.
    private static bool IsBitString(string text, SqlToken token) =>
        token.Kind == SqlTokenKind.String && text[token.Start] is 'B' or 'b' or 'X' or 'x';

    /// <summary>
    /// Looks inside a string or dollar-quoted literal. Returns true when its
    /// text leaves a PASSWORD hanging for a later literal to fill.
    /// </summary>
    private static bool CollectLiteral(string text, SqlToken token, int depth, List<Edit> edits)
    {
        if (token.Kind == SqlTokenKind.DollarString)
        {
            var tagLength = text.IndexOf('$', token.Start + 1) - token.Start + 1;
            var start = token.Start + tagLength;
            var end = Math.Max(start, token.IsIncomplete ? token.End : token.End - tagLength);
            return CollectRaw(text, start, end, depth, edits);
        }

        if (IsBitString(text, token))
        {
            return false;
        }

        // Past any E / N / U& prefix. A plain string decodes '' to '; an
        // E-string also \' and \\. Everything else is kept as written, so a
        // map from each decoded character back to where it came from is
        // enough to put an edit back in the original text.
        var quote = text.IndexOf('\'', token.Start, token.Length);
        var backslashes = text[token.Start] is 'E' or 'e';
        var contentStart = quote + 1;
        var contentEnd = Math.Max(contentStart, token.IsIncomplete ? token.End : token.End - 1);

        var decoded = new StringBuilder(contentEnd - contentStart);
        var map = new List<int>(contentEnd - contentStart + 1);
        var p = contentStart;
        while (p < contentEnd)
        {
            map.Add(p);
            var c = text[p];
            if (c == '\'' && p + 1 < contentEnd && text[p + 1] == '\'')
            {
                decoded.Append('\'');
                p += 2;
            }
            else if (backslashes && c == '\\' && p + 1 < contentEnd && text[p + 1] is '\'' or '\\')
            {
                decoded.Append(text[p + 1]);
                p += 2;
            }
            else
            {
                decoded.Append(c);
                p++;
            }
        }

        map.Add(contentEnd);

        var inner = new List<Edit>();
        var dangling = CollectProse(decoded.ToString(), depth + 1, inner);
        foreach (var edit in inner)
        {
            edits.Add(new Edit(map[edit.Start], map[edit.End], edit.Text.Replace("'", "''", StringComparison.Ordinal)));
        }

        return dangling;
    }

    private static void CollectComment(string text, SqlToken token, int depth, List<Edit> edits)
    {
        var start = token.Start + 2;
        var end = token.Kind == SqlTokenKind.BlockComment && !token.IsIncomplete ? token.End - 2 : token.End;
        CollectRaw(text, start, Math.Max(start, end), depth, edits);
    }

    // Text taken verbatim from text[start..end) (a dollar body, a comment):
    // offsets only shift.
    private static bool CollectRaw(string text, int start, int end, int depth, List<Edit> edits)
    {
        var inner = new List<Edit>();
        var dangling = CollectProse(text.Substring(start, end - start), depth + 1, inner);
        foreach (var edit in inner)
        {
            edits.Add(new Edit(edit.Start + start, edit.End + start, edit.Text));
        }

        return dangling;
    }

    // What sits inside a literal or a comment: read as SQL again (up to a
    // depth), and scanned as prose. Returns whether a PASSWORD is left hanging.
    private static bool CollectProse(string content, int depth, List<Edit> edits)
    {
        if (depth <= MaxNesting)
        {
            CollectStatement(content, depth, edits);
        }

        var dangling = CollectLoosePasswords(content, edits);
        CollectKeyValuePasswords(content, edits);
        CollectUriPasswords(content, edits);
        return dangling;
    }

    // Any word PASSWORD followed by a literal, whatever surrounds it: an
    // apostrophe earlier in a comment ("don't") must not hide the statement
    // after it. Also reports a PASSWORD with the value missing: followed by
    // text that then ends, by a format() placeholder, or by a literal that
    // runs off the end.
    private static bool CollectLoosePasswords(string content, List<Edit> edits)
    {
        var dangling = false;
        var i = 0;
        while (i < content.Length)
        {
            if (!IsWordAt(content, i, out var wordEnd))
            {
                i++;
                continue;
            }

            if (wordEnd - i == 8 && content.AsSpan(i, 8).Equals("PASSWORD", StringComparison.OrdinalIgnoreCase))
            {
                var p = SkipTrivia(content, wordEnd);
                if (p >= content.Length)
                {
                    dangling |= p > wordEnd;
                }
                else if (content[p] == '%')
                {
                    dangling = true;
                }
                else
                {
                    var literal = SqlLexer.TokenAt(content, p);
                    if (literal.Kind is SqlTokenKind.String or SqlTokenKind.DollarString && !IsBitString(content, literal))
                    {
                        AddLiteralEdit(content, literal.Start, literal.End, edits);
                        dangling |= literal.IsIncomplete;
                    }
                }
            }

            i = wordEnd;
        }

        return dangling;
    }

    // libpq conninfo and friends: password=value, sslpassword=value,
    // PGPASSWORD=value, ?password=value in a URI. A quoted value is taken to
    // its closing quote, a bare one to the next whitespace or quote.
    private static void CollectKeyValuePasswords(string content, List<Edit> edits)
    {
        var i = 0;
        while (i < content.Length)
        {
            if (!IsWordAt(content, i, out var wordEnd))
            {
                i++;
                continue;
            }

            if (content.AsSpan(i, wordEnd - i).EndsWith("password", StringComparison.OrdinalIgnoreCase))
            {
                var p = SkipSpaces(content, wordEnd);
                if (p < content.Length && content[p] == '=')
                {
                    p++;
                    if (p < content.Length && content[p] == '>')
                    {
                        p++;
                    }

                    p = SkipSpaces(content, p);
                    var end = p;
                    if (end < content.Length && content[end] == '\'')
                    {
                        end++;
                        while (end < content.Length)
                        {
                            if (content[end] == '\\')
                            {
                                end += 2;
                                continue;
                            }

                            end++;
                            if (content[end - 1] == '\'')
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        while (end < content.Length && !char.IsWhiteSpace(content[end]) && content[end] != '\'')
                        {
                            end += content[end] == '\\' ? 2 : 1;
                        }
                    }

                    end = Math.Min(end, content.Length);
                    if (end > p)
                    {
                        edits.Add(new Edit(p, end, ValueReplacement));
                    }

                    i = Math.Max(end, wordEnd);
                    continue;
                }
            }

            i = wordEnd;
        }
    }

    // postgresql://user:secret@host/db — the part between the first ':' of
    // the user info and the '@'.
    private static void CollectUriPasswords(string content, List<Edit> edits)
    {
        var from = 0;
        while (from < content.Length)
        {
            var scheme = content.IndexOf("://", from, StringComparison.Ordinal);
            if (scheme < 0)
            {
                return;
            }

            var start = scheme + 3;
            var end = start;
            while (end < content.Length && content[end] is not ('@' or '/' or '\'' or '"' or '?' or '#') && !char.IsWhiteSpace(content[end]))
            {
                end++;
            }

            if (end < content.Length && content[end] == '@')
            {
                var colon = content.IndexOf(':', start, end - start);
                if (colon >= 0 && colon + 1 < end)
                {
                    edits.Add(new Edit(colon + 1, end, ValueReplacement));
                }
            }

            from = Math.Max(end, start);
        }
    }

    // An identifier-shaped word starting exactly at i (not in the middle of one).
    private static bool IsWordAt(string content, int i, out int end)
    {
        end = i;
        if (!SqlLexer.IsIdentStart(content[i]) || (i > 0 && SqlLexer.IsIdentPart(content[i - 1])))
        {
            return false;
        }

        end = i + 1;
        while (end < content.Length && SqlLexer.IsIdentPart(content[end]))
        {
            end++;
        }

        return true;
    }

    private static int SkipSpaces(string content, int i)
    {
        while (i < content.Length && char.IsWhiteSpace(content[i]))
        {
            i++;
        }

        return i;
    }

    private static int SkipTrivia(string content, int i)
    {
        while (i < content.Length)
        {
            if (char.IsWhiteSpace(content[i]))
            {
                i++;
                continue;
            }

            if (i + 1 < content.Length && ((content[i] == '-' && content[i + 1] == '-') || (content[i] == '/' && content[i + 1] == '*')))
            {
                i = SqlLexer.TokenAt(content, i).End;
                continue;
            }

            break;
        }

        return i;
    }

    // Edits can overlap (the SQL reading and the prose scan often find the
    // same literal). Overlapping ones merge into one span that covers both,
    // replaced once: more redacted, never less.
    private static string Apply(string sql, List<Edit> edits)
    {
        edits.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));

        var output = new StringBuilder(sql.Length);
        var copied = 0;
        var i = 0;
        while (i < edits.Count)
        {
            var edit = edits[i];
            var end = edit.End;
            i++;
            while (i < edits.Count && edits[i].Start < end)
            {
                end = Math.Max(end, edits[i].End);
                i++;
            }

            output.Append(sql, copied, edit.Start - copied).Append(edit.Text);
            copied = end;
        }

        output.Append(sql, copied, sql.Length - copied);
        return output.ToString();
    }
}
