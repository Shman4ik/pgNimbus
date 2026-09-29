using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// The history file holds every statement's text in the clear, so the store
/// itself redacts secrets on the way in, and scrubs entries that reached the
/// file before it did (security audit 2026-09, finding 8).
/// </summary>
public class QueryHistoryStoreTests
{
    [Test]
    public async Task Load_ScrubsAnOldEntryHoldingAPasswordAndRewritesTheFile()
    {
        // The shape history.json had before the redactor existed: no
        // Connection field, the password as typed.
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            [
              { "Sql": "ALTER ROLE app PASSWORD 'hunter2';", "ExecutedAt": "2026-08-01T10:00:00+00:00",
                "ElapsedMs": 3.5, "Summary": "ALTER ROLE", "Pinned": true },
              { "Sql": "SELECT 1;", "ExecutedAt": "2026-08-01T09:00:00+00:00",
                "ElapsedMs": 1, "Summary": "1 row" }
            ]
            """);

        try
        {
            var entries = new QueryHistoryStore(path).Load();

            await Assert.That(entries.Count).IsEqualTo(2);
            await Assert.That(entries[0].Sql).IsEqualTo("ALTER ROLE app PASSWORD '<redacted>'::redacted;");
            await Assert.That(entries[0].Pinned).IsTrue();
            await Assert.That(entries[0].Summary).IsEqualTo("ALTER ROLE");
            await Assert.That(entries[1].Sql).IsEqualTo("SELECT 1;");

            var onDisk = await File.ReadAllTextAsync(path);
            await Assert.That(onDisk).DoesNotContain("hunter2");
            // (The serializer writes < > ' as \u escapes.)
            await Assert.That(onDisk).Contains("redacted");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Load_LeavesACleanFileUntouched()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");
        const string json = """[ { "Sql": "SELECT 1;", "ExecutedAt": "2026-08-01T09:00:00+00:00", "ElapsedMs": 1, "Summary": "1 row" } ]""";
        await File.WriteAllTextAsync(path, json);

        try
        {
            new QueryHistoryStore(path).Load();

            // Byte for byte: no rewrite happened.
            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(json);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Append_RedactsWhateverItIsGiven()
    {
        // The view model redacts too, but the store is the choke point: a
        // future caller that forgets still can't write the secret.
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");

        try
        {
            var store = new QueryHistoryStore(path);
            store.Append(new QueryHistoryEntry("DO $$ BEGIN CREATE ROLE app LOGIN PASSWORD 's3cret'; END $$;", DateTimeOffset.UtcNow, 1, "DO"));

            await Assert.That(await File.ReadAllTextAsync(path)).DoesNotContain("s3cret");
            await Assert.That(store.Load()[0].Sql).IsEqualTo("DO $$ BEGIN CREATE ROLE app LOGIN PASSWORD '<redacted>'::redacted; END $$;");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
