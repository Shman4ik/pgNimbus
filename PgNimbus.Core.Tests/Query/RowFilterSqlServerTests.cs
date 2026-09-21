using Npgsql;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Runs the predicates <see cref="RowFilterSql"/> generates against a real
/// server, one per type family, and counts what matches. The unit tests pin
/// the text; these pin that the text means what the filter bar says: that an
/// untyped literal really is typed by its column (numeric, enum, timestamptz),
/// that a text search escapes its wildcards, that json gets a search it can
/// actually run, and that the NULL tests see NULLs.
///
/// Gated on <c>PGNIMBUS_TEST_CONN</c> like the other server tests.
/// </summary>
[NotInParallel]
public class RowFilterSqlServerTests
{
    private const string EnumType = "pgnimbus_filter_scratch_status";
    private const string ScratchTable = "pgnimbus_filter_scratch";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    [Arguments("note", ColumnValueEditor.Text, "text", FilterOperator.Contains, "50%", 1)]
    [Arguments("note", ColumnValueEditor.Text, "text", FilterOperator.StartsWith, "o'br", 1)]
    [Arguments("note", ColumnValueEditor.Text, "text", FilterOperator.NotContains, "_", 2)]
    [Arguments("total", ColumnValueEditor.Text, "numeric(10,2)", FilterOperator.GreaterOrEqual, "10.5", 2)]
    [Arguments("total", ColumnValueEditor.Text, "numeric(10,2)", FilterOperator.Contains, "0.5", 1)]
    [Arguments("status", ColumnValueEditor.Enum, EnumType, FilterOperator.Equals, "shipped", 1)]
    [Arguments("status", ColumnValueEditor.Enum, EnumType, FilterOperator.NotEquals, "shipped", 1)]
    [Arguments("paid", ColumnValueEditor.Boolean, "boolean", FilterOperator.IsFalse, null, 1)]
    [Arguments("paid", ColumnValueEditor.Boolean, "boolean", FilterOperator.IsNull, null, 1)]
    [Arguments("payload", ColumnValueEditor.Json, "json", FilterOperator.Contains, "coupon", 1)]
    [Arguments("placed_at", ColumnValueEditor.Timestamp, "timestamp with time zone", FilterOperator.Less, "2026-07-14 09:00:00+00", 1)]
    [Arguments("shipped_on", ColumnValueEditor.Date, "date", FilterOperator.IsNotNull, null, 2)]
    public async Task Predicate_matches_what_the_filter_says(
        string column, ColumnValueEditor editor, string dataType, FilterOperator op, string? value, int expected)
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres available to run filter predicates against.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using var connection = await dataSource.OpenConnectionAsync();
        await ExecuteAsync(connection, $$"""
            DROP TABLE IF EXISTS {{ScratchTable}};
            DROP TYPE IF EXISTS {{EnumType}};
            CREATE TYPE {{EnumType}} AS ENUM ('pending', 'shipped');
            CREATE TABLE {{ScratchTable}} (
                id int PRIMARY KEY,
                note text,
                total numeric(10,2),
                status {{EnumType}},
                paid boolean,
                payload json,
                placed_at timestamptz,
                shipped_on date);
            INSERT INTO {{ScratchTable}} VALUES
                (1, '50% off', 10.50, 'shipped', true,  '{"coupon": "X"}', '2026-07-14 08:00:00+00', '2026-07-15'),
                (2, 'O''Brien', 99.00, 'pending', false, '{}',              '2026-07-14 10:00:00+00', NULL),
                (3, 'snake_case', 1.00, NULL,     NULL,  NULL,               NULL,                     '2026-07-16');
            """);

        try
        {
            var predicate = RowFilterSql.ToPredicate(new RowFilter(column, op, value), editor, dataType);
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {ScratchTable} WHERE {predicate}", connection);
            var matched = (long)(await count.ExecuteScalarAsync())!;

            await Assert.That(matched).IsEqualTo(expected);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {ScratchTable}; DROP TYPE IF EXISTS {EnumType};");
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
