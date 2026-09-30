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
            await Assert.That(entries[0].Summary).IsEqualTo(QueryHistoryStore.WithheldSummary);
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
    public async Task Load_StampsAnUnstampedFileOnceAndThenLeavesItUntouched()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");
        const string json = """[ { "Sql": "SELECT 1;", "ExecutedAt": "2026-08-01T09:00:00+00:00", "ElapsedMs": 1, "Summary": "1 row" } ]""";
        await File.WriteAllTextAsync(path, json);

        try
        {
            var first = new QueryHistoryStore(path).Load();
            await Assert.That(first[0].Sql).IsEqualTo("SELECT 1;");
            await Assert.That(first[0].Redacted).IsNotNull();

            // The first load wrote the stamps; the second reads them and writes
            // nothing: byte for byte, no rewrite happened.
            var stamped = await File.ReadAllTextAsync(path);
            new QueryHistoryStore(path).Load();
            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(stamped);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Load_ScrubsAnEntryWhoseStampIsFromAnotherRedactorVersion()
    {
        // A stamp an older redactor wrote (or one somebody typed) vouches for
        // nothing: the entry is redacted again.
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            [ { "Sql": "ALTER ROLE app PASSWORD 'hunter2';", "ExecutedAt": "2026-08-01T10:00:00+00:00",
                "ElapsedMs": 1, "Summary": "ALTER ROLE", "Redacted": "0:00000000000000000000000000000000" } ]
            """);

        try
        {
            var entries = new QueryHistoryStore(path).Load();

            await Assert.That(entries[0].Sql).IsEqualTo("ALTER ROLE app PASSWORD '<redacted>'::redacted;");
            await Assert.That(await File.ReadAllTextAsync(path)).DoesNotContain("hunter2");
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

    [Test]
    public async Task Append_DropsTheErrorMessageOfAStatementThatHeldAPassword()
    {
        // The server quotes the token it failed on, and nothing beside it says
        // it is a secret, so only dropping the whole line keeps it out.
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");

        try
        {
            var store = new QueryHistoryStore(path);
            store.Append(new QueryHistoryEntry(
                "CREATE SUBSCRIPTION s CONNECTION 'host=db password=hunter2 dbname' PUBLICATION p;",
                DateTimeOffset.UtcNow, 1,
                "Error: invalid connection string syntax: missing \"=\" after \"dbname\" (near \"hunter2\")"));

            await Assert.That(await File.ReadAllTextAsync(path)).DoesNotContain("hunter2");
            await Assert.That(store.Load()[0].Summary).IsEqualTo(QueryHistoryStore.WithheldSummary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Load_DropsTheResultLineOfAnEntryRedactedBeforeTheRuleExisted()
    {
        // Written by the previous version: the text already redacted, the
        // result line as the server gave it.
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            [
              { "Sql": "ALTER ROLE app PASSWORD '<redacted>'::redacted VALID UNTILL 'x';", "ExecutedAt": "2026-08-01T10:00:00+00:00",
                "ElapsedMs": 1, "Summary": "Error: syntax error at or near \"UNTILL\"" }
            ]
            """);

        try
        {
            var loaded = new QueryHistoryStore(path).Load();
            await Assert.That(loaded[0].Summary).IsEqualTo(QueryHistoryStore.WithheldSummary);
            await Assert.That(await File.ReadAllTextAsync(path)).DoesNotContain("syntax error");

            // Idempotent: a second load finds nothing left to rewrite.
            var written = await File.ReadAllTextAsync(path);
            new QueryHistoryStore(path).Load();
            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(written);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Redact_KeepsTheResultLineOfAnOrdinaryStatement()
    {
        var entry = new QueryHistoryEntry("SELECT * FROM users WHERE email = 'a@b.c';", DateTimeOffset.UtcNow, 1,
            "Error: column \"emial\" does not exist");

        var redacted = QueryHistoryStore.Redact(entry);

        await Assert.That(redacted.Sql).IsEqualTo(entry.Sql);
        await Assert.That(redacted.Summary).IsEqualTo(entry.Summary);
    }

    [Test]
    public async Task Redact_ReturnsAStampedEntryAsItIs()
    {
        var stamped = QueryHistoryStore.Redact(new QueryHistoryEntry("SELECT 1;", DateTimeOffset.UtcNow, 1, "1 row"));

        await Assert.That(QueryHistoryStore.Redact(stamped)).IsSameReferenceAs(stamped);
        // Pinning keeps the text, so it keeps the stamp's worth too.
        var pinned = stamped with { Pinned = true };
        await Assert.That(QueryHistoryStore.Redact(pinned)).IsSameReferenceAs(pinned);
    }

    [Test]
    public async Task Redact_DoesNotTrustAStampOnTextThatChangedSince()
    {
        // The stamp hashes the text it vouches for, so a `with` that swaps the
        // text in cannot carry the old stamp's word over to a password.
        var stamped = QueryHistoryStore.Redact(new QueryHistoryEntry("SELECT 1;", DateTimeOffset.UtcNow, 1, "1 row"));
        var changed = stamped with { Sql = "ALTER ROLE app PASSWORD 'hunter2';" };

        var redacted = QueryHistoryStore.Redact(changed);

        await Assert.That(redacted.Sql).IsEqualTo("ALTER ROLE app PASSWORD '<redacted>'::redacted;");
        await Assert.That(redacted.Summary).IsEqualTo(QueryHistoryStore.WithheldSummary);
    }
}
