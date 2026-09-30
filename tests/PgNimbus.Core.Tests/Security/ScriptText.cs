namespace PgNimbus.Core.Tests.Security;

/// <summary>
/// Helpers for reading a generated script the way the server would, shared by
/// the script-builder tests that check a name cannot break out of a comment.
/// </summary>
internal static class ScriptText
{
    /// <summary>
    /// The first line of <paramref name="statement"/> that is not a
    /// <c>--</c> comment — what the statement starts with once the leading
    /// commentary is skipped. A <c>--</c> comment ends at <c>\n</c> or
    /// <c>\r</c>, so the split is on both.
    /// </summary>
    public static string FirstStatementLine(string statement) =>
        statement.Split(['\n', '\r']).SkipWhile(l => l.StartsWith("--", StringComparison.Ordinal)).FirstOrDefault() ?? "";
}
