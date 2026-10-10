namespace PgNimbus.Core.Connections;

/// <summary>
/// One window's hold on an SSH tunnel that several windows may share. A window
/// opened on a database restored through the tunnel ("Open in New Window"
/// after a restore) goes through the same forward rather than signing in to
/// the jump host again, which would need the SSH secret the first window no
/// longer has. The tunnel closes when the last lease is disposed, so whichever
/// window closes first, the other keeps its connection.
/// </summary>
public sealed class SshTunnelLease : IDisposable
{
    private sealed class Shared(IDisposable resource, SshTunnel? tunnel)
    {
        public IDisposable Resource { get; } = resource;

        public SshTunnel? Tunnel { get; } = tunnel;

        public int Holders { get; set; } = 1;
    }

    private readonly Shared _shared;
    private bool _disposed;

    private SshTunnelLease(Shared shared) => _shared = shared;

    /// <summary>The first lease on <paramref name="tunnel"/>, which it now owns.</summary>
    public static SshTunnelLease Create(SshTunnel tunnel) => new(new Shared(tunnel, tunnel));

    /// <summary>A lease on any resource, for the tests, which can't open a tunnel.</summary>
    internal static SshTunnelLease CreateFor(IDisposable resource) => new(new Shared(resource, null));

    /// <summary>The shared tunnel: the local end to connect to.</summary>
    public SshTunnel Tunnel => _shared.Tunnel ?? throw new InvalidOperationException("This lease holds no tunnel.");

    /// <summary>Another hold on the same tunnel.</summary>
    /// <exception cref="ObjectDisposedException">This lease was already released, so the tunnel may be gone.</exception>
    public SshTunnelLease Acquire()
    {
        lock (_shared)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _shared.Holders++;
            return new SshTunnelLease(_shared);
        }
    }

    /// <summary>Releases this hold; the last one closes the tunnel.</summary>
    public void Dispose()
    {
        bool last;
        lock (_shared)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            last = --_shared.Holders == 0;
        }

        if (last)
        {
            _shared.Resource.Dispose();
        }
    }
}
