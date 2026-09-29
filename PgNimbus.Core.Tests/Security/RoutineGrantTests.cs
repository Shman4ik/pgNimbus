using Npgsql;
using PgNimbus.Core.Security;

namespace PgNimbus.Core.Tests.Security;

/// <summary>
/// Security audit 2026-09, finding 18: the grant script for a function with a
/// <c>DEFAULT</c> argument was a syntax error (the argument list came from
/// <c>pg_get_function_arguments</c>), and a procedure failed under
/// <c>ON FUNCTION</c>. This reads both from a real catalog, builds the script,
/// and runs it. Gated on <c>PGNIMBUS_TEST_CONN</c>; skips without it.
/// </summary>
[NotInParallel]
public sealed class RoutineGrantTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task A_function_with_a_default_and_a_procedure_can_both_be_granted()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to run the generated GRANT against.");
        }

        // Roles are cluster-wide; a name of its own keeps parallel runs apart.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = $"pgnimbus_routine_{suffix}";
        var role = $"pgnimbus_routine_{suffix}";
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        var ct = CancellationToken.None;

        try
        {
            await ExecuteAsync(dataSource, $"""
                CREATE ROLE {role} NOLOGIN;
                CREATE SCHEMA {schema};
                CREATE FUNCTION {schema}.add_to(a integer, b integer DEFAULT 1) RETURNS integer
                    LANGUAGE sql AS 'SELECT a + b';
                CREATE PROCEDURE {schema}.touch(n integer DEFAULT 0)
                    LANGUAGE plpgsql AS $$ BEGIN PERFORM n; END $$;
                -- PUBLIC can execute every new routine; without this the check
                -- below would pass whether the grant ran or not.
                REVOKE EXECUTE ON ALL ROUTINES IN SCHEMA {schema} FROM PUBLIC;
                """);

            var routines = await new PrivilegeService(dataSource).GetSecurablesAsync(SecurableKind.Function, schema, ct);
            await Assert.That(routines.Count).IsEqualTo(2);
            await Assert.That(routines.Any(r => r.Arguments!.Contains("DEFAULT", StringComparison.OrdinalIgnoreCase))).IsFalse();

            foreach (var routine in routines)
            {
                await Assert.That(await CanExecuteAsync(dataSource, role, routine)).IsFalse();
            }

            var script = GrantScriptBuilder.Build(
                [.. routines.Select(r => new PrivilegeChange(r, role, PrivilegeKind.Execute, Grant: true))]);
            await ExecuteAsync(dataSource, script);

            foreach (var routine in routines)
            {
                await Assert.That(await CanExecuteAsync(dataSource, role, routine)).IsTrue();
            }
        }
        finally
        {
            await ExecuteAsync(dataSource, $"""
                DROP SCHEMA IF EXISTS {schema} CASCADE;
                DROP ROLE IF EXISTS {role};
                """);
        }
    }

    private static async Task<bool> CanExecuteAsync(NpgsqlDataSource dataSource, string role, SecurableRef routine)
    {
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var check = new NpgsqlCommand("SELECT has_function_privilege(@role, @oid::oid, 'EXECUTE')", connection);
        check.Parameters.AddWithValue("role", role);
        check.Parameters.AddWithValue("oid", (long)routine.Oid);
        return (bool)(await check.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
