using Npgsql;
using NpgsqlTypes;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Npgsql reads a Postgres <c>infinity</c> or <c>-infinity</c> timestamp or date
/// as the largest or smallest <see cref="DateTime"/> (and <see cref="DateOnly"/>,
/// <see cref="DateTimeOffset"/>). The writers whose text is executed, filter by
/// cell (<see cref="RowFilterSql.ValueText"/>), <see cref="SqlLiteral.Format"/>
/// and the literal fallback <see cref="PgValueSyntax.InvariantText"/>, wrote
/// those as dates: filtering by an infinity cell sent
/// <c>9999-12-31 23:59:59.999999+00</c>, a finite timestamp, and matched nothing.
/// They write the words now, in a scalar and a range bound alike.
/// </summary>
[NotInParallel]
public sealed class InfinityLiteralTests
{
    // How Npgsql hands over a timestamptz: UTC kind. A timestamp is Unspecified.
    private static readonly DateTime UtcMax = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
    private static readonly DateTime UtcMin = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
    private static readonly DateTime Finite = new(2026, 7, 22, 19, 56, 13, DateTimeKind.Utc);

    [Test]
    public async Task Filter_value_text_writes_infinity_as_the_word()
    {
        await Assert.That(RowFilterSql.ValueText(UtcMax)).IsEqualTo("infinity");
        await Assert.That(RowFilterSql.ValueText(UtcMin)).IsEqualTo("-infinity");
        await Assert.That(RowFilterSql.ValueText(DateTime.MaxValue)).IsEqualTo("infinity");
        await Assert.That(RowFilterSql.ValueText(DateOnly.MaxValue)).IsEqualTo("infinity");
        await Assert.That(RowFilterSql.ValueText(DateOnly.MinValue)).IsEqualTo("-infinity");
        await Assert.That(RowFilterSql.ValueText(DateTimeOffset.MaxValue)).IsEqualTo("infinity");
        await Assert.That(RowFilterSql.ValueText(DateTimeOffset.MinValue)).IsEqualTo("-infinity");

        // A finite value beside it is unchanged.
        await Assert.That(RowFilterSql.ValueText(Finite)).IsEqualTo("2026-07-22 19:56:13+00");
    }

    [Test]
    public async Task Filter_value_text_writes_an_infinite_range_bound_as_the_word()
    {
        // An infinite bound is still a bound ([x,infinity) is not [x,)).
        await Assert.That(RowFilterSql.ValueText(new NpgsqlRange<DateTime>(Finite, true, UtcMax, false)))
            .IsEqualTo("[\"2026-07-22 19:56:13+00\",infinity)");
        await Assert.That(RowFilterSql.ValueText(new NpgsqlRange<DateOnly>(DateOnly.MinValue, true, new DateOnly(2026, 1, 1), false)))
            .IsEqualTo("[-infinity,2026-01-01)");
    }

    [Test]
    public async Task Sql_literal_writes_infinity_as_the_quoted_word()
    {
        await Assert.That(SqlLiteral.Format(UtcMax)).IsEqualTo("'infinity'");
        await Assert.That(SqlLiteral.Format(DateTime.MinValue)).IsEqualTo("'-infinity'");
        await Assert.That(SqlLiteral.Format(DateOnly.MaxValue)).IsEqualTo("'infinity'");
        await Assert.That(SqlLiteral.Format(DateTimeOffset.MinValue)).IsEqualTo("'-infinity'");
        await Assert.That(SqlLiteral.Format(new NpgsqlRange<DateTime>(UtcMin, true, UtcMax, false)))
            .IsEqualTo("'[-infinity,infinity)'");

        await Assert.That(SqlLiteral.Format(new DateTime(2026, 7, 14, 8, 30, 0))).IsEqualTo("'2026-07-14 08:30:00'");
    }

    [Test]
    public async Task Invariant_text_writes_infinity_as_the_word()
    {
        await Assert.That(PgValueSyntax.InvariantText(UtcMax)).IsEqualTo("infinity");
        await Assert.That(PgValueSyntax.InvariantText(DateOnly.MinValue)).IsEqualTo("-infinity");
        await Assert.That(PgValueSyntax.InvariantText(DateTimeOffset.MaxValue)).IsEqualTo("infinity");
        await Assert.That(PgValueSyntax.InvariantText(new NpgsqlRange<DateTime>(Finite, true, UtcMax, false)))
            .IsEqualTo("[2026-07-22T19:56:13.0000000Z,infinity)");

        // The array writer falls back to it for an element nobody formats.
        await Assert.That(PgValueSyntax.FormatArray(new[] { DateTime.MinValue, UtcMax }))
            .IsEqualTo("{-infinity,infinity}");
    }

    // --- Against a real server ----------------------------------------------

    private const string ScratchTable = "pgnimbus_infinity_scratch";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    /// <summary>
    /// "Filter by this cell" on an infinite cell: the value read from the server,
    /// written by <see cref="RowFilterSql.ValueText"/> into an equality filter,
    /// matches that row and no other. The same value through
    /// <see cref="SqlLiteral.Format"/> does too.
    /// </summary>
    [Test]
    [Arguments("ts", ColumnValueEditor.Timestamp, "timestamp without time zone", 1)]
    [Arguments("ts", ColumnValueEditor.Timestamp, "timestamp without time zone", 2)]
    [Arguments("tstz", ColumnValueEditor.Timestamp, "timestamp with time zone", 1)]
    [Arguments("tstz", ColumnValueEditor.Timestamp, "timestamp with time zone", 2)]
    [Arguments("d", ColumnValueEditor.Date, "date", 1)]
    [Arguments("d", ColumnValueEditor.Date, "date", 2)]
    [Arguments("r", ColumnValueEditor.CastText, "tstzrange", 1)]
    [Arguments("r", ColumnValueEditor.CastText, "tstzrange", 2)]
    [Arguments("dr", ColumnValueEditor.CastText, "daterange", 1)]
    public async Task A_filter_built_from_an_infinite_cell_matches_that_row(
        string column, ColumnValueEditor editor, string dataType, int id)
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to filter infinities against.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using var connection = await dataSource.OpenConnectionAsync();
        await ExecuteAsync(connection, $"""
            DROP TABLE IF EXISTS {ScratchTable};
            CREATE TABLE {ScratchTable} (
                id int PRIMARY KEY,
                ts timestamp,
                tstz timestamptz,
                d date,
                r tstzrange,
                dr daterange);
            INSERT INTO {ScratchTable} VALUES
                (1, 'infinity', 'infinity', 'infinity',
                    tstzrange('2026-07-22 19:56:13.543613+00', 'infinity'), daterange('2026-01-01', 'infinity')),
                (2, '-infinity', '-infinity', '-infinity',
                    tstzrange('-infinity', 'infinity'), daterange('2026-01-01', '2026-02-01')),
                (3, '2026-07-22 19:56:13', '2026-07-22 19:56:13+00', '2026-07-22',
                    tstzrange('2026-07-22 19:56:13+00', '2026-07-23 00:00:00+00'), daterange('2026-07-22', NULL));
            """);

        try
        {
            object cell;
            await using (var read = new NpgsqlCommand($"SELECT {column} FROM {ScratchTable} WHERE id = {id}", connection))
            {
                cell = (await read.ExecuteScalarAsync())!;
            }

            var filter = new RowFilter(column, FilterOperator.Equals, RowFilterSql.ValueText(cell));
            var predicate = RowFilterSql.ToPredicate(filter, editor, dataType);
            await Assert.That(await MatchingIdsAsync(connection, predicate)).IsEquivalentTo(new[] { id })
                .Because($"filter by cell: {predicate}");

            var literal = $"{column} = {SqlLiteral.Format(cell)}";
            await Assert.That(await MatchingIdsAsync(connection, literal)).IsEquivalentTo(new[] { id })
                .Because($"SqlLiteral: {literal}");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {ScratchTable};");
        }
    }

    private static async Task<List<int>> MatchingIdsAsync(NpgsqlConnection connection, string predicate)
    {
        var ids = new List<int>();
        await using var command = new NpgsqlCommand($"SELECT id FROM {ScratchTable} WHERE {predicate} ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
