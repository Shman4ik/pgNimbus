using Npgsql;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Arrays of a value type that hold a NULL element (2026-10). Npgsql's default
/// <see cref="ArrayNullabilityMode.Never"/> reads an <c>integer[]</c> as
/// <c>int[]</c> and throws for <c>{1,NULL,3}</c>, and the engine's per-cell guard
/// showed <c>&lt;unreadable integer[]&gt;</c>: no value, and no edit. Every data
/// source the app builds now reads such arrays with nullable elements
/// (<see cref="ConnectionProfile.ArrayNullability"/>), so the cell shows the
/// literal the server prints, and that literal saves back unchanged through
/// safe mode's batch, row check included. The shapes themselves (2-D, bytea[])
/// are <see cref="ArrayLiteralRoundTripTests"/>'; these are the NULL elements.
///
/// The offline half checks what the connection strings ask for; the rest is
/// gated on <c>PGNIMBUS_TEST_CONN</c> like <see cref="QueryEngineBitStringTests"/>.
/// </summary>
// Every live test here drops and recreates the same scratch table.
[NotInParallel]
public class QueryEngineNullableArrayTests
{
    private const string Table = "pgnimbus_nullable_array_scratch";

    // Row 1 holds a NULL in every array; row 2 the same shapes without one.
    private static readonly string[] Columns = ["id", "ints", "grid", "days", "ids", "amounts", "spans"];

    private static readonly (string Column, string CastType)[] Arrays =
    [
        ("ints", "integer[]"),
        ("grid", "integer[]"),
        ("days", "date[]"),
        ("ids", "uuid[]"),
        ("amounts", "numeric[]"),
        ("spans", "int4range[]"),
    ];

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task Every_connection_string_the_app_builds_reads_value_type_arrays_as_nullable()
    {
        var profile = new ConnectionProfile(Guid.NewGuid(), "p", "db.example.com", 5432, "app", "me");
        var fromProfile = new NpgsqlConnectionStringBuilder(profile.BuildConnectionString("pw"));
        await Assert.That(fromProfile.ArrayNullabilityMode).IsEqualTo(ArrayNullabilityMode.Always);

        // PGNIMBUS_CONN: the same setting, beside the standard-strings option,
        // and over whatever the string itself asked for.
        var outside = new NpgsqlConnectionStringBuilder(
            ConnectionProfile.ForAppSession("Host=h;Database=d;Username=u;Array Nullability Mode=Never"));
        await Assert.That(outside.ArrayNullabilityMode).IsEqualTo(ArrayNullabilityMode.Always);
        await Assert.That(outside.Options).IsEqualTo(ConnectionProfile.StandardStringsSessionOption);
    }

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres available to read nullable arrays against.");
        }
    }

    /// <summary>The pool the connection dialog hands a window: a profile's.</summary>
    private static NpgsqlDataSource CreateDataSource()
    {
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString!);
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "arrays", csb.Host ?? "localhost", csb.Port, csb.Database ?? "postgres", csb.Username ?? "postgres",
            Core.Connections.SslMode.Prefer);
        return profile.CreateDataSource(csb.Password);
    }

    private static async Task SeedAsync()
    {
        await ExecAsync($$$"""
            DROP TABLE IF EXISTS {{{Table}}};
            CREATE TABLE {{{Table}}} (
                id int PRIMARY KEY,
                ints integer[],
                grid integer[],
                days date[],
                ids uuid[],
                amounts numeric[],
                spans int4range[]);
            INSERT INTO {{{Table}}} VALUES
                (1, '{1,NULL,3}', '{{1,NULL},{3,4}}', '{2026-01-01,NULL}',
                    '{00000000-0000-0000-0000-000000000001,NULL}', '{1.50,NULL}', '{"[1,3)",NULL}'),
                (2, '{1,2,3}', '{{1,2},{3,4}}', '{2026-01-01}',
                    '{00000000-0000-0000-0000-000000000001}', '{1.50}', '{"[1,3)"}');
            """);
    }

    private static Task DropAsync() => ExecAsync($"DROP TABLE IF EXISTS {Table};");

    private static async Task ExecAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static string SelectAll => $"SELECT {string.Join(", ", Columns)} FROM {Table} ORDER BY id";

    // What psql prints for each array of a row, the text the grid has to show,
    // one per line so a failure names the column.
    private static async Task<string> ServerTextAsync(int id)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        var select = string.Join(", ", Arrays.Select(a => $"{a.Column}::text"));
        await using var command = dataSource.CreateCommand($"SELECT {select} FROM {Table} WHERE id = {id}");
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return string.Join("\n", Arrays.Select((a, i) => $"{a.Column} {reader.GetString(i)}"));
    }

    private static async Task<List<object?[]>> DrainAsync(StatementResult result)
    {
        switch (result)
        {
            case MaterializedResultSet materialized:
                return [.. materialized.Rows];
            case ResultSet resultSet:
                var rows = new List<object?[]>();
                await foreach (var batch in resultSet.Batches)
                {
                    rows.AddRange(batch.Rows);
                }

                return rows;
            default:
                var detail = result is QueryError error ? $": {error.Message}" : string.Empty;
                throw new InvalidOperationException($"Expected rows but got {result.GetType().Name}{detail}");
        }
    }

    // Each array cell of a row as the grid writes it (PgValueSyntax.FormatArray
    // is what CellText.Preview and the inline editor's pre-fill come down to).
    private static string[] Literals(object?[] row) =>
        [.. Arrays.Select(a => PgValueSyntax.FormatArray((Array)row[Array.IndexOf(Columns, a.Column)]!))];

    // The same, in ServerTextAsync's shape.
    private static string Shown(object?[] row) =>
        string.Join("\n", Literals(row).Select((literal, i) => $"{Arrays[i].Column} {literal}"));

    [Test]
    public async Task An_array_holding_a_null_reads_and_shows_the_literal_the_server_prints()
    {
        SkipIfNoConnection();
        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);
            var result = await engine.ExecuteAsync(SelectAll, CancellationToken.None);
            var columns = ((ResultSet)result).Columns;
            var rows = await DrainAsync(result);

            await Assert.That(rows.SelectMany(r => r).Any(QueryEngine.IsUnreadableCell)).IsFalse();
            await Assert.That(rows[0][1]).IsTypeOf<int?[]>();
            await Assert.That((int?[])rows[0][1]!).IsEquivalentTo(new int?[] { 1, null, 3 });
            await Assert.That(rows[0][2]).IsTypeOf<int?[,]>();

            // One CLR shape per column whatever the data: row 2 has no NULL, and
            // is still int?[] (ArrayNullabilityMode.Always, not PerInstance).
            await Assert.That(rows[1][1]).IsTypeOf<int?[]>();
            await Assert.That(columns[1].ClrType).IsEqualTo(typeof(Array));

            await Assert.That(Shown(rows[0])).IsEqualTo(await ServerTextAsync(1));
            await Assert.That(Shown(rows[1])).IsEqualTo(await ServerTextAsync(2));
            await Assert.That(Literals(rows[0])[1]).IsEqualTo("{{1,NULL},{3,4}}");
        }
        finally
        {
            await DropAsync();
        }
    }

    [Test]
    public async Task A_transaction_and_a_script_read_the_same_arrays()
    {
        SkipIfNoConnection();
        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);
            var expected = await ServerTextAsync(1);

            await engine.BeginTransactionAsync(CancellationToken.None);
            var inTransaction = await DrainAsync(await engine.ExecuteAsync(SelectAll, CancellationToken.None));
            await engine.RollbackAsync(CancellationToken.None);
            await Assert.That(Shown(inTransaction[0])).IsEqualTo(expected);

            var results = new List<StatementResult>();
            await foreach (var result in engine.ExecuteScriptAsync(["SELECT 1", SelectAll], null))
            {
                results.Add(result);
            }

            await Assert.That(Shown((await DrainAsync(results[1]))[0])).IsEqualTo(expected);
        }
        finally
        {
            await DropAsync();
        }
    }

    [Test]
    public async Task The_literal_an_array_cell_shows_saves_back_unchanged_through_safe_mode()
    {
        SkipIfNoConnection();
        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);
            var before = await ServerTextAsync(1);
            var row = (await DrainAsync(await engine.ExecuteAsync(SelectAll, CancellationToken.None)))[0];

            // What an unchanged edit of each cell sends: the text the cell shows,
            // cast to the declared type, with the row as read as the snapshot the
            // commit re-reads FOR UPDATE and compares. A false conflict here would
            // throw StagedChangesConflictException.
            var set = new PendingChangeSet("public", Table, ["id"]);
            var snapshot = new RowSnapshot(Columns, row);
            var literals = Literals(row);
            for (var i = 0; i < Arrays.Length; i++)
            {
                set.StageEdit([row[0]], Arrays[i].Column, literals[i], castType: Arrays[i].CastType, original: i == 0 ? snapshot : null);
            }

            await engine.ApplyBatchAsync(set.BuildStatements(), set.BuildRowCheck(), CancellationToken.None);

            var saved = (await DrainAsync(await engine.ExecuteAsync(SelectAll, CancellationToken.None)))[0];
            await Assert.That(await ServerTextAsync(1)).IsEqualTo(before);
            for (var i = 1; i < Columns.Length; i++)
            {
                await Assert.That(CellValueComparer.Compare(row[i], saved[i])).IsEqualTo(CellComparison.Equal)
                    .Because(Columns[i]);
            }

            // Still two dimensions: {1,NULL,3,4} would have been [1:4].
            await using var dims = dataSource.CreateCommand($"SELECT array_dims(grid) FROM {Table} WHERE id = 1");
            await Assert.That(await dims.ExecuteScalarAsync()).IsEqualTo("[1:2][1:2]");
        }
        finally
        {
            await DropAsync();
        }
    }

    [Test]
    public async Task A_null_element_set_elsewhere_is_a_conflict()
    {
        SkipIfNoConnection();
        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);
            var row = (await DrainAsync(await engine.ExecuteAsync(SelectAll, CancellationToken.None)))[1];

            var set = new PendingChangeSet("public", Table, ["id"]);
            set.StageEdit([row[0]], "ints", "{7,8,9}", castType: "integer[]", original: new RowSnapshot(Columns, row));

            // Another session puts a NULL into the array the user loaded without one.
            await ExecAsync($"UPDATE {Table} SET ints = '{{1,NULL,3}}' WHERE id = 2");

            var conflict = await Assert.That(() => engine.ApplyBatchAsync(set.BuildStatements(), set.BuildRowCheck(), CancellationToken.None))
                .Throws<StagedChangesConflictException>();
            var column = conflict!.Conflicts.Single().Columns.Single(c => c.Column == "ints");
            await Assert.That(column.ChangedElsewhere).IsTrue();
            await Assert.That(CellDisplay.Format(column.Current)).IsEqualTo("'{1,NULL,3}'");
        }
        finally
        {
            await DropAsync();
        }
    }

    [Test]
    public async Task Filter_by_an_array_cell_matches_its_row()
    {
        SkipIfNoConnection();
        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);
            var rows = await DrainAsync(await engine.ExecuteAsync(SelectAll, CancellationToken.None));

            foreach (var (column, castType) in new[] { ("ints", "integer[]"), ("grid", "integer[]") })
            {
                var index = Array.IndexOf(Columns, column);
                var filter = new RowFilter(column, FilterOperator.Contains, RowFilterSql.ValueText(rows[0][index]!, castType));
                var predicate = RowFilterSql.ToPredicate(filter, ColumnValueEditor.Array, castType);

                await using var command = dataSource.CreateCommand($"SELECT id FROM {Table} WHERE {predicate} ORDER BY id");
                var ids = new List<int>();
                await using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        ids.Add(reader.GetInt32(0));
                    }
                }

                await Assert.That(ids).IsEquivalentTo(new[] { 1 }).Because(predicate);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // The premise, and the guard that still stands behind it: a data source
    // built without the setting (Npgsql's default, Never) cannot read the
    // array, and the engine shows a placeholder instead of failing the result.
    [Test]
    public async Task Without_the_setting_the_cell_is_a_placeholder_not_a_failed_result()
    {
        SkipIfNoConnection();
        await SeedAsync();
        await using var plain = NpgsqlDataSource.Create(ConnectionString!);
        try
        {
            var rows = await DrainAsync(await new QueryEngine(plain).ExecuteAsync(SelectAll, CancellationToken.None));
            await Assert.That(rows[0][1]).IsEqualTo(QueryEngine.UnreadableCell("integer[]"));
            await Assert.That(rows[1][1]).IsTypeOf<int[]>();
        }
        finally
        {
            await DropAsync();
        }
    }
}
