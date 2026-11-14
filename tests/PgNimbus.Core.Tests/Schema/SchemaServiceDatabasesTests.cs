using Npgsql;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

/// <summary>
/// The title bar's database list (<see cref="SchemaService.GetDatabasesAsync"/>):
/// databases that accept connections, no templates, sorted, the current one
/// included.
/// </summary>
public sealed class SchemaServiceDatabasesTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task Lists_connectable_databases_sorted_without_templates()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to list databases from.");
        }

        var name = $"pgn_switch_{Guid.NewGuid().ToString("N")[..8]}";
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        var service = new SchemaService(dataSource);
        var current = new NpgsqlConnectionStringBuilder(ConnectionString).Database
            ?? throw new InvalidOperationException("The test connection names no database.");

        await ExecuteAsync(dataSource, $"CREATE DATABASE {name}");
        try
        {
            var listed = await service.GetDatabasesAsync(CancellationToken.None);

            await Assert.That(listed).Contains(current);
            await Assert.That(listed).Contains(name);
            await Assert.That(listed).DoesNotContain("template0");
            await Assert.That(listed).DoesNotContain("template1");
            await Assert.That(listed.SequenceEqual(listed.OrderBy(n => n, StringComparer.Ordinal))).IsTrue();

            // A database that refuses connections is no place to switch to.
            await ExecuteAsync(dataSource, $"ALTER DATABASE {name} WITH ALLOW_CONNECTIONS false");
            await Assert.That(await service.GetDatabasesAsync(CancellationToken.None)).DoesNotContain(name);
        }
        finally
        {
            await ExecuteAsync(dataSource, $"DROP DATABASE IF EXISTS {name}");
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
