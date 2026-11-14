using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Npgsql;
using PgNimbus.Core.Backup;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Query;
using SslMode = PgNimbus.Core.Connections.SslMode;

namespace PgNimbus.Core.Tests.Connections;

/// <summary>
/// The connection dialog's Advanced block: a client certificate and key
/// (mutual TLS), and per-profile statement and lock timeouts set as startup
/// options. Plus ~/.pgpass, which needs nothing from the profile: a profile
/// with no stored password connects with no password, and Npgsql reads the
/// password file by itself.
/// </summary>
public class AdvancedConnectionSettingsTests
{
    private static ConnectionProfile Profile(
        string? certificate = null,
        string? key = null,
        int? statementTimeout = null,
        int? lockTimeout = null,
        SslMode mode = SslMode.Require,
        bool readOnly = false,
        ConnectionEnvironment environment = ConnectionEnvironment.None) =>
        new(Guid.NewGuid(), "p", "db.example.com", 5432, "app", "me", mode,
            ReadOnly: readOnly,
            Environment: environment,
            ClientCertificatePath: certificate,
            ClientKeyPath: key,
            StatementTimeoutSeconds: statementTimeout,
            LockTimeoutSeconds: lockTimeout);

    private static NpgsqlConnectionStringBuilder Built(ConnectionProfile profile, bool? readOnly = null) =>
        new(profile.BuildConnectionString("pw", readOnly: readOnly));

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-advanced-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    // --- The connection string ----------------------------------------------

    [Test]
    public async Task A_client_certificate_and_key_reach_the_connection_string_in_every_mode()
    {
        foreach (var mode in Enum.GetValues<SslMode>())
        {
            var builder = Built(Profile("/certs/client.crt", "/certs/client.key", mode: mode));
            await Assert.That(builder.SslCertificate).IsEqualTo("/certs/client.crt");
            await Assert.That(builder.SslKey).IsEqualTo("/certs/client.key");
        }
    }

    [Test]
    public async Task No_client_certificate_is_written_without_one()
    {
        // Npgsql then looks for PGSSLCERT and ~/.postgresql/postgresql.crt itself.
        var builder = Built(Profile(certificate: "  ", key: null));
        await Assert.That(builder.SslCertificate).IsNull();
        await Assert.That(builder.SslKey).IsNull();
    }

    [Test]
    public async Task Timeouts_are_startup_options_before_the_standard_strings_one()
    {
        await Assert.That(Built(Profile(statementTimeout: 30, lockTimeout: 5)).Options).IsEqualTo(
            "-c statement_timeout=30s -c lock_timeout=5s -c standard_conforming_strings=on");
        await Assert.That(Built(Profile(lockTimeout: 2)).Options).IsEqualTo(
            "-c lock_timeout=2s -c standard_conforming_strings=on");
        await Assert.That(Built(Profile(statementTimeout: 60, readOnly: true)).Options).IsEqualTo(
            "-c default_transaction_read_only=on -c statement_timeout=60s -c standard_conforming_strings=on");
    }

    [Test]
    public async Task No_timeout_and_a_zero_timeout_write_nothing()
    {
        await Assert.That(Built(Profile()).Options).IsEqualTo(ConnectionProfile.StandardStringsSessionOption);
        await Assert.That(Built(Profile(statementTimeout: 0, lockTimeout: -3)).Options).IsEqualTo(ConnectionProfile.StandardStringsSessionOption);
        await Assert.That(ConnectionProfile.NormalizeTimeout(0)).IsNull();
        await Assert.That(ConnectionProfile.NormalizeTimeout(int.MaxValue)).IsEqualTo(ConnectionProfile.MaxTimeoutSeconds);
    }

    [Test]
    public async Task The_read_only_marker_is_still_found_beside_the_timeouts()
    {
        // BuildMainWindow and the write gate look for the read-only option by Contains.
        var options = Built(Profile(statementTimeout: 10, environment: ConnectionEnvironment.Production)).Options!;
        await Assert.That(options.Contains(ConnectionProfile.ReadOnlySessionOption, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task A_production_windows_writable_pool_keeps_the_timeouts_and_the_certificate()
    {
        var profile = Profile("/certs/client.crt", "/certs/client.key", statementTimeout: 30, lockTimeout: 5, environment: ConnectionEnvironment.Production);
        var writable = Built(profile, readOnly: false);
        await Assert.That(writable.Options).IsEqualTo("-c statement_timeout=30s -c lock_timeout=5s -c standard_conforming_strings=on");
        await Assert.That(writable.SslCertificate).IsEqualTo("/certs/client.crt");
        await Assert.That(writable.SslKey).IsEqualTo("/certs/client.key");
    }

    // --- connections.json ------------------------------------------------------

    [Test]
    public async Task An_old_connections_file_loads_unchanged_and_the_new_fields_round_trip()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "connections.json");
            File.WriteAllText(path, """
                [
                  {
                    "Id": "8f6b3c1e-2f4a-4c55-9d7e-0a1b2c3d4e5f",
                    "Name": "before advanced",
                    "Host": "db.example.com",
                    "Port": 5432,
                    "Database": "app",
                    "Username": "me",
                    "SslMode": 5,
                    "ReadOnly": true,
                    "RootCertificatePath": "/certs/ca.pem",
                    "Environment": 3
                  }
                ]
                """);
            var store = new ConnectionProfileStore(path);
            var old = store.Load().Single();
            await Assert.That(old.Name).IsEqualTo("before advanced");
            await Assert.That(old.SslMode).IsEqualTo(SslMode.VerifyFull);
            await Assert.That(old.ReadOnly).IsTrue();
            await Assert.That(old.Environment).IsEqualTo(ConnectionEnvironment.Production);
            await Assert.That(old.ClientCertificatePath).IsNull();
            await Assert.That(old.ClientKeyPath).IsNull();
            await Assert.That(old.StatementTimeoutSeconds).IsNull();
            await Assert.That(old.LockTimeoutSeconds).IsNull();
            await Assert.That(Built(old).Options).IsEqualTo(ConnectionProfile.SessionOptions(readOnly: true));

            store.Save([old with { ClientCertificatePath = "/certs/client.crt", ClientKeyPath = "/certs/client.key", StatementTimeoutSeconds = 30, LockTimeoutSeconds = 5 }]);
            var saved = store.Load().Single();
            await Assert.That(saved.ClientCertificatePath).IsEqualTo("/certs/client.crt");
            await Assert.That(saved.ClientKeyPath).IsEqualTo("/certs/client.key");
            await Assert.That(saved.StatementTimeoutSeconds).IsEqualTo(30);
            await Assert.That(saved.LockTimeoutSeconds).IsEqualTo(5);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // --- A pasted string ---------------------------------------------------------

    [Test]
    [Arguments("host=db.example.com sslmode=verify-full sslcert=/certs/client.crt sslkey=/certs/client.key")]
    [Arguments("postgres://me@db.example.com/app?sslmode=require&sslcert=%2Fcerts%2Fclient.crt&sslkey=%2Fcerts%2Fclient.key")]
    [Arguments("Host=db.example.com;SSL Certificate=/certs/client.crt;SSL Key=/certs/client.key")]
    [Arguments("jdbc:postgresql://db.example.com/app?sslcert=/certs/client.crt&sslkey=/certs/client.key")]
    [Arguments("PGSSLCERT=/certs/client.crt PGSSLKEY=/certs/client.key psql -h db.example.com app")]
    public async Task A_pasted_client_certificate_and_key_are_kept(string text)
    {
        await Assert.That(ConnectionStringParser.TryParse(text, out var parsed, out var error)).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(parsed.ClientCertificatePath).IsEqualTo("/certs/client.crt");
        await Assert.That(parsed.ClientKeyPath).IsEqualTo("/certs/client.key");
    }

    [Test]
    [Arguments(@"host=db.example.com sslcert=\\fileserver\share\client.crt")]
    [Arguments(@"host=db.example.com sslkey=\\fileserver\share\client.key")]
    [Arguments("host=db.example.com sslcert=//fileserver/share/client.crt")]
    [Arguments("postgres://me@db.example.com/app?sslkey=https%3A%2F%2Fexample.com%2Fclient.key")]
    [Arguments(@"PGSSLKEY=\\fileserver\share\client.key psql -h db.example.com app")]
    public async Task A_pasted_client_certificate_or_key_on_another_machine_is_refused(string text)
    {
        await Assert.That(ConnectionStringParser.TryParse(text, out _, out var error)).IsFalse();
        await Assert.That(error).Contains("another machine");
    }

    [Test]
    public async Task Normalizing_to_npgsql_carries_the_client_certificate()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            ConnectionStringParser.NormalizeToNpgsql("host=db.example.com dbname=app sslcert=/certs/client.crt sslkey=/certs/client.key"));
        await Assert.That(builder.SslCertificate).IsEqualTo("/certs/client.crt");
        await Assert.That(builder.SslKey).IsEqualTo("/certs/client.key");
    }

    // --- pg_dump and pg_restore --------------------------------------------------

    [Test]
    public async Task The_tools_get_the_client_certificate_and_not_the_timeouts()
    {
        var profile = Profile(@"C:\certs\my client.crt", "/certs/client.key", statementTimeout: 30, lockTimeout: 5);
        var text = PgToolConnection.From(profile, "pw", null).ToConnectionString();
        await Assert.That(text).Contains(@"sslcert='C:\\certs\\my client.crt'");
        await Assert.That(text).Contains("sslkey=/certs/client.key");
        await Assert.That(text).DoesNotContain("timeout=30");
        await Assert.That(text).DoesNotContain("lock_timeout");
        await Assert.That(text).DoesNotContain("options");

        var none = PgToolConnection.From(Profile(), "pw", null).ToConnectionString();
        await Assert.That(none).DoesNotContain("sslcert");
        await Assert.That(none).DoesNotContain("sslkey");
    }

    [Test]
    public async Task A_window_from_a_connection_string_passes_its_client_certificate_on()
    {
        var connection = PgToolConnection.FromConnectionString("Host=db;Database=app;SSL Mode=Require;SSL Certificate=/c.crt;SSL Key=/c.key");
        await Assert.That(connection.ClientCertificatePath).IsEqualTo("/c.crt");
        await Assert.That(connection.ClientKeyPath).IsEqualTo("/c.key");
    }

    [Test]
    public async Task The_tools_keep_the_client_certificate_and_password_file_variables()
    {
        var environment = new Dictionary<string, string?>
        {
            ["PGSSLCERT"] = "/c.crt",
            ["PGSSLKEY"] = "/c.key",
            ["PGPASSFILE"] = "/p",
            ["PGOPTIONS"] = "-c statement_timeout=1",
        };
        PgToolProcess.PrepareEnvironment(environment, [], null);
        await Assert.That(environment.ContainsKey("PGSSLCERT")).IsTrue();
        await Assert.That(environment.ContainsKey("PGSSLKEY")).IsTrue();
        await Assert.That(environment.ContainsKey("PGPASSFILE")).IsTrue();
        await Assert.That(environment.ContainsKey("PGOPTIONS")).IsFalse();
    }

    // --- The files ---------------------------------------------------------------

    [Test]
    public async Task The_files_are_checked_before_a_connect()
    {
        var directory = NewDirectory();
        try
        {
            using var key = RSA.Create(2048);
            var certificatePath = Path.Combine(directory, "client.crt");
            var plainKeyPath = Path.Combine(directory, "client.key");
            var protectedKeyPath = Path.Combine(directory, "protected.key");
            var legacyKeyPath = Path.Combine(directory, "legacy.key");
            File.WriteAllText(certificatePath, "-----BEGIN CERTIFICATE-----\n-----END CERTIFICATE-----\n");
            File.WriteAllText(plainKeyPath, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(protectedKeyPath, key.ExportEncryptedPkcs8PrivateKeyPem("secret",
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000)));
            File.WriteAllText(legacyKeyPath, "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,00\n\n-----END RSA PRIVATE KEY-----\n");

            await Assert.That(ClientCertificates.Check(null, null)).IsNull();
            await Assert.That(ClientCertificates.Check(certificatePath, null)).IsNull();
            await Assert.That(ClientCertificates.Check(certificatePath, plainKeyPath)).IsNull();
            await Assert.That(ClientCertificates.Check(null, plainKeyPath)).Contains("needs its client certificate");
            await Assert.That(ClientCertificates.Check(Path.Combine(directory, "missing.crt"), null)).Contains("not found");
            await Assert.That(ClientCertificates.Check(certificatePath, Path.Combine(directory, "missing.key"))).Contains("not found");
            await Assert.That(ClientCertificates.Check(certificatePath, protectedKeyPath)).Contains("passphrase");
            await Assert.That(ClientCertificates.Check(certificatePath, legacyKeyPath)).Contains("passphrase");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // --- Mutual TLS against a local listener -------------------------------------

    /// <summary>
    /// A local listener plays a server that asks for a client certificate, and
    /// the profile's PEM certificate and key are what the client presents. The
    /// listener answers the startup message with nothing, so the connect fails
    /// after the handshake either way; what is checked is what arrived.
    /// </summary>
    [Test]
    [Retry(2)]
    public async Task The_profiles_client_certificate_is_presented_in_the_handshake()
    {
        using var authority = CreateAuthority();
        using var serverCertificate = Issue(authority, "CN=db.example.test", serverAuth: true);
        using var clientKey = RSA.Create(2048);
        using var clientCertificate = Issue(authority, "CN=me", serverAuth: false, clientKey);
        var directory = NewDirectory();
        try
        {
            var certificatePath = Path.Combine(directory, "client.crt");
            var keyPath = Path.Combine(directory, "client.key");
            File.WriteAllText(certificatePath, clientCertificate.ExportCertificatePem());
            File.WriteAllText(keyPath, clientKey.ExportPkcs8PrivateKeyPem());
            await Assert.That(ClientCertificates.Check(certificatePath, keyPath)).IsNull();

            var profile = Profile(certificatePath, keyPath, mode: SslMode.Require) with { Host = "127.0.0.1" };
            var presented = await HandshakeAsync(serverCertificate, port =>
                (profile with { Port = port }).CreateDataSource("pw", pooling: false));
            await Assert.That(presented).IsEqualTo(clientCertificate.Thumbprint);

            // Without the fields, nothing of ours is sent (the machine may have a
            // default ~/.postgresql/postgresql.crt, which is not this one).
            var without = await HandshakeAsync(serverCertificate, port =>
                (Profile(mode: SslMode.Require) with { Host = "127.0.0.1", Port = port }).CreateDataSource("pw", pooling: false));
            await Assert.That(without).IsNotEqualTo(clientCertificate.Thumbprint);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task<string?> HandshakeAsync(X509Certificate2 serverCertificate, Func<int, NpgsqlDataSource> createDataSource)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stopAccepting = new CancellationTokenSource();
        var presented = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stopAccepting.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                using (client)
                {
                    if (!presented.Task.IsCompleted && await ServeTlsAsync(client, serverCertificate, timeout.Token) is { } thumbprint)
                    {
                        presented.TrySetResult(thumbprint);
                    }
                }
            }
        });

        await using (var dataSource = createDataSource(port))
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
            }
            catch (Exception)
            {
                // Expected: the fake server never finishes the startup.
            }
        }

        await stopAccepting.CancelAsync();
        await server;
        presented.TrySetResult(null);
        return await presented.Task;
    }

    // The thumbprint of the certificate the client presented, "" for none, null for a failed handshake.
    private static async Task<string?> ServeTlsAsync(TcpClient client, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        try
        {
            var stream = client.GetStream();
            var request = new byte[8];
            while (true)
            {
                await stream.ReadExactlyAsync(request, cancellationToken);
                if (BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(4)) == 80877103) // SSLRequest
                {
                    break;
                }

                await stream.WriteAsync("N"u8.ToArray(), cancellationToken);
            }

            await stream.WriteAsync("S"u8.ToArray(), cancellationToken);
            X509Certificate? received = null;
            await using var ssl = new SslStream(stream, leaveInnerStreamOpen: true);
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = (_, presentedCertificate, _, _) =>
                    {
                        received = presentedCertificate;
                        return true;
                    },
                },
                cancellationToken);

            // The startup message, sent once the client accepted the handshake.
            var startup = new byte[8];
            await ssl.ReadExactlyAsync(startup, cancellationToken);
            return received is null ? string.Empty : new X509Certificate2(received).Thumbprint;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static X509Certificate2 CreateAuthority()
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=pgNimbus test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    // A server certificate comes back with its key, loadable by SChannel; a
    // client certificate comes back without it, since the test writes the key
    // to its own file as the profile names it.
    private static X509Certificate2 Issue(X509Certificate2 authority, string subject, bool serverAuth, RSA? clientKey = null)
    {
        using var ownKey = clientKey is null ? RSA.Create(2048) : null;
        var key = clientKey ?? ownKey!;
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (serverAuth)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(subject[3..]);
            request.CertificateExtensions.Add(san.Build());
        }

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(serverAuth ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var issued = request.Create(authority, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(12), serial);
        if (!serverAuth)
        {
            return issued;
        }

        using (issued)
        using (var withKey = issued.CopyWithPrivateKey(key))
        {
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        }
    }

    // --- ~/.pgpass -------------------------------------------------------------

    /// <summary>
    /// A profile with no stored password connects with none, and Npgsql then
    /// reads the password file (<c>PGPASSFILE</c>, else <c>~/.pgpass</c> or
    /// <c>%APPDATA%\postgresql\pgpass.conf</c>) by itself. A local listener asks
    /// for a cleartext password and reads what arrives.
    /// </summary>
    [Test]
    [NotInParallel(nameof(PGPASSFILE_is_read_for_a_profile_with_no_password))]
    public async Task PGPASSFILE_is_read_for_a_profile_with_no_password()
    {
        var directory = NewDirectory();
        var previous = Environment.GetEnvironmentVariable("PGPASSFILE");
        try
        {
            var passfile = Path.Combine(directory, "pgpass");
            File.WriteAllText(passfile, "127.0.0.1:*:app:me:from-the-pgpass-file\n");
            Environment.SetEnvironmentVariable("PGPASSFILE", passfile);

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var server = ServePasswordRequestAsync(listener, timeout.Token);

            var profile = Profile(mode: SslMode.Disable) with { Host = "127.0.0.1", Port = port };
            await using (var dataSource = profile.CreateDataSource(password: null, pooling: false))
            {
                try
                {
                    await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
                }
                catch (Exception)
                {
                    // The fake server stops after reading the password.
                }
            }

            await Assert.That(await server).IsEqualTo("from-the-pgpass-file");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGPASSFILE", previous);
            Directory.Delete(directory, true);
        }
    }

    private static async Task<string?> ServePasswordRequestAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        var head = new byte[8];
        while (true)
        {
            await stream.ReadExactlyAsync(head, cancellationToken);
            var length = BinaryPrimitives.ReadInt32BigEndian(head);
            var code = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(4));
            if (code is 80877103 or 80877104) // SSLRequest, GSSENCRequest
            {
                await stream.WriteAsync("N"u8.ToArray(), cancellationToken);
                continue;
            }

            await stream.ReadExactlyAsync(new byte[length - 8], cancellationToken); // the rest of the startup message
            break;
        }

        // AuthenticationCleartextPassword.
        var request = new byte[9];
        request[0] = (byte)'R';
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(1), 8);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(5), 3);
        await stream.WriteAsync(request, cancellationToken);

        var type = new byte[1];
        await stream.ReadExactlyAsync(type, cancellationToken);
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken);
        var body = new byte[BinaryPrimitives.ReadInt32BigEndian(lengthBytes) - 4];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return type[0] == (byte)'p' ? Encoding.UTF8.GetString(body).TrimEnd('\0') : null;
    }

    // --- Live: the server enforces them -------------------------------------------

    private static readonly string? LiveConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static NpgsqlDataSource LiveDataSource(int? statementTimeout, int? lockTimeout)
    {
        if (string.IsNullOrEmpty(LiveConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to enforce the timeouts.");
        }

        var builder = new NpgsqlConnectionStringBuilder(LiveConnectionString)
        {
            Options = ConnectionProfile.SessionOptions(readOnly: false, statementTimeout, lockTimeout),
            MaxPoolSize = 1,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    private static async Task<string> ShowAsync(NpgsqlDataSource dataSource, string setting)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SHOW {setting}", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed record Failure(string? SqlState, string Message, bool Lost);

    // A statement fails either before its rows (a QueryError) or while they
    // stream (an exception from the batches, which the tab shows as "Error: …").
    // Either way it must not read as a lost connection or an unknown outcome.
    private static async Task<Failure> FailureOf(StatementResult result)
    {
        if (result is QueryError error)
        {
            return new Failure(error.SqlState, error.Message, error.ConnectionLost || error.OutcomeUnknown);
        }

        try
        {
            await foreach (var _ in ((ResultSet)result).Batches)
            {
            }
        }
        catch (Exception ex)
        {
            return new Failure((ex as PostgresException)?.SqlState, ex.Message, ConnectionFailure.IsLoss(ex));
        }

        throw new InvalidOperationException("The statement did not fail.");
    }

    [Test]
    public async Task A_statement_past_the_timeout_reads_as_a_timeout_not_as_a_lost_connection()
    {
        await using var dataSource = LiveDataSource(statementTimeout: 1, lockTimeout: null);
        var engine = new QueryEngine(dataSource);

        var failure = await FailureOf(await engine.ExecuteAsync("SELECT pg_sleep(3)", CancellationToken.None));
        await Assert.That(failure.SqlState).IsEqualTo(PostgresErrorCodes.QueryCanceled);
        await Assert.That(failure.Message).Contains("statement timeout");
        await Assert.That(failure.Lost).IsFalse();

        // The app's own reads still run, and the pool's reset keeps the setting.
        await Assert.That(await ShowAsync(dataSource, "statement_timeout")).IsEqualTo("1s");
    }

    [Test]
    public async Task An_export_runs_past_the_timeout_and_the_next_statement_has_it_again()
    {
        await using var dataSource = LiveDataSource(statementTimeout: 1, lockTimeout: null);
        var engine = new QueryEngine(dataSource);

        var result = await engine.ExecuteAsync("SELECT 1 FROM pg_sleep(2)", CancellationToken.None, withoutStatementTimeout: true);
        await Assert.That(result).IsTypeOf<ResultSet>();
        var rows = 0;
        await foreach (var batch in ((ResultSet)result).Batches)
        {
            rows += batch.Rows.Count;
        }

        await Assert.That(rows).IsEqualTo(1);
        // One connection in the pool, so this is the one the export used.
        await Assert.That(await ShowAsync(dataSource, "statement_timeout")).IsEqualTo("1s");
    }

    [Test]
    [NotInParallel(nameof(A_lock_wait_past_the_lock_timeout_reads_as_55P03))]
    public async Task A_lock_wait_past_the_lock_timeout_reads_as_55P03()
    {
        const string table = "pgnimbus_lock_timeout_scratch";
        await using var admin = LiveDataSource(statementTimeout: null, lockTimeout: null);
        await using var holder = await admin.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand($"DROP TABLE IF EXISTS {table}; CREATE TABLE {table} (id int)", holder))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using var tx = await holder.BeginTransactionAsync();
            await using (var lockIt = new NpgsqlCommand($"LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE", holder, tx))
            {
                await lockIt.ExecuteNonQueryAsync();
            }

            await using var dataSource = LiveDataSource(statementTimeout: null, lockTimeout: 1);
            var engine = new QueryEngine(dataSource);
            var failure = await FailureOf(await engine.ExecuteAsync($"SELECT * FROM {table}", CancellationToken.None));
            await Assert.That(failure.SqlState).IsEqualTo(PostgresErrorCodes.LockNotAvailable);
            await Assert.That(failure.Message).Contains("lock timeout");
            await Assert.That(failure.Lost).IsFalse();
            await tx.RollbackAsync();
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {table}", holder);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
