using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
using PgNimbus.Core.Connections;
using SslMode = PgNimbus.Core.Connections.SslMode;

namespace PgNimbus.Core.Tests.Connections;

/// <summary>
/// Security audit 2026-09, finding 9: what a profile asks of TLS. The default
/// for a profile with no mode, the root certificate a provider CA needs (from
/// the profile and from a pasted string), the picker's descriptions, and
/// Verify full through an SSH tunnel, where the socket's address is 127.0.0.1
/// but the certificate names the real host.
/// </summary>
public class TlsSettingsTests
{
    private static ConnectionProfile Profile(SslMode mode, string? rootCertificate = null, string host = "db.example.com") =>
        new(Guid.NewGuid(), "p", host, 5432, "app", "me", mode, RootCertificatePath: rootCertificate);

    // --- The profile's default -----------------------------------------------

    [Test]
    public async Task A_profile_that_names_no_mode_requires_encryption()
    {
        var profile = new ConnectionProfile(Guid.NewGuid(), "p", "h", 5432, "d", "u");
        await Assert.That(profile.SslMode).IsEqualTo(SslMode.Require);
    }

    [Test]
    public async Task A_saved_profile_without_the_field_loads_as_require_not_disable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-tls-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "connections.json");
            // Hand-edited: no SslMode (whose zero value is Disable), no RootCertificatePath.
            File.WriteAllText(path, """
                [
                  {
                    "Id": "8f6b3c1e-2f4a-4c55-9d7e-0a1b2c3d4e5f",
                    "Name": "hand-edited",
                    "Host": "db.example.com",
                    "Port": 5432,
                    "Database": "app",
                    "Username": "me"
                  },
                  {
                    "Id": "9f6b3c1e-2f4a-4c55-9d7e-0a1b2c3d4e5f",
                    "Name": "older",
                    "Host": "db.example.com",
                    "Port": 5432,
                    "Database": "app",
                    "Username": "me",
                    "SslMode": 2
                  }
                ]
                """);
            var store = new ConnectionProfileStore(path);
            var loaded = store.Load();
            await Assert.That(loaded[0].SslMode).IsEqualTo(SslMode.Require);
            await Assert.That(loaded[0].RootCertificatePath).IsNull();
            // A saved mode is kept as it was: the default is for new profiles only.
            await Assert.That(loaded[1].SslMode).IsEqualTo(SslMode.Prefer);

            store.Save([Profile(SslMode.VerifyFull, "/etc/ssl/rds-global-bundle.pem")]);
            var saved = store.Load().Single();
            await Assert.That(saved.SslMode).IsEqualTo(SslMode.VerifyFull);
            await Assert.That(saved.RootCertificatePath).IsEqualTo("/etc/ssl/rds-global-bundle.pem");
            // Derived, so it stays out of the file.
            await Assert.That(File.ReadAllText(path)).DoesNotContain("UsesRootCertificate");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task The_enum_keeps_its_numbers()
    {
        // Persisted as numbers: a renumbering would change every saved profile.
        var numbered = Enum.GetValues<SslMode>().Select(mode => $"{(int)mode}:{mode}");
        await Assert.That(numbered).IsEquivalentTo(
            ["0:Disable", "1:Allow", "2:Prefer", "3:Require", "4:VerifyCa", "5:VerifyFull"],
            CollectionOrdering.Matching);
    }

    // --- The connection string -------------------------------------------------

    [Test]
    public async Task A_root_certificate_reaches_the_connection_string_for_the_verifying_modes()
    {
        var full = new NpgsqlConnectionStringBuilder(Profile(SslMode.VerifyFull, "/certs/ca.pem").BuildConnectionString("pw"));
        await Assert.That(full.RootCertificate).IsEqualTo("/certs/ca.pem");
        await Assert.That(full.SslMode).IsEqualTo(Npgsql.SslMode.VerifyFull);

        var ca = new NpgsqlConnectionStringBuilder(Profile(SslMode.VerifyCa, "/certs/ca.pem").BuildConnectionString("pw"));
        await Assert.That(ca.RootCertificate).IsEqualTo("/certs/ca.pem");
    }

    [Test]
    public async Task No_root_certificate_is_written_without_one_or_for_a_mode_that_ignores_it()
    {
        var none = new NpgsqlConnectionStringBuilder(Profile(SslMode.VerifyFull).BuildConnectionString("pw"));
        await Assert.That(string.IsNullOrEmpty(none.RootCertificate)).IsTrue();

        // Npgsql checks nothing under Require; naming a CA there would read as if it did.
        var require = new NpgsqlConnectionStringBuilder(Profile(SslMode.Require, "/certs/ca.pem").BuildConnectionString("pw"));
        await Assert.That(string.IsNullOrEmpty(require.RootCertificate)).IsTrue();
        await Assert.That(require.SslMode).IsEqualTo(Npgsql.SslMode.Require);
    }

    // --- Pasted strings ------------------------------------------------------------

    [Test]
    [Arguments("postgres://me@db.example.com/app?sslmode=verify-full&sslrootcert=%2Fcerts%2Fca.pem")]
    [Arguments("host=db.example.com dbname=app sslmode=verify-full sslrootcert=/certs/ca.pem")]
    [Arguments("host=db.example.com dbname=app sslmode=verify-full sslrootcert='/certs/ca.pem'")]
    [Arguments("Host=db.example.com;Database=app;SSL Mode=VerifyFull;Root Certificate=/certs/ca.pem")]
    [Arguments("Host=db.example.com;Database=app;SSL Mode=VerifyFull;Root Certificate=\"/certs/ca.pem\"")]
    [Arguments("jdbc:postgresql://db.example.com/app?sslmode=verify-full&sslrootcert=/certs/ca.pem")]
    [Arguments("PGSSLMODE=verify-full PGSSLROOTCERT=/certs/ca.pem psql -h db.example.com app")]
    public async Task A_pasted_root_certificate_is_kept(string text)
    {
        await Assert.That(ConnectionStringParser.TryParse(text, out var parsed, out var error)).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(parsed.SslMode).IsEqualTo(SslMode.VerifyFull);
        await Assert.That(parsed.RootCertificatePath).IsEqualTo("/certs/ca.pem");
    }

    [Test]
    public async Task A_windows_path_survives_the_uri_and_the_keyword_dialects()
    {
        const string path = @"C:\Users\me\certs\ca.pem";

        var uri = "postgres://me@h/app?sslmode=verify-full&sslrootcert=" + Uri.EscapeDataString(path);
        await Assert.That(ConnectionStringParser.TryParse(uri, out var fromUri, out _)).IsTrue();
        await Assert.That(fromUri.RootCertificatePath).IsEqualTo(path);

        var npgsql = $"Host=h;SSL Mode=VerifyFull;Root Certificate={path}";
        await Assert.That(ConnectionStringParser.TryParse(npgsql, out var fromKeywords, out _)).IsTrue();
        await Assert.That(fromKeywords.RootCertificatePath).IsEqualTo(path);
    }

    [Test]
    public async Task Libpq_system_means_the_os_store()
    {
        await Assert.That(ConnectionStringParser.TryParse("host=h sslmode=verify-full sslrootcert=system", out var parsed, out _)).IsTrue();
        // Empty, not null: the string did say which CA to trust.
        await Assert.That(parsed.RootCertificatePath).IsEqualTo(string.Empty);

        await Assert.That(ConnectionStringParser.TryParse("host=h sslmode=require", out var silent, out _)).IsTrue();
        await Assert.That(silent.RootCertificatePath).IsNull();
    }

    [Test]
    public async Task Normalizing_to_npgsql_carries_the_root_certificate()
    {
        var normalized = ConnectionStringParser.NormalizeToNpgsql(
            "postgres://me@db.example.com/app?sslmode=verify-full&sslrootcert=%2Fcerts%2Fca.pem");
        var builder = new NpgsqlConnectionStringBuilder(normalized);
        await Assert.That(builder.RootCertificate).IsEqualTo("/certs/ca.pem");
        await Assert.That(builder.SslMode).IsEqualTo(Npgsql.SslMode.VerifyFull);
    }

    // --- The picker's text -----------------------------------------------------------

    [Test]
    public async Task Every_mode_has_a_label_and_a_description()
    {
        foreach (var mode in Enum.GetValues<SslMode>())
        {
            var info = SslModes.Describe(mode);
            await Assert.That(info.Mode).IsEqualTo(mode);
            await Assert.That(string.IsNullOrWhiteSpace(info.Label)).IsFalse();
            await Assert.That(string.IsNullOrWhiteSpace(info.Description)).IsFalse();
            // One line, and the humanizer rule for user-facing text.
            await Assert.That(info.Description).DoesNotContain("\n");
            await Assert.That(info.Description).DoesNotContain("\u2014");
            await Assert.That(info.Description).DoesNotContain("\u2013");
        }

        await Assert.That(SslModes.All.Select(i => i.Mode)).IsEquivalentTo(Enum.GetValues<SslMode>(), CollectionOrdering.Matching);
        await Assert.That(SslModes.All.Select(i => i.Label).Distinct().Count()).IsEqualTo(SslModes.All.Count);
    }

    [Test]
    public async Task Verify_full_is_the_one_recommended_and_require_says_it_checks_nothing()
    {
        await Assert.That(SslModes.All.Where(i => i.Recommended).Select(i => i.Mode)).IsEquivalentTo([SslMode.VerifyFull]);
        await Assert.That(SslModes.Describe(SslMode.Require).Description).Contains("not checked");
        await Assert.That(() => SslModes.Describe((SslMode)99)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task The_tls_hint_is_only_for_a_mode_that_insists_and_a_server_without_tls()
    {
        var refused = new NpgsqlException("SSL connection requested. No SSL enabled connection from this host is configured.");
        var wrapped = new InvalidOperationException("outer", refused);
        await Assert.That(SslModes.ServerWithoutTlsHint(wrapped, SslMode.Require)).Contains("Prefer or Disable");
        await Assert.That(SslModes.ServerWithoutTlsHint(refused, SslMode.VerifyFull)).IsNotNull();
        await Assert.That(SslModes.ServerWithoutTlsHint(refused, SslMode.Prefer)).IsNull();
        await Assert.That(SslModes.ServerWithoutTlsHint(new NpgsqlException("password authentication failed"), SslMode.Require)).IsNull();
    }

    // --- Against a real server -------------------------------------------------------

    private static readonly string? LiveConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    /// <summary>
    /// The test server (CI's and the local container's <c>postgres:17</c>) has
    /// no TLS, which is the case the new default has to handle honestly:
    /// Require must refuse rather than fall back to plaintext, and say what to
    /// change. Verify full against a real certificate is not exercised here; the
    /// handshake test above covers it with a local TLS listener.
    /// </summary>
    [Test]
    public async Task Require_refuses_a_server_without_tls_and_says_which_setting_to_change()
    {
        if (string.IsNullOrEmpty(LiveConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set: no Postgres to connect to.");
        }

        var target = new NpgsqlConnectionStringBuilder(LiveConnectionString);
        await using (var probe = NpgsqlDataSource.Create(LiveConnectionString!))
        await using (var connection = await probe.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("SHOW ssl", connection))
        {
            if ((string?)await command.ExecuteScalarAsync() == "on")
            {
                Skip.Test("The test server has TLS on; this test is about one without it.");
            }
        }

        var profile = new ConnectionProfile(Guid.NewGuid(), "p", target.Host!, target.Port, target.Database!, target.Username!);
        await Assert.That(profile.SslMode).IsEqualTo(SslMode.Require);

        Exception? failure = null;
        try
        {
            await ConnectionTester.TestAsync(profile, target.Password);
        }
        catch (Exception e)
        {
            failure = e;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(SslModes.ServerWithoutTlsHint(failure!, profile.SslMode)).IsNotNull();

        // The same profile at Prefer connects, in plaintext: the fallback Require exists to refuse.
        var version = await ConnectionTester.TestAsync(profile with { SslMode = SslMode.Prefer }, target.Password);
        await Assert.That(string.IsNullOrEmpty(version)).IsFalse();
    }

    // --- Verify full through a tunnel -------------------------------------------------

    [Test]
    public async Task A_tunnelled_connection_names_the_real_host_as_the_tls_target()
    {
        var profile = Profile(SslMode.VerifyFull, host: "db.internal.example");
        var options = new SslClientAuthenticationOptions { TargetHost = "127.0.0.1" };
        profile.ApplyTunnelTargetHost(options);
        await Assert.That(options.TargetHost).IsEqualTo("db.internal.example");
    }

    /// <summary>
    /// End to end, with no Postgres: a local listener answers Postgres's
    /// SSLRequest and completes a TLS handshake with a certificate issued to
    /// <c>db.example.test</c> by a throwaway CA the profile names as its root.
    /// Connecting to it at 127.0.0.1 as a tunnel would, Verify full passes only
    /// because the profile's host is the TLS target, and without that it fails.
    /// </summary>
    [Test]
    public async Task Verify_full_passes_through_a_tunnel_and_fails_without_the_real_host()
    {
        const string serverName = "db.example.test";
        using var authority = CreateAuthority();
        using var serverCertificate = IssueServerCertificate(authority, serverName);
        var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-tls-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var caPath = Path.Combine(directory, "ca.pem");
            File.WriteAllText(caPath, authority.ExportCertificatePem());
            var profile = Profile(SslMode.VerifyFull, caPath, host: serverName);

            // Through CreateDataSource, as the connect path builds it.
            var tunnelled = await HandshakeAsync(serverCertificate, port =>
                profile.CreateDataSource("pw", ("127.0.0.1", port), pooling: false));
            await Assert.That(tunnelled.Completed).IsTrue();
            await Assert.That(tunnelled.ServerNameIndication).IsEqualTo(serverName);

            // The same connection string without the callback: the name the
            // certificate is checked against is 127.0.0.1, and the client refuses.
            var plain = await HandshakeAsync(serverCertificate, port =>
                NpgsqlDataSource.Create(profile.BuildConnectionString("pw", ("127.0.0.1", port))));
            await Assert.That(plain.Completed).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed record HandshakeResult(bool Completed, string? ServerNameIndication);

    private static async Task<HandshakeResult> HandshakeAsync(X509Certificate2 certificate, Func<int, NpgsqlDataSource> createDataSource)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stopAccepting = new CancellationTokenSource();
        var first = new TaskCompletionSource<HandshakeResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Npgsql tries a failed open a second time, so the listener keeps
        // accepting: the first connection is the one measured, and any later
        // one is closed at once rather than left in the backlog, where it would
        // wait out the whole connect timeout for an answer.
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
                    if (!first.Task.IsCompleted)
                    {
                        first.TrySetResult(await ServeAsync(client, certificate, timeout.Token));
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
                // Expected either way: the fake server never finishes the startup.
            }
        }

        var result = await first.Task.WaitAsync(timeout.Token);
        await stopAccepting.CancelAsync();
        await server;
        return result;
    }

    private static async Task<HandshakeResult> ServeAsync(TcpClient client, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        try
        {
            var stream = client.GetStream();
            var request = new byte[8];
            while (true)
            {
                await stream.ReadExactlyAsync(request, cancellationToken);
                var code = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(4));
                if (code == 80877103) // SSLRequest
                {
                    break;
                }

                // GSSENCRequest, or anything else: decline and wait for the next.
                await stream.WriteAsync("N"u8.ToArray(), cancellationToken);
            }

            await stream.WriteAsync("S"u8.ToArray(), cancellationToken);
            await using var ssl = new SslStream(stream, leaveInnerStreamOpen: true);
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions { ServerCertificate = certificate },
                cancellationToken);

            // The server's side of the handshake finishes before a client
            // validates the certificate (SChannel checks it afterwards), so what
            // shows the client accepted it is the startup message it sends next,
            // inside the tunnel. That gets no answer, and the client gives up.
            var startup = new byte[8];
            await ssl.ReadExactlyAsync(startup, cancellationToken);
            return new HandshakeResult(true, ssl.TargetHostName);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new HandshakeResult(false, null);
        }
    }

    private static X509Certificate2 CreateAuthority()
    {
        // Not disposed: the returned certificate signs the server's with it.
        var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=pgNimbus test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 IssueServerCertificate(X509Certificate2 authority, string dnsName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = request.Create(authority, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(12), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        // SChannel cannot serve an ephemeral key; a PKCS#12 round trip gives it a usable one.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }
}
