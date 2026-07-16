using PgNimbus.Core.Settings;

namespace PgNimbus.Core.Tests.Settings;

public class WorkspaceStoreTests
{
    [Test]
    public async Task GetEntry_NoFile_ReturnsNull()
    {
        var store = new WorkspaceStore(Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json"));

        var entry = store.GetEntry("localhost/demo");

        await Assert.That(entry).IsNull();
    }

    [Test]
    public async Task GetEntry_CorruptFile_ReturnsNullAndDoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "not valid json {{{");

        try
        {
            var entry = new WorkspaceStore(path).GetEntry("localhost/demo");

            await Assert.That(entry).IsNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task SaveThenGetEntry_RoundTripsTabsAndActiveIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");

        try
        {
            var store = new WorkspaceStore(path);
            var tabs = new List<WorkspaceTab>
            {
                new("SELECT 1;", null),
                new("SELECT * FROM orders;", "orders · source"),
            };

            store.Save("localhost/demo", tabs, activeTabIndex: 1);

            var entry = store.GetEntry("localhost/demo");

            await Assert.That(entry).IsNotNull();
            await Assert.That(entry!.Connection).IsEqualTo("localhost/demo");
            await Assert.That(entry.ActiveTabIndex).IsEqualTo(1);
            await Assert.That(entry.Tabs.Count).IsEqualTo(2);
            await Assert.That(entry.Tabs[0].Sql).IsEqualTo("SELECT 1;");
            await Assert.That(entry.Tabs[0].Title).IsNull();
            await Assert.That(entry.Tabs[1].Sql).IsEqualTo("SELECT * FROM orders;");
            await Assert.That(entry.Tabs[1].Title).IsEqualTo("orders · source");

            var otherEntry = store.GetEntry("otherhost/otherdb");

            await Assert.That(otherEntry).IsNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Save_SameConnectionTwice_ReplacesEntry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");

        try
        {
            var store = new WorkspaceStore(path);
            store.Save("localhost/demo", [new WorkspaceTab("SELECT 1;")], activeTabIndex: 0);
            store.Save("localhost/demo", [new WorkspaceTab("SELECT 2;"), new WorkspaceTab("SELECT 3;")], activeTabIndex: 1);

            var entry = store.GetEntry("localhost/demo");

            await Assert.That(entry).IsNotNull();
            await Assert.That(entry!.Tabs.Count).IsEqualTo(2);
            await Assert.That(entry.Tabs[0].Sql).IsEqualTo("SELECT 2;");
            await Assert.That(entry.ActiveTabIndex).IsEqualTo(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task SaveThenGetEntry_RoundTripsFilePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");

        try
        {
            var store = new WorkspaceStore(path);
            var tabs = new List<WorkspaceTab> { new("SELECT 1;", "query.sql", "/home/user/query.sql") };

            store.Save("localhost/demo", tabs, activeTabIndex: 0);

            var entry = store.GetEntry("localhost/demo");

            await Assert.That(entry).IsNotNull();
            await Assert.That(entry!.Tabs[0].FilePath).IsEqualTo("/home/user/query.sql");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task GetEntry_JsonWithoutFilePathProperty_LoadsWithNullFilePath()
    {
        // A workspace.json written before FilePath existed on WorkspaceTab must
        // still deserialize — the new field falls back to its default (null)
        // rather than failing the whole load.
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path,
            """[{"Connection":"localhost/demo","SavedAt":"2026-01-01T00:00:00Z","Tabs":[{"Sql":"SELECT 1;","Title":null}],"ActiveTabIndex":0}]""");

        try
        {
            var entry = new WorkspaceStore(path).GetEntry("localhost/demo");

            await Assert.That(entry).IsNotNull();
            await Assert.That(entry!.Tabs.Count).IsEqualTo(1);
            await Assert.That(entry.Tabs[0].Sql).IsEqualTo("SELECT 1;");
            await Assert.That(entry.Tabs[0].FilePath).IsNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Save_MoreThan20Connections_EvictsOldest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");

        try
        {
            var store = new WorkspaceStore(path);
            for (var i = 0; i < 21; i++)
            {
                store.Save($"host{i}/db", [new WorkspaceTab($"SELECT {i};")], activeTabIndex: 0);
            }

            await Assert.That(store.GetEntry("host0/db")).IsNull();

            for (var i = 1; i < 21; i++)
            {
                await Assert.That(store.GetEntry($"host{i}/db")).IsNotNull();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
