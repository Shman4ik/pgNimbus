namespace PgNimbus.Core.Tests;

/// <summary>
/// <see cref="InternalSql"/> marks the SQL pgNimbus sends on its own behalf so
/// the slow-query shortlist (<see cref="Monitoring.StatementStatsService"/>)
/// can tell it apart from what a user ran.
/// </summary>
public class InternalSqlTests
{
    [Test]
    public async Task Tag_prefixes_the_marker()
    {
        var tagged = InternalSql.Tag("SELECT 1");
        await Assert.That(tagged).IsEqualTo("/* pgNimbus */ SELECT 1");
    }

    [Test]
    public async Task IsTagged_is_true_for_a_tagged_statement()
    {
        await Assert.That(InternalSql.IsTagged(InternalSql.Tag("SELECT 1"))).IsTrue();
    }

    [Test]
    public async Task IsTagged_is_false_for_user_sql()
    {
        await Assert.That(InternalSql.IsTagged("SELECT * FROM customers")).IsFalse();
    }

    [Test]
    public async Task IsTagged_is_false_when_the_marker_appears_later_in_the_text()
    {
        // Only a *leading* marker counts — a user statement that happens to
        // mention the marker text midway through must not be treated as ours.
        await Assert.That(InternalSql.IsTagged("SELECT '/* pgNimbus */' AS note")).IsFalse();
    }
}
