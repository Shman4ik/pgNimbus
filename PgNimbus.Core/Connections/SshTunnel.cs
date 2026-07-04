using Renci.SshNet;

namespace PgNimbus.Core.Connections;

/// <summary>
/// A live local port forward through an SSH jump host to a database target.
/// Callers connect to <see cref="LocalHost"/>:<see cref="LocalPort"/> instead
/// of the real database endpoint. Disposing tears down both the forwarded
/// port and the underlying SSH connection.
/// </summary>
public sealed class SshTunnel : IDisposable
{
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

        var client = new SshClient(connectionInfo);
        client.Connect();

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

    private static ConnectionInfo BuildConnectionInfo(SshTunnelOptions options, string password) => options.AuthMethod switch
    {
        SshAuthMethod.Password => new PasswordConnectionInfo(options.Host, options.Port, options.Username, password),
        SshAuthMethod.PrivateKey => new PrivateKeyConnectionInfo(
            options.Host,
            options.Port,
            options.Username,
            new PrivateKeyFile(options.PrivateKeyPath ?? throw new InvalidOperationException("Private key path is required."), password)),
        _ => throw new ArgumentOutOfRangeException(nameof(options)),
    };

    public void Dispose()
    {
        _forwardedPort.Stop();
        _forwardedPort.Dispose();
        _client.Disconnect();
        _client.Dispose();
    }
}
