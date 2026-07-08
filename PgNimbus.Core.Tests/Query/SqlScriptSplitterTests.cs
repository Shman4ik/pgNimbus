using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

public class SqlScriptSplitterTests
{
    [Test]
    public async Task SplitsOnTopLevelSemicolons()
    {
        var statements = SqlScriptSplitter.Split("select 1; select 2 ; select 3");

        await Assert.That(string.Join('|', statements)).IsEqualTo("select 1|select 2|select 3");
    }

    [Test]
    public async Task IgnoresSemicolonsInsideLexicalContexts()
    {
        var statements = SqlScriptSplitter.Split(
            "select ';' as a; select 1 -- not here;\n; select $tag$ ; $tag$; select /* ; */ 2");

        await Assert.That(string.Join('|', statements)).IsEqualTo(
            "select ';' as a|select 1 -- not here;|select $tag$ ; $tag$|select /* ; */ 2");
    }

    [Test]
    public async Task DropsEmptyAndCommentOnlyStatements()
    {
        var statements = SqlScriptSplitter.Split(";;  ; -- only a comment\n;select 1;");

        await Assert.That(string.Join('|', statements)).IsEqualTo("select 1");
    }

    [Test]
    public async Task StatementSpanAtCoversCaretStatement()
    {
        const string sql = "select 1;\nselect 2;";
        var caretInSecond = sql.IndexOf('2');

        var span = SqlScriptSplitter.StatementSpanAt(sql, caretInSecond);

        await Assert.That(span).IsNotNull();
        await Assert.That(sql[span!.Value.Start..span.Value.End]).IsEqualTo("select 2");
    }
}
