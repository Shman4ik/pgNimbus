using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// bit columns against a real server (#354). Npgsql reads <c>bit(n)</c> as a
/// <see cref="System.Collections.BitArray"/> but <c>bit(1)</c> as a
/// <see cref="bool"/>, so a <c>bit(1)</c> column showed True/False and every
/// write of its value failed (<c>cannot cast type boolean to bit</c>). The
/// engine now requests every bit type as text by its Postgres type, so all of
/// them arrive as the literal the server prints, on every read path, and that
/// literal casts back.
///
/// Gated on <c>PGNIMBUS_TEST_CONN</c> like <see cref="QueryEngineCompositeTests"/>.
/// </summary>
// Every test here drops and recreates the same scratch domain and table.
[NotInParallel]
public class QueryEngineBitStringTests
{
    private const string Table = "pgnimbus_bit_scratch";
    private const string Domain = "pgnimbus_bit_scratch_flag";
    private const string Columns = "id, flag, mask, flags, tail, guarded, active";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres available to test bit reads against.");
        }
    }

    private static NpgsqlDataSource CreateDataSource() => NpgsqlDataSource.Create(ConnectionString!);

    // Seeded on a throwaway data source, so the engine's own data source loads
    // pg_type after the domain exists (see QueryEngineCompositeTests.SeedAsync).
    private static async Task SeedAsync()
    {
        await ExecAsync($$"""
            DROP TABLE IF EXISTS {{Table}};
            DROP DOMAIN IF EXISTS {{Domain}};
            CREATE DOMAIN {{Domain}} AS bit(1);
            CREATE TABLE {{Table}} (
                id int PRIMARY KEY,
                flag bit(1),
                mask bit(8),
                flags bit(1)[],
                tail varbit,
                guarded {{Domain}},
                active boolean);
            INSERT INTO {{Table}} VALUES (1, B'1', B'10110001', '{1,0}', B'101', B'0', true);
            """);
    }

    private static Task DropAsync() => ExecAsync($"DROP TABLE IF EXISTS {Table}; DROP DOMAIN IF EXISTS {Domain};");

    private static async Task ExecAsync(string sql)
    {
        await using var dataSource = CreateDataSource();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<object?[]>> DrainAsync(StatementResult result)
    {
        if (result is not ResultSet resultSet)
        {
            var detail = result is QueryError error ? $": {error.Message}" : string.Empty;
            throw new InvalidOperationException($"Expected a ResultSet but got {result.GetType().Name}{detail}");
        }

        var rows = new List<object?[]>();
        await foreach (var batch in resultSet.Batches)
        {
            rows.AddRange(batch.Rows);
        }

        return rows;
    }

    // The row as psql prints it: bit(1), bit(8), bit(1)[], varbit and a domain
    // over bit(1) as their literals, and the boolean left a bool.
    private static async Task AssertLiterals(object?[] row)
    {
        await Assert.That(row[1]).IsEqualTo("1");
        await Assert.That(row[2]).IsEqualTo("10110001");
        await Assert.That(row[3]).IsEqualTo("{1,0}");
        await Assert.That(row[4]).IsEqualTo("101");
        await Assert.That(row[5]).IsEqualTo("0");
        await Assert.That(row[6] is true).IsTrue();
    }

    [Test]
    public async Task Every_bit_type_reads_as_its_literal_and_boolean_stays_a_bool()
    {
        SkipIfNoConnection();
        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var result = await new QueryEngine(dataSource).ExecuteAsync($"SELECT {Columns} FROM {Table}", CancellationToken.None);
            var columns = ((ResultSet)result).Columns;
            var rows = await DrainAsync(result);

            await Assert.That(rows).Count().IsEqualTo(1);
            await AssertLiterals(rows[0]);
            // The column says what the cells hold, so the edit path never tries a bool conversion.
            await Assert.That(columns[1].ClrType).IsEqualTo(typeof(string));
            await Assert.That(columns[6].ClrType).IsEqualTo(typeof(bool));
        }
        finally
        {
            await DropAsync();
        }
    }

    [Test]
    public async Task A_transaction_and_a_script_read_the_same_literals()
    {
        SkipIfNoConnection();
        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);

            await engine.BeginTransactionAsync(CancellationToken.None);
            var inTransaction = (MaterializedResultSet)await engine.ExecuteAsync($"SELECT {Columns} FROM {Table}", CancellationToken.None);
            await engine.RollbackAsync(CancellationToken.None);
            await AssertLiterals(inTransaction.Rows[0]);

            // The second statement of a script is never retried, but it can return
            // rows, so it is still described and its bit columns still come back as text.
            var results = new List<StatementResult>();
            await foreach (var result in engine.ExecuteScriptAsync(["SELECT 1", $"SELECT {Columns} FROM {Table}"], null))
            {
                results.Add(result);
            }

            await AssertLiterals(((MaterializedResultSet)results[1]).Rows[0]);
        }
        finally
        {
            await DropAsync();
        }
    }

    [Test]
    public async Task The_value_a_bit_cell_shows_saves_back_through_its_cast()
    {
        SkipIfNoConnection();
        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);
            var row = (await DrainAsync(await engine.ExecuteAsync($"SELECT {Columns} FROM {Table}", CancellationToken.None)))[0];

            // What safe mode sends: the edited text cast to the declared type, with
            // the row as read as the snapshot the commit re-reads and compares. The
            // array goes back exactly as read; before the fix it was a bool[] and
            // the flag a bool, and neither has a cast to bit.
            var set = new PendingChangeSet("public", Table, ["id"]);
            set.StageEdit([row[0]], "flag", "0", castType: "bit(1)", original: new RowSnapshot(Columns.Split(", "), row));
            set.StageEdit([row[0]], "flags", row[3], castType: "bit(1)[]");
            set.StageEdit([row[0]], "guarded", row[5], castType: Domain);
            await engine.ApplyBatchAsync(set.BuildStatements(), set.BuildRowCheck(), CancellationToken.None);

            var saved = (await DrainAsync(await engine.ExecuteAsync($"SELECT {Columns} FROM {Table}", CancellationToken.None)))[0];
            await Assert.That(saved[1]).IsEqualTo("0");
            await Assert.That(saved[3]).IsEqualTo("{1,0}");
            await Assert.That(saved[5]).IsEqualTo("0");
        }
        finally
        {
            await DropAsync();
        }
    }
}
