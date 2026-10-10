using PgNimbus.Core.Backup;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// The install steps are what a user follows when a backup can't start, so
/// each platform's are checked for the exact command, the version it installs
/// and the closing "Look Again".
/// </summary>
public class PgToolInstallGuideTests
{
    [Test]
    public async Task The_newest_known_release_is_installed_unless_the_server_is_newer()
    {
        await Assert.That(PgToolInstallGuide.MajorToInstall(new PgVersion(14, 2))).IsEqualTo(PgToolInstallGuide.NewestKnownMajor);
        await Assert.That(PgToolInstallGuide.MajorToInstall(new PgVersion(21, 0))).IsEqualTo(21);
        await Assert.That(PgToolInstallGuide.MajorToInstall(new PgVersion(9, 6))).IsEqualTo(PgToolInstallGuide.NewestKnownMajor);
        await Assert.That(PgToolInstallGuide.MajorToInstall(null)).IsEqualTo(PgToolInstallGuide.NewestKnownMajor);
    }

    [Test]
    public async Task Windows_installs_only_the_command_line_tools_of_that_release()
    {
        var steps = PgToolInstallGuide.Steps(PgToolPlatform.Windows, 18);

        await Assert.That(steps.Any(s => s.LinkUrl == "https://www.enterprisedb.com/downloads/postgres-postgresql-downloads")).IsTrue();
        await Assert.That(steps.Any(s => s.Text.Contains("Command Line Tools", StringComparison.Ordinal))).IsTrue();
        await Assert.That(steps.Select(s => s.Command).OfType<string>()).Contains("winget install -e --id PostgreSQL.PostgreSQL.18 --interactive");
        await Assert.That(steps[^1].Text).Contains(@"C:\Program Files\PostgreSQL\18\bin");
    }

    [Test]
    public async Task macOS_uses_Homebrew_when_it_is_there_and_Postgres_app_otherwise()
    {
        var withBrew = PgToolInstallGuide.Steps(PgToolPlatform.MacOS, 18, hasHomebrew: true);
        await Assert.That(withBrew[0].Command).IsEqualTo("brew install libpq");

        var without = PgToolInstallGuide.Steps(PgToolPlatform.MacOS, 18, hasHomebrew: false);
        await Assert.That(without[0].LinkUrl).IsEqualTo("https://postgresapp.com/downloads.html");
    }

    [Test]
    public async Task Linux_names_the_package_for_the_distribution()
    {
        var ubuntu = PgToolInstallGuide.Steps(PgToolPlatform.Linux, 18, LinuxFamily.Ubuntu);
        await Assert.That(ubuntu.Select(s => s.Command).OfType<string>()).Contains("sudo apt install postgresql-client-18");

        var redHat = PgToolInstallGuide.Steps(PgToolPlatform.Linux, 17, LinuxFamily.RedHat);
        await Assert.That(redHat.Select(s => s.Command).OfType<string>()).Contains("sudo dnf install postgresql17");

        var arch = PgToolInstallGuide.Steps(PgToolPlatform.Linux, 18, LinuxFamily.Arch);
        await Assert.That(arch[0].Command).IsEqualTo("sudo pacman -S postgresql-libs");

        foreach (var steps in new[] { ubuntu, redHat, arch, PgToolInstallGuide.Steps(PgToolPlatform.Linux, 18) })
        {
            await Assert.That(steps[^1].Text).StartsWith("Then click Look Again.");
        }
    }

    [Test]
    [Arguments("ID=ubuntu\nID_LIKE=debian\n", LinuxFamily.Ubuntu)]
    [Arguments("ID=debian\n", LinuxFamily.Debian)]
    [Arguments("NAME=\"Pop!_OS\"\nID=pop\nID_LIKE=\"ubuntu debian\"\n", LinuxFamily.Ubuntu)]
    [Arguments("ID=\"rocky\"\nID_LIKE=\"rhel centos fedora\"\n", LinuxFamily.RedHat)]
    [Arguments("ID=fedora\n", LinuxFamily.RedHat)]
    [Arguments("ID=manjaro\nID_LIKE=arch\n", LinuxFamily.Arch)]
    [Arguments("ID=nixos\n", LinuxFamily.Other)]
    [Arguments("", LinuxFamily.Other)]
    public async Task The_distribution_is_read_from_os_release(string osRelease, LinuxFamily family)
    {
        await Assert.That(PgToolInstallGuide.ParseOsRelease(osRelease)).IsEqualTo(family);
    }
}
