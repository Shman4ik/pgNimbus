using System.Net.Sockets;
using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// The classifier two very different recovery paths hang off: the query
/// engine's silent single retry, and the notification listener's re-LISTEN.
/// A false positive here re-runs work that already happened; a false negative
/// leaves a monitor dead with the connection it lost.
/// </summary>
public class ConnectionFailureTests
{
    private static PostgresException Postgres(string sqlState) =>
        new("terminating connection", "FATAL", "FATAL", sqlState);

    [Test]
    public async Task Class08IsALoss()
    {
        await Assert.That(ConnectionFailure.IsLoss(Postgres("08006"))).IsTrue();
    }

    [Test]
    public async Task AdminShutdownIsALoss()
    {
        await Assert.That(ConnectionFailure.IsLoss(Postgres(PostgresErrorCodes.AdminShutdown))).IsTrue();
    }

    [Test]
    public async Task OrdinaryStatementErrorIsNotALoss()
    {
        // A syntax error arrived over a perfectly live connection: retrying it
        // would just fail again, and hide the real message behind the retry.
        await Assert.That(ConnectionFailure.IsLoss(Postgres(PostgresErrorCodes.SyntaxError))).IsFalse();
    }

    [Test]
    public async Task SocketLevelFailureIsALoss()
    {
        var ex = new NpgsqlException("Exception while reading from stream", new SocketException(10054));

        await Assert.That(ConnectionFailure.IsLoss(ex)).IsTrue();
    }

    [Test]
    public async Task TimeoutIsNotALoss()
    {
        // Npgsql wraps command timeouts and pool exhaustion in the same
        // exception type; re-running those could double-apply a write that is
        // still executing server-side.
        var ex = new NpgsqlException("Timeout", new TimeoutException());

        await Assert.That(ConnectionFailure.IsLoss(ex)).IsFalse();
    }

    [Test]
    public async Task CancellationIsNotALoss()
    {
        await Assert.That(ConnectionFailure.IsLoss(new OperationCanceledException())).IsFalse();
    }
}
