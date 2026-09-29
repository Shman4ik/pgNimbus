using Npgsql;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

/// <summary>
/// Security audit 2026-09, finding 18: an edit is cast to the column's type,
/// and <c>format_type</c> spells a type on the search_path bare, so a schema
/// created later that shadows the name changed what the cast resolved to.
/// <see cref="ColumnDetail.CastTargetType"/> is qualified outside pg_catalog
/// and bare for the built-ins; <see cref="ColumnDetail.DataType"/> keeps the
/// display spelling.
/// </summary>
public sealed class SchemaServiceCastTypeTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task A_column_detail_without_a_qualified_type_casts_to_its_data_type()
    {
        var column = new ColumnDetail("c", "integer", NotNull: false, IsPrimaryKey: false);

        await Assert.That(column.CastTargetType).IsEqualTo("integer");
        await Assert.That((column with { QualifiedDataType = "public.mood" }).CastTargetType).IsEqualTo("public.mood");
    }

    [Test]
    public async Task User_types_are_qualified_for_casts_and_built_ins_stay_bare()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to read column types from.");
        }

        // In public, which is on the default search_path: the case where
        // format_type leaves the name bare and a shadowing schema could take it.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var mood = $"pgn_mood_{suffix}";
        var table = $"pgn_cast_{suffix}";
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await ExecuteAsync(dataSource, $"""
            CREATE TYPE public.{mood} AS ENUM ('ok', 'bad');
            CREATE TABLE public.{table} (
                id integer PRIMARY KEY,
                feeling public.{mood},
                feelings public.{mood}[],
                label character varying(20),
                at timestamp with time zone);
            """);

        try
        {
            var columns = (await new SchemaService(dataSource).GetColumnsAsync("public", table, CancellationToken.None))
                .ToDictionary(c => c.Name);

            await Assert.That(columns["feeling"].DataType).IsEqualTo(mood);
            await Assert.That(columns["feeling"].CastTargetType).IsEqualTo($"public.{mood}");
            await Assert.That(columns["feelings"].CastTargetType).IsEqualTo($"public.{mood}[]");
            await Assert.That(columns["id"].CastTargetType).IsEqualTo("integer");
            await Assert.That(columns["label"].CastTargetType).IsEqualTo("character varying(20)");
            await Assert.That(columns["at"].CastTargetType).IsEqualTo("timestamp with time zone");

            // The narrowed search_path was transaction-local: the pooled
            // connection hands the next reader its ordinary path back.
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var show = new NpgsqlCommand("SHOW search_path", connection);
            await Assert.That((string?)await show.ExecuteScalarAsync()).IsNotEqualTo("pg_catalog");
        }
        finally
        {
            await ExecuteAsync(dataSource, $"""
                DROP TABLE IF EXISTS public.{table};
                DROP TYPE IF EXISTS public.{mood};
                """);
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
