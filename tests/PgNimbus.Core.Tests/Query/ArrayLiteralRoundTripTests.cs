using Npgsql;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Array cells against a real server: the literal <see cref="PgValueSyntax.FormatArray"/>
/// writes is what the grid shows and what an inline edit pre-fills, so casting it
/// back must give the value that was read. Two shapes did not: a 2-D array, which
/// Npgsql reads as a CLR <c>T[,]</c> and the formatter wrote flat
/// (<c>{{1,2},{3,4}}</c> as <c>{1,2,3,4}</c>, which casts back as a 1-D array),
/// and a <c>bytea[]</c>, read as <c>byte[][]</c> and written as nested numbers
/// (<c>{{222,173,190,239}}</c>, which casts back as a 2-D bytea[] of the digit
/// strings). Both saved without an error.
///
/// Gated on <c>PGNIMBUS_TEST_CONN</c> like <see cref="QueryEngineBitStringTests"/>.
/// </summary>
// Every test here drops and recreates the same scratch table.
[NotInParallel]
public class ArrayLiteralRoundTripTests
{
    private const string Table = "pgnimbus_array_scratch";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    // Column, declared type, and the value it holds, as SQL.
    private static readonly (string Column, string Type, string Value)[] Cases =
    [
        ("grid", "integer[]", "'{{1,2},{3,4}}'"),
        ("cube", "integer[]", "'{{{1},{2}},{{3},{4}}}'"),
        ("words", "text[]", """'{{"a b",NULL},{"c\\d",""},{"x,y","say \"hi\""}}'"""),
        ("blobs", "bytea[]", """ARRAY['\xdeadbeef'::bytea, NULL, ''::bytea]"""),
        ("blob_grid", "bytea[]", """ARRAY[ARRAY['\xdead'::bytea], ARRAY['\xbeef'::bytea]]"""),
        // A 1-D array, as before. NULL elements are in the text and bytea cases:
        // Npgsql reads an int[] holding a NULL as an error (its default array
        // nullability), which the engine shows as <unreadable integer[]>.
        ("flat", "integer[]", "'{1,2,3}'"),
    ];

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres available to read arrays back.");
        }
    }

    private static NpgsqlDataSource CreateDataSource() => NpgsqlDataSource.Create(ConnectionString!);

    private static async Task ExecAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?[]> ReadRowAsync(QueryEngine engine, string sql)
    {
        if (await engine.ExecuteAsync(sql, CancellationToken.None) is not ResultSet resultSet)
        {
            throw new InvalidOperationException($"Expected a ResultSet for: {sql}");
        }

        var rows = new List<object?[]>();
        await foreach (var batch in resultSet.Batches)
        {
            rows.AddRange(batch.Rows);
        }

        return rows.Single();
    }

    // The CLR shape Npgsql read: rank and the length of each dimension.
    private static string Shape(object? value) => value is Array array
        ? string.Join('x', Enumerable.Range(0, array.Rank).Select(array.GetLength))
        : value?.GetType().Name ?? "null";

    [Test]
    public async Task The_literal_of_an_array_cell_casts_back_to_the_same_value()
    {
        SkipIfNoConnection();
        await using var dataSource = CreateDataSource();
        var engine = new QueryEngine(dataSource);

        foreach (var (column, type, value) in Cases)
        {
            var read = (await ReadRowAsync(engine, $"SELECT {value}::{type} AS {column}"))[0];
            await Assert.That(read is Array).IsTrue().Because($"{column} is read as an array");
            var literal = PgValueSyntax.FormatArray((Array)read!);

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"""
                SELECT CAST(@literal AS {type}) IS NOT DISTINCT FROM {value}::{type},
                       array_dims(CAST(@literal AS {type})),
                       array_dims({value}::{type}),
                       ({value}::{type})::text
                """,
                connection);
            command.Parameters.AddWithValue("literal", literal);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();

            await Assert.That(reader.GetFieldValue<string?>(1)).IsEqualTo(reader.GetFieldValue<string?>(2))
                .Because($"{column}: {literal} keeps the dimensions");
            await Assert.That(reader.GetBoolean(0)).IsTrue().Because($"{column}: {literal} is the value read");

            // And it is the text psql shows, but for the case of bytea's hex digits:
            // the grid writes them upper case, as it writes a bytea cell.
            var server = reader.GetString(3);
            await Assert.That(type == "bytea[]" ? literal.ToLowerInvariant().Replace("null", "NULL") : literal)
                .IsEqualTo(server);
        }
    }

    [Test]
    public async Task An_edit_that_keeps_the_shown_text_saves_the_value_unchanged()
    {
        SkipIfNoConnection();
        await using var dataSource = CreateDataSource();
        var columns = Cases.Select(c => c.Column).ToArray();
        await ExecAsync(dataSource, $"""
            DROP TABLE IF EXISTS {Table};
            CREATE TABLE {Table} (id int PRIMARY KEY, {string.Join(", ", Cases.Select(c => $"{c.Column} {c.Type}"))});
            INSERT INTO {Table} VALUES (1, {string.Join(", ", Cases.Select(c => c.Value))});
            """);
        try
        {
            var engine = new QueryEngine(dataSource);
            var select = $"SELECT id, {string.Join(", ", columns)} FROM {Table}";
            var row = await ReadRowAsync(engine, select);

            // What an inline edit sends: the cell's text, cast to the declared
            // type; safe mode also re-reads the row and compares it with the
            // snapshot, 2-D arrays and bytea[] included.
            var set = new PendingChangeSet("public", Table, ["id"]);
            var snapshot = new RowSnapshot(["id", .. columns], row);
            for (var i = 0; i < Cases.Length; i++)
            {
                set.StageEdit([row[0]], Cases[i].Column, PgValueSyntax.FormatArray((Array)row[i + 1]!), castType: Cases[i].Type, original: snapshot);
            }

            await engine.ApplyBatchAsync(set.BuildStatements(), set.BuildRowCheck(), CancellationToken.None);

            var saved = await ReadRowAsync(engine, select);
            for (var i = 0; i < Cases.Length; i++)
            {
                await Assert.That(Shape(saved[i + 1])).IsEqualTo(Shape(row[i + 1])).Because(Cases[i].Column);
                await Assert.That(PgValueSyntax.FormatArray((Array)saved[i + 1]!))
                    .IsEqualTo(PgValueSyntax.FormatArray((Array)row[i + 1]!)).Because(Cases[i].Column);
            }

            var unchanged = string.Join(" AND ", Cases.Select(c => $"{c.Column} IS NOT DISTINCT FROM {c.Value}::{c.Type}"));
            var check = await ReadRowAsync(engine, $"SELECT {unchanged} FROM {Table}");
            await Assert.That(check[0] is true).IsTrue();
        }
        finally
        {
            await ExecAsync(dataSource, $"DROP TABLE IF EXISTS {Table}");
        }
    }
}
