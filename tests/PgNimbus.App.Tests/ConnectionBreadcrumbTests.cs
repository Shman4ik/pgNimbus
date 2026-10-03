using PgNimbus.App.ViewModels;
using PgNimbus.Core.Connections;

namespace PgNimbus.App.Tests;

/// <summary>
/// The title bar's breadcrumb names the connection after its profile, and the
/// endpoint moves to the tooltip (2026-10: a Neon pooler host filled the bar).
/// </summary>
public class ConnectionBreadcrumbTests
{
    [Test]
    [Arguments("Campaign Manager", "Campaign Manager")]
    [Arguments("  Campaign Manager ", "Campaign Manager")]
    [Arguments(null, "db.example.com")]
    [Arguments("", "db.example.com")]
    [Arguments("db.example.com/app", "db.example.com")]
    [Arguments("DB.example.com/APP", "db.example.com")]
    public async Task The_breadcrumb_shows_a_chosen_name_and_otherwise_the_host(string? profileName, string expected)
    {
        await Assert.That(MainViewModel.BreadcrumbName(profileName, "db.example.com", "app")).IsEqualTo(expected);
    }

    [Test]
    public async Task The_tooltip_names_the_profile_host_and_the_ssh_hop()
    {
        var direct = new ConnectionProfile(Guid.NewGuid(), "Prod", "db.example.com", 6432, "app", "alice");
        await Assert.That(MainViewModel.DescribeEndpoint(direct)).IsEqualTo("alice@db.example.com:6432/app");

        // Through a tunnel the connection string says 127.0.0.1; the tooltip says
        // where the database really is, and how it is reached.
        var tunneled = direct with { SshTunnel = new SshTunnelOptions("bastion.example.com", 22, "ops", SshAuthMethod.Agent) };
        await Assert.That(MainViewModel.DescribeEndpoint(tunneled))
            .IsEqualTo("alice@db.example.com:6432/app\nthrough SSH: ops@bastion.example.com:22");
    }
}
