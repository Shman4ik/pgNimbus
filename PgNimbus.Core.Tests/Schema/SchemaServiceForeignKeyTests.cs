using Npgsql;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

/// <summary>
/// <see cref="SchemaService.GetForeignKeysAsync"/> against a real server: two
/// FKs between one pair of tables come back as two constraints, each with its
/// name — what completion's JOIN … ON suggestions label their rows with.
/// Gated on <c>PGNIMBUS_TEST_CONN</c>.
/// </summary>
[NotInParallel]
public class SchemaServiceForeignKeyTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task Each_constraint_comes_back_with_its_name_and_column_order()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to read foreign keys from.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using (var seed = dataSource.CreateCommand(
            """
            DROP SCHEMA IF EXISTS pgn_fk CASCADE;
            CREATE SCHEMA pgn_fk;
            CREATE TABLE pgn_fk.users (id int PRIMARY KEY);
            CREATE TABLE pgn_fk.lines (order_id int, line_no int, PRIMARY KEY (order_id, line_no));
            CREATE TABLE pgn_fk.orders (
                id int PRIMARY KEY,
                buyer_id int CONSTRAINT orders_buyer_fkey REFERENCES pgn_fk.users (id),
                seller_id int CONSTRAINT orders_seller_fkey REFERENCES pgn_fk.users (id),
                o_id int, l_no int,
                CONSTRAINT orders_line_fkey FOREIGN KEY (l_no, o_id) REFERENCES pgn_fk.lines (line_no, order_id));
            """))
        {
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            var fks = (await new SchemaService(dataSource).GetForeignKeysAsync(CancellationToken.None))
                .Where(f => f.FromSchema == "pgn_fk")
                .ToList();

            await Assert.That(fks.Select(f => f.ConstraintName)).IsEquivalentTo(new[] { "orders_buyer_fkey", "orders_line_fkey", "orders_seller_fkey" });
            var composite = fks.Single(f => f.ConstraintName == "orders_line_fkey");
            await Assert.That(string.Join(",", composite.FromColumns)).IsEqualTo("l_no,o_id");
            await Assert.That(string.Join(",", composite.ToColumns)).IsEqualTo("line_no,order_id");
        }
        finally
        {
            await using var drop = dataSource.CreateCommand("DROP SCHEMA IF EXISTS pgn_fk CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
