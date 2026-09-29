using System.Data.Common;
using Npgsql;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Tests.Connections;

/// <summary>
/// The host-key check against a real sshd, through the real
/// <see cref="SshTunnel.Connect"/>: that SSH.NET raises the event, that the
/// blob it hands over is the one <c>ssh-keyscan</c> sees, and that a refusal
/// really stops the connect (security audit 2026-09, finding 4).
///
/// Gated on <c>PGNIMBUS_TEST_SSH</c>, a <c>Key=Value;</c> string naming an
/// SSH server that takes password auth:
/// <c>Host=127.0.0.1;Port=2299;Username=pgn;Password=…</c>. An optional
/// <c>Target=host:port</c> names a Postgres the SSH server can reach; with it
/// and <c>PGNIMBUS_TEST_CONN</c> set, a query also runs through the tunnel.
/// Unset, every test here skips. Locally this is a
/// <c>linuxserver/openssh-server</c> container with
/// <c>AllowTcpForwarding yes</c>; the tests never read or write the user's
/// real <c>~/.ssh/known_hosts</c>, only temp files.
/// </summary>
[NotInParallel]
public class SshTunnelHostKeyLiveTests
{
    private static readonly string? Settings = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_SSH");

    private sealed record Server(SshTunnelOptions Options, string Password, string? TargetHost, int TargetPort);

    private static Server Require()
    {
        if (string.IsNullOrEmpty(Settings))
        {
            Skip.Test("PGNIMBUS_TEST_SSH not set: no SSH server to check host keys against.");
        }

        var builder = new DbConnectionStringBuilder { ConnectionString = Settings };
        string Get(string key) => builder.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

        var port = int.TryParse(Get("Port"), out var p) ? p : 22;
        var options = new SshTunnelOptions(Get("Host"), port, Get("Username"), SshAuthMethod.Password);

        string? targetHost = null;
        var targetPort = 5432;
        if (Get("Target") is { Length: > 0 } target)
        {
            var colon = target.LastIndexOf(':');
            targetHost = colon > 0 ? target[..colon] : target;
            if (colon > 0)
            {
                targetPort = int.Parse(target[(colon + 1)..]);
            }
        }

        return new Server(options, Get("Password"), targetHost, targetPort);
    }

    private sealed class Policy(bool answer) : ISshHostKeyPolicy
    {
        public List<SshHostKeyPrompt> Asked { get; } = [];

        public bool AcceptUnknown(SshHostKeyPrompt prompt)
        {
            Asked.Add(prompt);
            return answer;
        }
    }

    private sealed class Files : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "pgnimbus-ssh-live-" + Guid.NewGuid().ToString("N"));

        public Files() => Directory.CreateDirectory(_directory);

        public string User => Path.Combine(_directory, "user_known_hosts");

        public string Own => Path.Combine(_directory, "app", "known_hosts");

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private static SshTunnel Connect(Server server, SshHostKeyVerifier verifier) =>
        SshTunnel.Connect(server.Options, server.Password, server.TargetHost ?? "127.0.0.1", server.TargetPort, verifier);

    /// <summary>Accepts the server's key into a fresh file and returns that one entry.</summary>
    private static KnownHostEntry LearnKey(Server server, Files files)
    {
        using (Connect(server, new SshHostKeyVerifier(new Policy(answer: true), null, files.Own)))
        {
        }

        return KnownHosts.ParseLine(File.ReadAllLines(files.Own).Single(), 1)!;
    }

    [Test]
    public async Task An_unknown_host_is_put_to_the_policy_and_a_declined_key_does_not_connect()
    {
        var server = Require();
        using var files = new Files();
        var policy = new Policy(answer: false);

        var failure = Assert.Throws<SshTunnelException>(() => Connect(server, new SshHostKeyVerifier(policy, files.User, files.Own)));

        var prompt = policy.Asked.Single();
        await Assert.That(prompt.Host).IsEqualTo(server.Options.Host);
        await Assert.That(prompt.Port).IsEqualTo(server.Options.Port);
        await Assert.That(prompt.Fingerprint).StartsWith("SHA256:");
        await Assert.That(failure.Message).Contains("not accepted");
        await Assert.That(failure.Message).Contains(prompt.Fingerprint);
        await Assert.That(File.Exists(files.Own)).IsFalse();
    }

    [Test]
    public async Task An_accepted_key_is_remembered_in_the_form_ssh_writes_and_the_next_connect_asks_nobody()
    {
        var server = Require();
        using var files = new Files();
        var first = new Policy(answer: true);

        using (var tunnel = Connect(server, new SshHostKeyVerifier(first, files.User, files.Own)))
        {
            await Assert.That(tunnel.LocalPort).IsGreaterThan(0);
        }

        var prompt = first.Asked.Single();
        var entry = KnownHosts.ParseLine(File.ReadAllLines(files.Own).Single(), 1)!;
        await Assert.That(entry.MatchesHost(server.Options.Host, server.Options.Port)).IsTrue();
        await Assert.That(entry.KeyType).IsEqualTo(prompt.KeyType);
        await Assert.That(entry.Fingerprint).IsEqualTo(prompt.Fingerprint);

        var second = new Policy(answer: false);
        using (Connect(server, new SshHostKeyVerifier(second, files.User, files.Own)))
        {
        }

        await Assert.That(second.Asked).IsEmpty();
    }

    /// <summary>
    /// A key the user's own <c>known_hosts</c> holds is trusted with no prompt
    /// and nothing written. The line is hashed the way <c>ssh-keygen -H</c>
    /// hashes it, so a hashed user file is exercised against a live key too.
    /// </summary>
    [Test]
    public async Task A_key_in_the_users_hashed_known_hosts_is_trusted_without_asking()
    {
        var server = Require();
        using var files = new Files();
        var learned = LearnKey(server, files);
        File.Delete(files.Own);

        var salt = new byte[20];
        Random.Shared.NextBytes(salt);
        var hashedHost = KnownHosts.HashHost(KnownHosts.HostToken(server.Options.Host, server.Options.Port), salt);
        File.WriteAllText(files.User, $"{hashedHost} {learned.KeyType} {Convert.ToBase64String(learned.Key)}\n");

        var policy = new Policy(answer: false);
        using (Connect(server, new SshHostKeyVerifier(policy, files.User, files.Own)))
        {
        }

        await Assert.That(policy.Asked).IsEmpty();
        await Assert.That(File.Exists(files.Own)).IsFalse();
    }

    [Test]
    public async Task A_changed_key_is_refused_with_both_fingerprints_and_the_file_to_fix()
    {
        var server = Require();
        using var files = new Files();
        var learned = LearnKey(server, files);

        // Same type, different key: flip the blob's last byte (the type prefix,
        // which the reader checks, stays intact).
        var forged = (byte[])learned.Key.Clone();
        forged[^1] ^= 0xff;
        File.WriteAllText(files.Own, KnownHosts.FormatEntry(server.Options.Host, server.Options.Port, learned.KeyType, forged) + "\n");

        var policy = new Policy(answer: true);
        var failure = Assert.Throws<SshTunnelException>(() => Connect(server, new SshHostKeyVerifier(policy, files.User, files.Own)));

        await Assert.That(failure.Message).Contains("has changed");
        await Assert.That(failure.Message).Contains(KnownHosts.Fingerprint(forged));
        await Assert.That(failure.Message).Contains(learned.Fingerprint);
        await Assert.That(failure.Message).Contains($"{files.Own} (line 1)");
        await Assert.That(policy.Asked).IsEmpty();
        // The stored key is the evidence; it is never overwritten.
        await Assert.That(KnownHosts.ParseLine(File.ReadAllLines(files.Own).Single(), 1)!.Fingerprint).IsEqualTo(KnownHosts.Fingerprint(forged));
    }

    [Test]
    public async Task A_revoked_key_is_refused()
    {
        var server = Require();
        using var files = new Files();
        var learned = LearnKey(server, files);
        File.WriteAllText(files.User, "@revoked " + KnownHosts.FormatEntry(server.Options.Host, server.Options.Port, learned.KeyType, learned.Key) + "\n");

        var failure = Assert.Throws<SshTunnelException>(() => Connect(server, new SshHostKeyVerifier(new Policy(answer: true), files.User, files.Own)));

        await Assert.That(failure.Message).Contains("@revoked");
    }

    /// <summary>
    /// A host known_hosts knows by one key type, presenting a key of another,
    /// used to be "unknown" and got a first-use prompt, which is exactly what an
    /// interceptor with a fresh key would want. The connect is now offered only
    /// the known types, as OpenSSH does, so it is refused with no prompt (review
    /// of the 2026-09 audit fixes). The sshd has no P-521 key.
    /// </summary>
    [Test]
    public async Task A_server_with_no_key_of_the_known_type_is_refused_without_a_prompt()
    {
        var server = Require();
        using var files = new Files();
        File.WriteAllText(files.User, KnownHosts.FormatEntry(server.Options.Host, server.Options.Port, "ecdsa-sha2-nistp521", Nistp521Blob()) + "\n");
        var policy = new Policy(answer: true);

        var failure = Assert.Throws<SshTunnelException>(() => Connect(server, new SshHostKeyVerifier(policy, files.User, files.Own)));

        await Assert.That(failure.Message).Contains("did not offer a host key of the type known_hosts knows it by (ecdsa-sha2-nistp521)");
        await Assert.That(policy.Asked).IsEmpty();
        await Assert.That(File.Exists(files.Own)).IsFalse();
    }

    /// <summary>
    /// The prompt runs inside the key exchange, so a person reading the
    /// fingerprint for longer than <see cref="SshTunnel.ConnectTimeout"/> used to
    /// get "could not reach the SSH server". The accepted key is remembered, and
    /// the connect is tried once more without asking.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_key_accepted_after_the_connect_timeout_still_connects(CancellationToken ct)
    {
        var server = Require();
        using var files = new Files();
        var slow = new SlowPolicy(SshTunnel.ConnectTimeout + TimeSpan.FromSeconds(3));
        var verifier = new SshHostKeyVerifier(slow, files.User, files.Own);

        using (Connect(server, verifier))
        {
        }

        await Assert.That(verifier.Prompts).IsEqualTo(1);
        await Assert.That(File.ReadAllLines(files.Own)).Count().IsEqualTo(1);
    }

    private sealed class SlowPolicy(TimeSpan delay) : ISshHostKeyPolicy
    {
        public bool AcceptUnknown(SshHostKeyPrompt prompt)
        {
            Thread.Sleep(delay);
            return true;
        }
    }

    // An ecdsa-sha2-nistp521 public key blob, in the SSH wire format known_hosts stores.
    private static byte[] Nistp521Blob()
    {
        using var ec = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP521);
        var q = ec.ExportParameters(includePrivateParameters: false).Q;
        var point = new byte[1 + q.X!.Length + q.Y!.Length];
        point[0] = 4;
        q.X.CopyTo(point, 1);
        q.Y.CopyTo(point, 1 + q.X.Length);

        using var blob = new MemoryStream();
        foreach (var part in new[] { "ecdsa-sha2-nistp521"u8.ToArray(), "nistp521"u8.ToArray(), point })
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, part.Length);
            blob.Write(length);
            blob.Write(part);
        }

        return blob.ToArray();
    }

    /// <summary>The whole point of the tunnel: a query reaches Postgres through it once the key is trusted.</summary>
    [Test]
    public async Task A_query_runs_through_a_tunnel_whose_key_was_accepted()
    {
        var server = Require();
        var conn = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");
        if (server.TargetHost is null || string.IsNullOrEmpty(conn))
        {
            Skip.Test("PGNIMBUS_TEST_SSH has no Target=host:port, or PGNIMBUS_TEST_CONN is not set: nothing to query through the tunnel.");
        }

        using var files = new Files();
        using var tunnel = Connect(server, new SshHostKeyVerifier(new Policy(answer: true), files.User, files.Own));

        var builder = new NpgsqlConnectionStringBuilder(conn) { Host = tunnel.LocalHost, Port = tunnel.LocalPort, Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 41 + 1", connection);

        await Assert.That(await command.ExecuteScalarAsync()).IsEqualTo(42);
    }
}
