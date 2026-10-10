using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Tests.Connections;

/// <summary>
/// A window opened on a restored database shares the first window's SSH
/// tunnel: the tunnel must outlive whichever of the two closes first, and
/// close with the last.
/// </summary>
public class SshTunnelLeaseTests
{
    private sealed class Counter : IDisposable
    {
        public int Disposed { get; private set; }

        public void Dispose() => Disposed++;
    }

    [Test]
    public async Task The_tunnel_closes_with_the_last_hold_whichever_order_they_go_in()
    {
        var tunnel = new Counter();
        var first = SshTunnelLease.CreateFor(tunnel);
        var second = first.Acquire();

        first.Dispose();
        await Assert.That(tunnel.Disposed).IsEqualTo(0);

        // Releasing twice counts once.
        first.Dispose();
        await Assert.That(tunnel.Disposed).IsEqualTo(0);

        second.Dispose();
        await Assert.That(tunnel.Disposed).IsEqualTo(1);
    }

    [Test]
    public async Task A_released_lease_hands_out_no_more()
    {
        var lease = SshTunnelLease.CreateFor(new Counter());
        lease.Dispose();

        await Assert.That(() => lease.Acquire()).Throws<ObjectDisposedException>();
    }
}
