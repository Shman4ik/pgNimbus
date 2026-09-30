using Npgsql;
using PgNimbus.Core.Query;
using PgNimbus.Core.Security;

namespace PgNimbus.Core.Tests.Security;

/// <summary>
/// The live half of <see cref="ScramSha256VerifierTests"/>: a role created
/// through the real <see cref="RoleScriptBuilder"/> and <see cref="SecurityEditor"/>
/// path, whose statement carries only the client-computed SCRAM-SHA-256
/// verifier, must authenticate a fresh connection that presents the cleartext.
/// That is the whole claim of the change (security audit 2026-09, finding 7):
/// the server stores what it was given as-is and the login still works.
///
/// <para>Gated on <c>PGNIMBUS_TEST_CONN</c> and skips cleanly without it. The
/// server behind it must authenticate host connections with
/// <c>scram-sha-256</c> (the <c>postgres:17</c> image's default) and the test
/// connection must be a superuser, since it reads <c>pg_authid</c>. Roles are
/// cluster-wide, so the names carry a random suffix and every role is dropped
/// in a <c>finally</c>.</para>
/// </summary>
[NotInParallel]
public class ScramPasswordServerTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to create a SCRAM role on.");
        }
    }

    private static string RoleName() => "pgnimbus_scram_" + Guid.NewGuid().ToString("N")[..12];

    private static RoleDefinition Definition(string name) =>
        new(name, CanLogin: true, IsSuperuser: false, Inherit: true, CanCreateDb: false, CanCreateRole: false,
            CanReplicate: false, BypassRls: false, ConnectionLimit: null, ValidUntil: null, MemberOf: [], Comment: null);

    /// <summary>
    /// The same host, port and database as the admin connection, as the new
    /// role with the cleartext, and unpooled so every open is a real SCRAM
    /// exchange rather than a pooled socket handed back.
    /// </summary>
    private static string AsRole(string role, string password) =>
        new NpgsqlConnectionStringBuilder(ConnectionString!)
        {
            Username = role,
            Password = password,
            Pooling = false,
        }.ConnectionString;

    private static async Task<string?> StoredSecretAsync(NpgsqlDataSource dataSource, string role)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT rolpassword FROM pg_authid WHERE rolname = @name", connection);
        command.Parameters.AddWithValue("name", role);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<string> WhoAmIAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_user", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DropRoleAsync(NpgsqlDataSource dataSource, string role)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP ROLE IF EXISTS {SqlIdentifier.QuoteIfNeeded(role)}", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task A_role_created_with_a_client_side_verifier_logs_in_with_the_cleartext()
    {
        SkipIfNoConnection();

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        var editor = new SecurityEditor(dataSource);
        var role = RoleName();
        const string password = "correct horse battery staple";

        try
        {
            var script = RoleScriptBuilder.Create(Definition(role), password);
            await Assert.That(script).DoesNotContain(password);
            await editor.ExecuteScriptAsync(script, CancellationToken.None);

            // What the server stored is what was sent: a SCRAM secret, not a
            // hash of the literal it received.
            var stored = await StoredSecretAsync(dataSource, role);
            await Assert.That(stored).StartsWith("SCRAM-SHA-256$4096:");
            await Assert.That(script).Contains(stored!);

            await Assert.That(await WhoAmIAsync(AsRole(role, password))).IsEqualTo(role);

            // And the check is real: the wrong password is refused with
            // 28P01, which is what proves the connection went through SCRAM
            // rather than a trust line in pg_hba.conf.
            var refused = await Assert.That(async () => await WhoAmIAsync(AsRole(role, "wrong")))
                .Throws<PostgresException>();
            await Assert.That(refused!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidPassword);
        }
        finally
        {
            await DropRoleAsync(dataSource, role);
        }
    }

    [Test]
    public async Task Set_password_replaces_the_secret_and_the_new_cleartext_logs_in()
    {
        SkipIfNoConnection();

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        var editor = new SecurityEditor(dataSource);
        var role = RoleName();

        try
        {
            await editor.ExecuteScriptAsync(RoleScriptBuilder.Create(Definition(role), "first"), CancellationToken.None);
            var before = await StoredSecretAsync(dataSource, role);

            // A password with a non-ASCII character that NFKC leaves alone, so
            // the client and the server hash the same UTF-8 whatever the
            // runtime's normalisation support.
            const string password = "zwe\u00EFte Passw\u00F6rt";
            var script = RoleScriptBuilder.SetPassword(role, password);
            await Assert.That(script).DoesNotContain(password);
            await editor.ExecuteScriptAsync(script, CancellationToken.None);

            var after = await StoredSecretAsync(dataSource, role);
            await Assert.That(after).StartsWith("SCRAM-SHA-256$4096:");
            await Assert.That(after).IsNotEqualTo(before);

            await Assert.That(await WhoAmIAsync(AsRole(role, password))).IsEqualTo(role);
            var refused = await Assert.That(async () => await WhoAmIAsync(AsRole(role, "first")))
                .Throws<PostgresException>();
            await Assert.That(refused!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidPassword);
        }
        finally
        {
            await DropRoleAsync(dataSource, role);
        }
    }
}
