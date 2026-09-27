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
}
