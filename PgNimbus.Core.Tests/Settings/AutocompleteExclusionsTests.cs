using PgNimbus.Core.Settings;

namespace PgNimbus.Core.Tests.Settings;

public class AutocompleteExclusionsTests
{
    [Test]
    public async Task For_UnknownConnection_IsEmpty()
    {
        var settings = new AppSettings
        {
            AutocompleteExcludedSchemas = { ["db1/app"] = ["billing"] },
        };

        await Assert.That(AutocompleteExclusions.For(settings, "db2/app")).IsEmpty();
    }

    [Test]
    public async Task For_NullConnection_IsEmpty()
    {
        // An ad-hoc connection with no host has no key to scope exclusions to;
        // it must read as "nothing excluded" rather than throwing.
        var settings = new AppSettings
        {
            AutocompleteExcludedSchemas = { ["db1/app"] = ["billing"] },
        };

        await Assert.That(AutocompleteExclusions.For(settings, null)).IsEmpty();
    }

    [Test]
    public async Task For_ScopesToItsOwnConnection()
    {
        var settings = new AppSettings
        {
            AutocompleteExcludedSchemas =
            {
                ["db1/app"] = ["billing", "legacy"],
                ["db2/app"] = ["audit"],
            },
        };

        var excluded = AutocompleteExclusions.For(settings, "db1/app");

        await Assert.That(excluded).Contains("billing");
        await Assert.That(excluded).Contains("legacy");
        await Assert.That(excluded).DoesNotContain("audit");
    }

    [Test]
    public async Task For_MatchesNamesOrdinally()
    {
        // Postgres identifiers are case-sensitive as stored: excluding "Reporting"
        // must not silently exclude a separate "reporting" schema too.
        var settings = new AppSettings
        {
            AutocompleteExcludedSchemas = { ["db1/app"] = ["Reporting"] },
        };

        var excluded = AutocompleteExclusions.For(settings, "db1/app");

        await Assert.That(excluded).Contains("Reporting");
        await Assert.That(excluded).DoesNotContain("reporting");
    }

    [Test]
    public async Task With_SortsAndDedupes()
    {
        var map = AutocompleteExclusions.With(new AppSettings(), "db1/app", ["legacy", "billing", "legacy"]);

        await Assert.That(map["db1/app"]).IsEquivalentTo(new List<string> { "billing", "legacy" });
    }

    [Test]
    public async Task With_EmptyList_RemovesTheEntry()
    {
        var settings = new AppSettings
        {
            AutocompleteExcludedSchemas =
            {
                ["db1/app"] = ["billing"],
                ["db2/app"] = ["audit"],
            },
        };

        var map = AutocompleteExclusions.With(settings, "db1/app", []);

        await Assert.That(map.ContainsKey("db1/app")).IsFalse();
        await Assert.That(map["db2/app"]).IsEquivalentTo(new List<string> { "audit" });
    }

    [Test]
    public async Task With_DoesNotMutateTheSourceSettings()
    {
        var settings = new AppSettings
        {
            AutocompleteExcludedSchemas = { ["db1/app"] = ["billing"] },
        };

        AutocompleteExclusions.With(settings, "db1/app", ["billing", "legacy"]);

        await Assert.That(settings.AutocompleteExcludedSchemas["db1/app"]).IsEquivalentTo(new List<string> { "billing" });
    }
}
