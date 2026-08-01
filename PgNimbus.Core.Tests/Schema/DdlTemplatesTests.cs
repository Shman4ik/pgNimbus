using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

public class DdlTemplatesTests
{
    [Test]
    public async Task NewTable_QualifiesWithTheSchema()
    {
        var sql = DdlTemplates.NewTable("analytics");

        await Assert.That(sql).StartsWith("CREATE TABLE analytics.new_table (");
        await Assert.That(sql).EndsWith(");");
    }

    [Test]
    public async Task NewTable_QuotesASchemaThatNeedsIt()
    {
        // A mixed-case or reserved schema name has to come back quoted, or the
        // template pasted into the editor wouldn't even parse.
        await Assert.That(DdlTemplates.NewTable("Reporting")).StartsWith("""CREATE TABLE "Reporting".new_table""");
    }
}
