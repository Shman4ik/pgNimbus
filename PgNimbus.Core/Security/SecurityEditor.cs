using Npgsql;

namespace PgNimbus.Core.Security;

/// <summary>
/// Runs a generated role/privilege script.
///
/// Almost nothing in the Roles &amp; Permissions window needs this: privilege
/// changes are handed to the SQL editor as a script, because a GRANT the user
/// can read, edit and run beats one applied behind their back. The exception is
/// any statement carrying a <c>PASSWORD</c> literal. Postgres has no parameter
/// form for it, so the secret has to be interpolated into statement text — and
/// routing that through a query tab would file it in the on-disk query history
/// and put it on screen. Those statements run here instead: composed, executed,
/// and dropped, never shown and never persisted.
///
/// Everything runs inside one transaction. DDL is transactional in Postgres, so
/// a three-statement drop-role recipe that fails on its second statement leaves
/// the role exactly as it was rather than half-dismantled.
/// </summary>
public sealed class SecurityEditor(NpgsqlDataSource dataSource)
{
    private readonly NpgsqlDataSource _dataSource = dataSource;

    /// <summary>
    /// Executes <paramref name="script"/> — one or more statements — atomically.
    /// The server's own error is allowed to propagate: a pre-flight permission
    /// check would only guess, and it would guess wrong on managed Postgres
    /// where the connected role is not a superuser but can still create roles.
    /// </summary>
    public async Task ExecuteScriptAsync(string script, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand(script, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}
