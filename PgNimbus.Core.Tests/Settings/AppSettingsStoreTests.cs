using PgNimbus.Core.Settings;

namespace PgNimbus.Core.Tests.Settings;

public class AppSettingsStoreTests
{
    [Test]
    public async Task Load_NoFile_ReturnsDefaults()
    {
        var store = new AppSettingsStore(Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json"));

        var settings = store.Load();

        await Assert.That(settings.Theme).IsEqualTo("system");
        await Assert.That(settings.AutoAliasTables).IsTrue();
    }

    [Test]
    public async Task Load_FileFromOlderBuild_MissingFieldsFallBackToTheirDefaults()
    {
        // The trap this guards: the source-generated JSON deserializer bypasses
        // property initializers for init-only setters, so a non-zero default
        // added after this file was written would silently load as null/false if
        // AppSettings ever regressed from set to init accessors. Theme carries
        // that guard — a file omitting it must still fall back to "system", not
        // null. AutoAliasTables also defaults to true, so it doubles as a guard;
        // ShowAdvancedSchemaObjects defaults to false and is checked for
        // completeness only.
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{ "ShowAdvancedSchemaObjects": true }""");

        try
        {
            var settings = new AppSettingsStore(path).Load();

            await Assert.That(settings.Theme).IsEqualTo("system");
            await Assert.That(settings.ShowAdvancedSchemaObjects).IsTrue();
            await Assert.That(settings.AutoAliasTables).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task SaveThenLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");

        try
        {
            var store = new AppSettingsStore(path);
            store.Save(new AppSettings { Theme = "light", AutoAliasTables = false });

            var settings = store.Load();

            await Assert.That(settings.Theme).IsEqualTo("light");
            await Assert.That(settings.AutoAliasTables).IsFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task SaveThenLoad_RoundTripsRecentSqlFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");

        try
        {
            var store = new AppSettingsStore(path);
            store.Save(new AppSettings { RecentSqlFiles = ["/home/user/a.sql", "/home/user/b.sql"] });

            var settings = store.Load();

            await Assert.That(settings.RecentSqlFiles.Count).IsEqualTo(2);
            await Assert.That(settings.RecentSqlFiles[0]).IsEqualTo("/home/user/a.sql");
            await Assert.That(settings.RecentSqlFiles[1]).IsEqualTo("/home/user/b.sql");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Load_FileWithoutRecentSqlFilesProperty_LoadsAsEmptyList()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{ "Theme": "dark" }""");

        try
        {
            var settings = new AppSettingsStore(path).Load();

            await Assert.That(settings.Theme).IsEqualTo("dark");
            await Assert.That(settings.RecentSqlFiles).IsNotNull();
            await Assert.That(settings.RecentSqlFiles.Count).IsEqualTo(0);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
