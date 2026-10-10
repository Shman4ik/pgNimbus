using PgNimbus.Core.Backup;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// pg_dump and pg_restore have to connect where the window is connected, with
/// the same TLS checks, and the password must reach them through the
/// environment only: the connection string is shown in the window and is the
/// program's command line, which every local user can read.
/// </summary>
public class PgToolConnectionTests
{
    private static ConnectionProfile Profile(Core.Connections.SslMode mode = Core.Connections.SslMode.Require, string? rootCertificate = null) =>
        new(Guid.NewGuid(), "prod", "db.example.com", 5433, "shop", "app", mode, RootCertificatePath: rootCertificate);

    [Test]
    public async Task The_connection_string_spells_out_the_profile_and_never_the_password()
    {
        var connection = PgToolConnection.From(Profile(), "s3cret pass", tunnelEndpoint: null);

        var text = connection.ToConnectionString();

        await Assert.That(text).IsEqualTo("host=db.example.com port=5433 dbname=shop user=app sslmode=require gssencmode=disable connect_timeout=10");
        await Assert.That(text).DoesNotContain("s3cret");
        await Assert.That(connection.Password).IsEqualTo("s3cret pass");
    }

    [Test]
    public async Task Through_a_tunnel_the_socket_goes_to_the_local_port_and_TLS_checks_the_real_host()
    {
        var connection = PgToolConnection.From(Profile(Core.Connections.SslMode.VerifyFull, "/certs/ca.pem"), "pw", ("127.0.0.1", 50123));

        await Assert.That(connection.IsTunnelled).IsTrue();
        await Assert.That(connection.ToConnectionString()).IsEqualTo(
            "host=db.example.com hostaddr=127.0.0.1 port=50123 dbname=shop user=app sslmode=verify-full sslrootcert=/certs/ca.pem gssencmode=disable connect_timeout=10");
    }

    [Test]
    public async Task A_verifying_mode_without_its_own_CA_uses_the_exported_trust_store()
    {
        var connection = PgToolConnection.From(Profile(Core.Connections.SslMode.VerifyFull), null, null);

        await Assert.That(connection.NeedsTrustedRoots).IsTrue();
        await Assert.That(connection.ToConnectionString("/data/trusted-roots.pem")).Contains("sslrootcert=/data/trusted-roots.pem");

        // Require checks nothing, so it names no CA even when the profile has one.
        var require = PgToolConnection.From(Profile(Core.Connections.SslMode.Require, "/certs/ca.pem"), null, null);
        await Assert.That(require.NeedsTrustedRoots).IsFalse();
        await Assert.That(require.ToConnectionString("/data/trusted-roots.pem")).DoesNotContain("sslrootcert");
    }

    [Test]
    [Arguments("shop", "shop")]
    [Arguments("my shop", "'my shop'")]
    [Arguments("it's", @"'it\'s'")]
    [Arguments(@"C:\Program Files\certs\ca.pem", @"'C:\\Program Files\\certs\\ca.pem'")]
    [Arguments("a=b", "'a=b'")]
    [Arguments("", "''")]
    public async Task Values_are_quoted_the_way_libpq_reads_them(string value, string expected)
    {
        await Assert.That(PgToolConnection.QuoteValue(value)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_window_opened_from_a_connection_string_connects_the_same_way()
    {
        var connection = PgToolConnection.FromConnectionString(
            "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=pw;SSL Mode=VerifyFull;Root Certificate=/ca.pem");

        await Assert.That(connection.Password).IsEqualTo("pw");
        await Assert.That(connection.ToConnectionString()).IsEqualTo(
            "host=localhost port=5432 dbname=postgres user=postgres sslmode=verify-full sslrootcert=/ca.pem gssencmode=disable connect_timeout=10");
        await Assert.That(connection.ForDatabase("other").ToConnectionString()).Contains("dbname=other");
    }
}
