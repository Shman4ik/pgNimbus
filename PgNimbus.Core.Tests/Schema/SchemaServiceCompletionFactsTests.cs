using Npgsql;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

/// <summary>
/// The catalog facts completion filters by (sql-completion-audit-2.md E03,
/// E05), read from a real server: which functions are machinery, which
/// relations are partitions. Gated on <c>PGNIMBUS_TEST_CONN</c>.
/// </summary>
[NotInParallel]
public class SchemaServiceCompletionFactsTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task Internal_functions_are_marked_from_the_catalog_not_the_name()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to read functions from.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        var builtins = (await new SchemaService(dataSource).GetFunctionsAsync("pg_catalog", CancellationToken.None))
            .GroupBy(f => f.Name)
            .ToDictionary(g => g.Key, g => g.All(f => f.IsInternal));

        // Type I/O, a boolean operator's implementation, an aggregate's
        // transition function, an estimator, an index AM handler.
        foreach (var name in new[] { "int4in", "texteq", "int4_sum", "eqsel", "bthandler" })
        {
            await Assert.That(builtins[name]).IsTrue();
        }

        // "implementation of || operator", by the server's own description.
        await Assert.That(builtins["textcat"]).IsTrue();

        foreach (var name in new[] { "lower", "now", "count", "date_trunc", "pg_size_pretty", "generate_series" })
        {
            await Assert.That(builtins[name]).IsFalse();
        }
    }

    [Test]
    public async Task Partitions_and_relation_kinds_are_read()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to read relations from.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using (var seed = dataSource.CreateCommand(
            """
            DROP SCHEMA IF EXISTS pgn_facts CASCADE;
            CREATE SCHEMA pgn_facts;
            CREATE TABLE pgn_facts.readings (at date) PARTITION BY RANGE (at);
            CREATE TABLE pgn_facts.readings_2026 PARTITION OF pgn_facts.readings FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
            CREATE VIEW pgn_facts.recent AS SELECT * FROM pgn_facts.readings;
            """))
        {
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            var relations = await new SchemaService(dataSource).GetCompletionRelationsAsync("pgn_facts", CancellationToken.None);

            await Assert.That(relations).IsEquivalentTo(new[]
            {
                new CompletionRelationInfo("readings", 'p', false),
                new CompletionRelationInfo("readings_2026", 'r', true),
                new CompletionRelationInfo("recent", 'v', false),
            });
        }
        finally
        {
            await using var drop = dataSource.CreateCommand("DROP SCHEMA IF EXISTS pgn_facts CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Test]
    public async Task Column_facts_enum_labels_defaults_sequences_and_settings_are_read()
    {
        // E06, E07 and what package N reads: from a real server.
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to read the catalog from.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using (var seed = dataSource.CreateCommand(
            """
            DROP SCHEMA IF EXISTS pgn_facts2 CASCADE;
            CREATE SCHEMA pgn_facts2;
            CREATE TYPE pgn_facts2.mood AS ENUM ('sad', 'ok', 'happy');
            CREATE TABLE pgn_facts2.people (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name text NOT NULL DEFAULT 'x',
                upper_name text GENERATED ALWAYS AS (upper(name)) STORED,
                mood pgn_facts2.mood);
            COMMENT ON COLUMN pgn_facts2.people.name IS 'what they are called';
            COMMENT ON TABLE pgn_facts2.people IS 'everyone';
            CREATE SEQUENCE pgn_facts2.tickets;
            CREATE FUNCTION pgn_facts2.greet(who text, loud boolean DEFAULT false) RETURNS text
                LANGUAGE sql AS $$ SELECT who $$;
            COMMENT ON FUNCTION pgn_facts2.greet(text, boolean) IS 'says hello';
            """))
        {
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            var service = new SchemaService(dataSource);
            var columns = (await service.GetAllColumnsAsync("pgn_facts2", CancellationToken.None)).ToDictionary(c => c.Column);
            await Assert.That(columns["id"].IsPrimaryKey).IsTrue();
            await Assert.That(columns["id"].Identity).IsEqualTo('a');
            await Assert.That(columns["name"].NotNull).IsTrue();
            await Assert.That(columns["name"].HasDefault).IsTrue();
            await Assert.That(columns["name"].Comment).IsEqualTo("what they are called");
            await Assert.That(columns["upper_name"].IsGenerated).IsTrue();
            await Assert.That(columns["upper_name"].HasDefault).IsFalse();

            var people = (await service.GetCompletionRelationsAsync("pgn_facts2", CancellationToken.None)).Single();
            await Assert.That(people.Comment).IsEqualTo("everyone");

            var mood = (await service.GetTypesAsync(CancellationToken.None)).Single(t => t.Schema == "pgn_facts2" && t.Name == "mood");
            await Assert.That(mood.EnumLabels).IsEquivalentTo(new[] { "sad", "ok", "happy" }, CollectionOrdering.Matching);

            var greet = (await service.GetFunctionsAsync("pgn_facts2", CancellationToken.None)).Single();
            await Assert.That(greet.Arguments).IsEqualTo("who text, loud boolean");
            await Assert.That(greet.FullArguments).IsEqualTo("who text, loud boolean DEFAULT false");
            await Assert.That(greet.Description).IsEqualTo("says hello");

            await Assert.That(await service.GetSequenceNamesAsync(CancellationToken.None)).Contains(new SequenceName("pgn_facts2", "tickets"));
            var settings = await service.GetSettingsAsync(CancellationToken.None);
            await Assert.That(settings.Single(s => s.Name == "work_mem").VarType).IsEqualTo("integer");
            await Assert.That(settings.Single(s => s.Name == "client_min_messages").EnumValues).Contains("notice");
        }
        finally
        {
            await using var drop = dataSource.CreateCommand("DROP SCHEMA IF EXISTS pgn_facts2 CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
