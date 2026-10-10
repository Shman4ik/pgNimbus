using PgNimbus.Core.Backup;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// The search on the machine the tests run on, with real programs. Where
/// <c>PGNIMBUS_TEST_PG_TOOLS</c> names an install (CI's PGDG client, a
/// developer's pgAdmin), the automatic search, with no folder configured, has
/// to find that same install and run it; that is the "it just finds pg_dump"
/// promise checked against a real machine rather than a described one.
/// </summary>
public class PgToolLiveTests
{
    [Test]
    public async Task The_automatic_search_finds_the_install_the_tests_use()
    {
        if (string.IsNullOrEmpty(LiveBackupDatabase.ToolsDirectory))
        {
            Skip.Test("PGNIMBUS_TEST_PG_TOOLS not set.");
        }

        var scan = await PgToolLocator.ScanAsync(configuredDirectory: null, CancellationToken.None);

        var expected = Path.GetFullPath(LiveBackupDatabase.ToolsDirectory!).TrimEnd(Path.DirectorySeparatorChar);
        var found = scan.Installs.FirstOrDefault(i => string.Equals(
            Path.GetFullPath(i.Directory).TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase));
        await Assert.That(found).IsNotNull().Because(string.Join("; ", scan.Problems.Select(p => $"{p.Directory}: {p.Reason}")));
        await Assert.That(found!.Version.Major).IsGreaterThanOrEqualTo(10);
    }

    [Test]
    public async Task A_folder_that_is_not_there_is_reported_not_crashed_on()
    {
        var scan = await PgToolLocator.ScanAsync(Path.Combine(Path.GetTempPath(), "no-such-pg-folder-" + Guid.NewGuid()), CancellationToken.None);

        await Assert.That(scan.Installs).IsEmpty();
        await Assert.That(scan.Problems.Single().Reason).Contains("no pg_dump");
    }
}
