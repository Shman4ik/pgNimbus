using Npgsql;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

/// <summary>
/// <see cref="SchemaService.GetTypesAsync"/> against a real server: what a
/// cast can name (domains, enums, free-standing composites, built-in base
/// types by their SQL spelling) and what it must not offer (table row types,
/// array types, pseudo-types). Gated on <c>PGNIMBUS_TEST_CONN</c>.
/// </summary>
[NotInParallel]
public class SchemaServiceTypesTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task Castable_types_are_read_and_row_and_array_types_are_not()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to read types from.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using (var seed = dataSource.CreateCommand(
            """
            DROP SCHEMA IF EXISTS pgn_types CASCADE;
            CREATE SCHEMA pgn_types;
            CREATE DOMAIN pgn_types.email AS text;
            CREATE TYPE pgn_types.mood AS ENUM ('ok', 'bad');
            CREATE TYPE pgn_types.pair AS (a int, b int);
            CREATE TABLE pgn_types.person (id int);
            """))
        {
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            var types = await new SchemaService(dataSource).GetTypesAsync(CancellationToken.None);
            var mine = types.Where(t => t.Schema == "pgn_types").Select(t => $"{t.Name}:{t.Kind}").ToList();

            await Assert.That(mine).IsEquivalentTo(new[] { "email:d", "mood:e", "pair:c" });
            await Assert.That(types.Any(t => t is { Schema: "pg_catalog", Name: "int4", DisplayName: "integer" })).IsTrue();
            await Assert.That(types.Any(t => t is { Schema: "pg_catalog", Name: "timestamptz", DisplayName: "timestamp with time zone" })).IsTrue();
            await Assert.That(types.Any(t => t.Name is "_int4" or "record" or "anyelement")).IsFalse();
        }
        finally
        {
            await using var drop = dataSource.CreateCommand("DROP SCHEMA IF EXISTS pgn_types CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
