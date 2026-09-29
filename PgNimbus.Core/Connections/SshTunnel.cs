using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PgNimbus.Core.Connections;

/// <summary>
/// Opening the tunnel failed. The message is written for the connection form:
/// it says which step failed and what to check, rather than SSH.NET's
/// "Permission denied (publickey)." on its own.
/// </summary>
public sealed class SshTunnelException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A live local port forward through an SSH jump host to a database target.
/// Callers connect to <see cref="LocalHost"/>:<see cref="LocalPort"/> instead
/// of the real database endpoint. Disposing tears down both the forwarded
/// port and the underlying SSH connection.
/// </summary>
public sealed class SshTunnel : IDisposable
{
    /// <summary>
    /// SSH.NET's default is 30 s. A jump host behind a VPN that is switched off
    /// never answers at all, and half a minute of a frozen Connect button reads
    /// as a hang rather than as "unreachable".
    /// </summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Keeps an idle tunnel alive through NAT and VPN idle timers — a query tab
    /// left alone over lunch otherwise comes back to a dead forward.
    /// </summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>The key files <c>ssh</c> itself tries, in its order, when none is named.</summary>
    public static readonly IReadOnlyList<string> DefaultKeyFileNames = ["id_ed25519", "id_ecdsa", "id_rsa"];

    private readonly SshClient _client;
    private readonly ForwardedPortLocal _forwardedPort;

    private SshTunnel(SshClient client, ForwardedPortLocal forwardedPort)
    {
        _client = client;
        _forwardedPort = forwardedPort;
    }

    public string LocalHost => "127.0.0.1";

    public int LocalPort => (int)_forwardedPort.BoundPort;

    /// <summary>
    /// Opens the tunnel. <paramref name="hostKeys"/> decides whether the jump
    /// host is who it claims to be: SSH.NET raises <c>HostKeyReceived</c> on the
    /// connect thread in the middle of the key exchange and trusts every key
    /// unless told otherwise, so with no subscriber anyone on the path to the
    /// bastion could terminate the SSH session and relay it, owning the
    /// forwarded Postgres socket (and, with password auth, the SSH password).
    /// The verifier's verdict is stashed and thrown from here rather than from
    /// inside the event, where SSH.NET would wrap it into a bare
    /// "connection failed".
    /// </summary>
    public static SshTunnel Connect(SshTunnelOptions options, string password, string targetHost, int targetPort, SshHostKeyVerifier hostKeys) =>
        Connect(options, password, targetHost, targetPort, hostKeys, retryAfterPrompt: true);

    private static SshTunnel Connect(
        SshTunnelOptions options, string password, string targetHost, int targetPort, SshHostKeyVerifier hostKeys, bool retryAfterPrompt)
    {
        var connectionInfo = BuildConnectionInfo(options, password);
        connectionInfo.Timeout = ConnectTimeout;

        // A host known_hosts already knows is only offered the key types it is
        // known by, as OpenSSH does. Otherwise a server (or an interceptor)
        // presenting a key of another type fell through to "unknown host" and
        // a first-use prompt (review of the 2026-09 audit fixes); now the key
        // exchange finds no common algorithm and the connect is refused.
        var knownTypes = hostKeys.KnownKeyTypes(options.Host, options.Port);
        var restricted = RestrictHostKeyAlgorithms(connectionInfo, knownTypes);
        var promptsBefore = hostKeys.Prompts;

        var client = new SshClient(connectionInfo) { KeepAliveInterval = KeepAliveInterval };
        SshTunnelException? hostKeyFailure = null;
        byte[]? trustedKey = null;
        client.HostKeyReceived += (_, e) =>
        {
            // SSH.NET raises this again on every re-key of a long-lived session.
            // The key checked at connect is the only one this session will ever
            // accept: a later exchange presenting another is refused outright,
            // never put to a prompt whose window may be long gone.
            if (trustedKey is not null)
            {
                e.CanTrust = e.HostKey.AsSpan().SequenceEqual(trustedKey);
                return;
            }

            hostKeyFailure = hostKeys.Check(options.Host, options.Port, e.HostKeyName, e.HostKey);
            e.CanTrust = hostKeyFailure is null;
            if (e.CanTrust)
            {
                trustedKey = e.HostKey;
            }
        };

        try
        {
            client.Connect();
        }
        catch (Exception ex)
        {
            client.Dispose();

            // The prompt runs inside the key exchange, so its reading time counts
            // against ConnectTimeout: accepting after 20 s used to end in "could
            // not reach the SSH server" (review of the 2026-09 audit fixes). The
            // key is remembered by now, so one more attempt trusts it at once.
            if (retryAfterPrompt && ex is SshOperationTimeoutException && hostKeyFailure is null
                && trustedKey is not null && hostKeys.Prompts > promptsBefore)
            {
                return Connect(options, password, targetHost, targetPort, hostKeys, retryAfterPrompt: false);
            }

            if (restricted && hostKeyFailure is null && trustedKey is null && ex is SshConnectionException)
            {
                throw new SshTunnelException(
                    $"{options.Host}:{options.Port} did not offer a host key of the type known_hosts knows it by ({string.Join(", ", knownTypes.Order(StringComparer.Ordinal))}). "
                    + "This happens when the server's keys were replaced, and also when someone between you and the server is intercepting the connection. pgNimbus did not connect. "
                    + "If you know the server's keys really changed, remove its lines from known_hosts and connect again; you will be asked to confirm the new key.",
                    ex);
            }

            throw hostKeyFailure ?? Describe(ex, options);
        }

        // A verdict that SSH.NET somehow did not act on must still stop here:
        // a tunnel that is up on a refused key is the bug this exists to fix.
        if (hostKeyFailure is not null)
        {
            client.Disconnect();
            client.Dispose();
            throw hostKeyFailure;
        }

        var forwardedPort = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);

        try
        {
            client.AddForwardedPort(forwardedPort);
            forwardedPort.Start();
        }
        catch
        {
            forwardedPort.Dispose();
            client.Disconnect();
            client.Dispose();
            throw;
        }

        return new SshTunnel(client, forwardedPort);
    }

    /// <summary>
    /// The key file a profile means: <c>~</c> expanded against
    /// <paramref name="home"/>, or — when no path is given — the first of
    /// <c>ssh</c>'s default keys that exists. Null when nothing is found.
    /// </summary>
    public static string? ResolveKeyPath(string? path, string home, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var trimmed = path.Trim().Trim('"');
            return trimmed == "~" ? home
                : trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith(@"~\", StringComparison.Ordinal)
                    ? Path.GetFullPath(Path.Combine(home, trimmed[2..]))
                    : trimmed;
        }

        return DefaultKeyFileNames
            .Select(name => Path.Combine(home, ".ssh", name))
            .FirstOrDefault(exists);
    }

    private static ConnectionInfo BuildConnectionInfo(SshTunnelOptions options, string password) => options.AuthMethod switch
    {
        SshAuthMethod.Password => new PasswordConnectionInfo(options.Host, options.Port, options.Username, password),
        SshAuthMethod.PrivateKey => new PrivateKeyConnectionInfo(options.Host, options.Port, options.Username, LoadKeyFile(options, password)),
        SshAuthMethod.Agent => new PrivateKeyConnectionInfo(options.Host, options.Port, options.Username, LoadAgentKeys()),
        _ => throw new ArgumentOutOfRangeException(nameof(options)),
    };

    private static PrivateKeyFile LoadKeyFile(SshTunnelOptions options, string passphrase)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = ResolveKeyPath(options.PrivateKeyPath, home, File.Exists)
            ?? throw new SshTunnelException(
                $"No key file was named, and none of the default keys ({string.Join(", ", DefaultKeyFileNames)}) is in {Path.Combine(home, ".ssh")}.");

        try
        {
            return new PrivateKeyFile(path, passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw new SshTunnelException(
                $"The key file {path} is protected by a passphrase. Type it in the passphrase field, or choose \"SSH agent\" if the key is already loaded there (ssh-add).",
                ex);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SshTunnelException($"The key file {path} does not exist.", ex);
        }
        catch (Exception ex) when (ex is SshException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            var hint = string.IsNullOrEmpty(passphrase) ? string.Empty : " If the key has a passphrase, check that it is typed correctly.";
            throw new SshTunnelException($"Could not read the key file {path}: {ex.Message}{hint}", ex);
        }
    }

    private static SshAgentKeySource LoadAgentKeys()
    {
        var agent = SshAgentClient.FromEnvironment()
            ?? throw new SshTunnelException(
                "SSH_AUTH_SOCK is not set, so there is no SSH agent to ask. Start ssh-agent and add your key with ssh-add, or pick another auth method.");

        IReadOnlyList<SshAgentIdentity> identities;
        try
        {
            identities = agent.ListIdentities();
        }
        catch (SshAgentException ex)
        {
            throw new SshTunnelException(ex.Message, ex);
        }

        if (identities.Count == 0)
        {
            throw new SshTunnelException($"The SSH agent at {agent.Address} holds no keys. Add yours with ssh-add.");
        }

        return new SshAgentKeySource(agent, identities);
    }

    // Keeps only the host key algorithms whose key type known_hosts holds for
    // the host (RSA keys are stored as ssh-rsa and negotiated as rsa-sha2-512 or
    // -256). Nothing is removed when nothing is known, or when none of the known
    // types is one SSH.NET can negotiate (a security-key host key, say): the
    // verifier's own prompt or refusal then stands as before.
    private static bool RestrictHostKeyAlgorithms(ConnectionInfo info, IReadOnlySet<string> knownTypes)
    {
        if (knownTypes.Count == 0)
        {
            return false;
        }

        var drop = info.HostKeyAlgorithms.Keys.Where(a => !knownTypes.Contains(KeyTypeOf(a))).ToList();
        if (drop.Count == info.HostKeyAlgorithms.Count)
        {
            return false;
        }

        foreach (var algorithm in drop)
        {
            info.HostKeyAlgorithms.Remove(algorithm);
        }

        return true;

        static string KeyTypeOf(string algorithm) => algorithm is "rsa-sha2-512" or "rsa-sha2-256" ? "ssh-rsa" : algorithm;
    }

    private static SshTunnelException Describe(Exception ex, SshTunnelOptions options)
    {
        var server = $"{options.Host}:{options.Port}";
        return ex switch
        {
            SshTunnelException tunnel => tunnel,
            SshAgentException agent => new SshTunnelException(agent.Message, agent),
            SshAuthenticationException => new SshTunnelException(
                $"The SSH server {server} refused the login as {options.Username}: {ex.Message} {AuthHint(options.AuthMethod)}", ex),
            SocketException or SshOperationTimeoutException => new SshTunnelException(
                $"Could not reach the SSH server {server} ({ex.Message}). Check the address, and that the VPN or network it sits behind is up.", ex),
            SshConnectionException => new SshTunnelException($"The SSH server {server} closed the connection: {ex.Message}", ex),
            _ => new SshTunnelException($"SSH tunnel to {server} failed: {ex.Message}", ex),
        };
    }

    private static string AuthHint(SshAuthMethod method) => method switch
    {
        SshAuthMethod.Agent => "None of the SSH agent's keys is authorized for this user on the server.",
        SshAuthMethod.PrivateKey => "Check that this key's public half is in the user's ~/.ssh/authorized_keys on the server.",
        _ => "Check the SSH password.",
    };

    public void Dispose()
    {
        _forwardedPort.Stop();
        _forwardedPort.Dispose();
        _client.Disconnect();
        _client.Dispose();
    }
}
