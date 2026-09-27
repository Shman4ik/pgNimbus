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

    public static SshTunnel Connect(SshTunnelOptions options, string password, string targetHost, int targetPort)
    {
        var connectionInfo = BuildConnectionInfo(options, password);
        connectionInfo.Timeout = ConnectTimeout;

        var client = new SshClient(connectionInfo) { KeepAliveInterval = KeepAliveInterval };
        try
        {
            client.Connect();
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw Describe(ex, options);
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
