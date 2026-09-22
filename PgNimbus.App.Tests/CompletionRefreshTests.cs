using Npgsql;
using PgNimbus.App.Completion;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Tests;

/// <summary>
/// The completion catalog's lifecycle (package I, T33/T35): a refresh that
/// fails keeps the previous catalog and says so, a disposed provider stops
/// refreshing, a newer refresh wins over an older one, and a search_path the
/// session changed is treated as unknown rather than followed.
/// </summary>
public class CompletionRefreshTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static CompletionCatalog Catalog(IReadOnlyList<string>? searchPath) => new(
        ["audit", "public"],
        [
            new CompletionTable("public", "users", [new("users", "id", "int4"), new("users", "name", "text")]),
            new CompletionTable("audit", "users", [new("users", "id", "int4"), new("users", "audit_only", "text")]),
            new CompletionTable("public", "orders", [new("orders", "id", "int4"), new("orders", "total", "numeric")]),
        ],
        [],
        [],
        searchPath);

    private static string[] Columns(SqlCompletionProvider provider, string marked)
    {
        var caret = marked.IndexOf('|');
        return [.. provider.GetCompletionData(marked.Remove(caret, 1), caret)
            .Where(i => i.Kind == SqlCompletionKind.Column)
            .Select(i => i.InsertText)];
    }

    // A closed local port: the connect fails at once, as a dropped server would.
    private static NpgsqlDataSource Unreachable() =>
        NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=3");

    [Test]
    public async Task A_failed_refresh_keeps_the_previous_catalog_and_reports_it_stale()
    {
        await using var dataSource = Unreachable();
        var provider = new SqlCompletionProvider(new SchemaService(dataSource));
        provider.Load(Catalog(["public"]));
        CompletionCatalogStatus? reported = null;
        provider.StatusChanged += status => reported = status;

        var published = await provider.RefreshAsync(CancellationToken.None);

        await Assert.That(published).IsFalse();
        await Assert.That(provider.Status.IsStale).IsTrue();
        await Assert.That(provider.Status.Error).IsNotNull();
        await Assert.That(reported).IsEqualTo(provider.Status);
        await Assert.That(Columns(provider, "SELECT * FROM users u WHERE u.|")).IsEquivalentTo(new[] { "id", "name" });
    }

    [Test]
    public async Task A_disposed_provider_does_not_refresh()
    {
        await using var dataSource = Unreachable();
        var provider = new SqlCompletionProvider(new SchemaService(dataSource));
        provider.Load(Catalog(["public"]));
        provider.Dispose();

        await Assert.That(await provider.RefreshAsync(CancellationToken.None)).IsFalse();
        await Assert.That(provider.Status.IsStale).IsFalse(); // nothing was attempted, nothing failed
    }

    [Test]
    public async Task A_search_path_changed_in_the_session_is_not_followed()
    {
        var provider = new SqlCompletionProvider(null);
        provider.Load(Catalog(["audit", "public"]));
        await Assert.That(Columns(provider, "SELECT * FROM users u WHERE u.|")).IsEquivalentTo(new[] { "id", "audit_only" });

        provider.SessionSearchPathChanged = true;
        await Assert.That(Columns(provider, "SELECT * FROM users u WHERE u.|")).IsEmpty();
        await Assert.That(Columns(provider, "SELECT * FROM orders o WHERE o.|")).IsEquivalentTo(new[] { "id", "total" });
        await Assert.That(Columns(provider, "SELECT * FROM audit.users u WHERE u.|")).IsEquivalentTo(new[] { "id", "audit_only" });
    }

    [Test]
    public async Task A_newer_refresh_wins_over_an_older_one_still_in_flight()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to refresh from.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        var provider = new SqlCompletionProvider(new SchemaService(dataSource));

        var older = provider.RefreshAsync(CancellationToken.None);
        var newer = provider.RefreshAsync(CancellationToken.None);

        await Assert.That(await older).IsFalse();
        await Assert.That(await newer).IsTrue();
        await Assert.That(provider.Status.IsStale).IsFalse();
    }
}
