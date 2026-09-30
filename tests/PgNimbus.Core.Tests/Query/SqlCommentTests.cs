using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// A <c>--</c> comment ends at the first <c>\n</c> or <c>\r</c>, so those two
/// are the whole of what a value placed in one has to lose (security audit
/// 2026-09, finding 11).
/// </summary>
public class SqlCommentTests
{
    [Test]
    [Arguments("x\nALTER ROLE eve SUPERUSER;--", "x ALTER ROLE eve SUPERUSER;--")]
    [Arguments("x\r\nALTER ROLE eve SUPERUSER;--", "x  ALTER ROLE eve SUPERUSER;--")]
    [Arguments("x\rALTER ROLE eve SUPERUSER;--", "x ALTER ROLE eve SUPERUSER;--")]
    [Arguments("plain name", "plain name")]
    [Arguments("", "")]
    public async Task Line_breaks_become_spaces_and_nothing_else_changes(string input, string expected)
    {
        await Assert.That(SqlComment.Safe(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task The_result_never_ends_a_comment_line()
    {
        var safe = SqlComment.Safe("a\nb\rc\r\nd");

        await Assert.That(safe.Contains('\n')).IsFalse();
        await Assert.That(safe.Contains('\r')).IsFalse();
    }
}
