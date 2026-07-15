using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

public class PendingChangeSetTests
{
    private static PendingChangeSet NewSet(params string[] pkColumns) =>
        new("public", "orders", pkColumns.Length == 0 ? ["id"] : pkColumns);

    [Test]
    public async Task StartsEmpty()
    {
        var set = NewSet();

        await Assert.That(set.IsEmpty).IsTrue();
        await Assert.That(set.Count).IsEqualTo(0);
        await Assert.That(set.BuildStatements()).IsEmpty();
        await Assert.That(set.BuildScript()).IsEmpty();
    }

    [Test]
    public async Task EditBuildsPrimaryKeyTargetedUpdate()
    {
        var set = NewSet();
        set.StageEdit([42], "status", "shipped");

        var statements = set.BuildStatements();

        await Assert.That(statements).Count().IsEqualTo(1);
        await Assert.That(statements[0].Sql)
            .IsEqualTo("""UPDATE "public"."orders" SET "status" = @v0 WHERE "id" = @pk0""");
        await Assert.That(statements[0].Parameters["v0"]).IsEqualTo("shipped");
        await Assert.That(statements[0].Parameters["pk0"]).IsEqualTo(42);
    }

    [Test]
    public async Task RepeatedEditsToSameCellCoalesce()
    {
        var set = NewSet();
        set.StageEdit([1], "status", "packed");
        set.StageEdit([1], "status", "shipped");

        var statements = set.BuildStatements();

        await Assert.That(set.Count).IsEqualTo(1);
        await Assert.That(statements).Count().IsEqualTo(1);
        await Assert.That(statements[0].Parameters["v0"]).IsEqualTo("shipped");
    }

    [Test]
    public async Task EditWithCastTypeWrapsTheParameterServerSide()
    {
        var set = NewSet();
        set.StageEdit([42], "mood", "happy", "mood");
        set.StageEdit([42], "tags", "{a,b}", "text[]");

        var statement = set.BuildStatements()[0];

        await Assert.That(statement.Sql).IsEqualTo(
            """UPDATE "public"."orders" SET "mood" = CAST(@v0 AS mood), "tags" = CAST(@v1 AS text[]) WHERE "id" = @pk0""");
        await Assert.That(statement.Parameters["v0"]).IsEqualTo("happy");
    }

    [Test]
    public async Task ScriptRendersCastEditsAsCastLiterals()
    {
        var set = NewSet();
        set.StageEdit([1], "mood", "happy", "mood");

        await Assert.That(set.BuildScript()).Contains(
            """UPDATE "public"."orders" SET "mood" = CAST('happy' AS mood) WHERE "id" = 1;""");
    }

    [Test]
    public async Task RecastingACellReplacesItsEarlierCastType()
    {
        var set = NewSet();
        set.StageEdit([1], "mood", "happy", "mood");
        set.StageEdit([1], "mood", null);

        // The re-staged plain value (e.g. an explicit NULL) must not keep the
        // superseded edit's cast.
        await Assert.That(set.BuildStatements()[0].Sql).Contains("\"mood\" = @v0");
        await Assert.That(set.BuildStatements()[0].Parameters["v0"]).IsNull();
    }

    [Test]
    public async Task EditsToDifferentColumnsOfOneRowShareOneUpdate()
    {
        var set = NewSet();
        set.StageEdit([1], "status", "shipped");
        set.StageEdit([1], "total", 99.5m);

        var statements = set.BuildStatements();

        await Assert.That(set.Count).IsEqualTo(1);
        await Assert.That(statements).Count().IsEqualTo(1);
        await Assert.That(statements[0].Sql)
            .IsEqualTo("""UPDATE "public"."orders" SET "status" = @v0, "total" = @v1 WHERE "id" = @pk0""");
    }

    [Test]
    public async Task RowIdentityIsStructuralAcrossGridReloads()
    {
        var set = NewSet("region", "id");
        set.StageEdit(["eu", 7], "status", "shipped");

        // A fresh array with equal values (a reloaded page) is the same row.
        await Assert.That(set.IsRowEdited(["eu", 7])).IsTrue();
        await Assert.That(set.GetRowEdits(["eu", 7])).IsNotNull();
        await Assert.That(set.IsRowEdited(["us", 7])).IsFalse();
    }

    [Test]
    public async Task CompositeKeyBuildsAndJoinedWhere()
    {
        var set = NewSet("region", "id");
        set.StageDelete(["eu", 7]);

        var statements = set.BuildStatements();

        await Assert.That(statements[0].Sql)
            .IsEqualTo("""DELETE FROM "public"."orders" WHERE "region" = @pk0 AND "id" = @pk1""");
        await Assert.That(statements[0].Parameters["pk0"]).IsEqualTo("eu");
        await Assert.That(statements[0].Parameters["pk1"]).IsEqualTo(7);
    }

    [Test]
    public async Task PrimaryKeyColumnsCannotBeEdited()
    {
        var set = NewSet();

        await Assert.That(() => set.StageEdit([1], "id", 2)).Throws<ArgumentException>();
    }

    [Test]
    public async Task DeleteSupersedesRowEditsUntilUnstaged()
    {
        var set = NewSet();
        set.StageEdit([1], "status", "shipped");
        set.StageDelete([1]);

        // While the delete is staged, the row's edits don't count or execute…
        await Assert.That(set.Count).IsEqualTo(1);
        await Assert.That(set.BuildStatements()).Count().IsEqualTo(1);
        await Assert.That(set.BuildStatements()[0].Sql).StartsWith("DELETE");

        // …and editing it is refused outright.
        await Assert.That(() => set.StageEdit([1], "total", 5)).Throws<InvalidOperationException>();

        // Unstaging the delete brings the edits back.
        await Assert.That(set.UnstageDelete([1])).IsTrue();
        await Assert.That(set.Count).IsEqualTo(1);
        await Assert.That(set.BuildStatements()[0].Sql).StartsWith("UPDATE");
    }

    [Test]
    public async Task StatementsOrderUpdatesThenDeletesThenInserts()
    {
        var set = NewSet();
        set.StageInsert([new PendingInsertValue("status", "text", "new")]);
        set.StageDelete([2]);
        set.StageEdit([1], "status", "shipped");

        var kinds = set.BuildStatements().Select(s => s.Sql.Split(' ')[0]).ToList();

        await Assert.That(string.Join('|', kinds)).IsEqualTo("UPDATE|DELETE|INSERT");
    }

    [Test]
    public async Task InsertCastsTypedValuesAndInlinesNulls()
    {
        var set = NewSet();
        set.StageInsert(
        [
            new PendingInsertValue("total", "numeric(10,2)", "99.50"),
            new PendingInsertValue("note", "text", null),
        ]);

        var statement = set.BuildStatements()[0];

        await Assert.That(statement.Sql).IsEqualTo(
            """INSERT INTO "public"."orders" ("total", "note") VALUES (CAST(@p0 AS numeric(10,2)), NULL)""");
        await Assert.That(statement.Parameters["p0"]).IsEqualTo("99.50");
    }

    [Test]
    public async Task EmptyInsertUsesDefaultValues()
    {
        var set = NewSet();
        set.StageInsert([]);

        await Assert.That(set.BuildStatements()[0].Sql)
            .IsEqualTo("""INSERT INTO "public"."orders" DEFAULT VALUES""");
    }

    [Test]
    public async Task ScriptRendersLiteralsForReview()
    {
        var set = new PendingChangeSet("public", "people", ["id"]);
        set.StageEdit([1], "name", "O'Brien");
        set.StageEdit([1], "active", true);
        set.StageDelete([2]);
        set.StageInsert([new PendingInsertValue("name", "text", "new 'guy'")]);

        var script = set.BuildScript();

        await Assert.That(script).Contains(
            """UPDATE "public"."people" SET "name" = 'O''Brien', "active" = true WHERE "id" = 1;""");
        await Assert.That(script).Contains("""DELETE FROM "public"."people" WHERE "id" = 2;""");
        await Assert.That(script).Contains(
            """INSERT INTO "public"."people" ("name") VALUES (CAST('new ''guy''' AS text));""");
    }

    [Test]
    public async Task ScriptRendersNullAndNumericLiterals()
    {
        var set = NewSet();
        set.StageEdit([1], "note", null);
        set.StageEdit([1], "total", 12.5m);

        var script = set.BuildScript();

        await Assert.That(script).Contains("\"note\" = NULL, \"total\" = 12.5");
    }

    [Test]
    public async Task ClearEmptiesEverything()
    {
        var set = NewSet();
        set.StageEdit([1], "status", "x");
        set.StageDelete([2]);
        set.StageInsert([]);

        set.Clear();

        await Assert.That(set.IsEmpty).IsTrue();
        await Assert.That(set.BuildStatements()).IsEmpty();
    }

    [Test]
    public async Task MismatchedKeyLengthIsRejected()
    {
        var set = NewSet("region", "id");

        await Assert.That(() => set.StageDelete([1])).Throws<ArgumentException>();
    }

    [Test]
    public async Task QuotedIdentifiersSurviveRoundTrip()
    {
        var set = new PendingChangeSet("Games", "Spell Book", ["Id"]);
        set.StageEdit([1], "Mana Cost", 3);

        var sql = set.BuildStatements()[0].Sql;

        await Assert.That(sql).IsEqualTo(
            """UPDATE "Games"."Spell Book" SET "Mana Cost" = @v0 WHERE "Id" = @pk0""");
    }
}
