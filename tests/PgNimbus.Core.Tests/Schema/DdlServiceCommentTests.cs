using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

/// <summary>
/// The "not found" text a source tab shows opens in an editor, so a relation
/// name carrying a line break must not end its comment and leave a statement
/// behind it (security audit 2026-09, finding 11).
/// </summary>
public class DdlServiceCommentTests
{
    [Test]
    [Arguments("x\nALTER ROLE eve SUPERUSER;--")]
    [Arguments("x\r\nALTER ROLE eve SUPERUSER;--")]
    public async Task A_missing_relation_is_one_comment_line_whatever_its_name(string name)
    {
        var text = DdlService.RelationNotFoundComment("s\nchema", name);

        await Assert.That(text.Split(['\n', '\r'])).Count().IsEqualTo(1);
        await Assert.That(text).StartsWith("-- ");
        await Assert.That(text).EndsWith(" not found");
        await Assert.That(text).Contains("ALTER ROLE eve SUPERUSER;--");
    }

    [Test]
    [Arguments("x\nALTER ROLE eve SUPERUSER;--")]
    [Arguments("x\r\nALTER ROLE eve SUPERUSER;--")]
    public async Task A_missing_function_is_one_comment_line_whatever_its_name_or_arguments(string name)
    {
        var text = DdlService.FunctionNotFoundComment("public", name, "a \"t\nype\"");

        await Assert.That(text.Split(['\n', '\r'])).Count().IsEqualTo(1);
        await Assert.That(text).StartsWith("-- function ");
        await Assert.That(text).EndsWith(") not found");
    }

    [Test]
    public async Task A_plain_name_reads_as_before()
    {
        await Assert.That(DdlService.RelationNotFoundComment("public", "orders")).IsEqualTo("-- public.orders not found");
        await Assert.That(DdlService.FunctionNotFoundComment("public", "f", "integer")).IsEqualTo("-- function public.f(integer) not found");
    }
}
