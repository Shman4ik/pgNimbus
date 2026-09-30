namespace PgNimbus.Core.Query;

/// <summary>
/// Makes a value safe to place inside a <c>--</c> comment of a generated script.
///
/// A <c>--</c> comment ends at the next <c>\n</c> or <c>\r</c> (Postgres's
/// lexer: <c>non_newline [^\n\r]</c>), and a schema or relation name may
/// legally contain either. Every script the app opens for review names the
/// objects it is about in a leading comment, so a table named
/// <c>"x⏎ALTER ROLE eve SUPERUSER;--"</c> would end the comment after
/// <c>x</c> and leave a live statement on the next line — run in autocommit
/// before the statement the script was opened for even fails. Quoting does
/// not help inside a comment; only the line breaks do, so they are the one
/// thing stripped. Every value that lands in a comment goes through
/// <see cref="Safe"/>; the quoted identifiers in the statements below it are
/// safe already.
/// </summary>
public static class SqlComment
{
    /// <summary>Replaces every <c>\r</c> and <c>\n</c> with a space, so the text cannot end the comment line it is placed on.</summary>
    public static string Safe(string text) =>
        text.Replace('\r', ' ').Replace('\n', ' ');
}
