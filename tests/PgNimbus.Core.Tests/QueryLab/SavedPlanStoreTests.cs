using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;

namespace PgNimbus.Core.Tests.QueryLab;

/// <summary>
/// Query Lab's saved plans on disk (<see cref="SavedPlanStore"/>): an index plus a file
/// per plan, refused rather than evicted at the caps, and never overwritten from a list
/// that couldn't be read.
/// </summary>
[NotInParallel(nameof(SavedPlanStore))]
public sealed class SavedPlanStoreTests : IDisposable
{
    private readonly List<string> _directories = [];

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pgnimbus-lab-{Guid.NewGuid():N}");
        _directories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _directories.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SavedPlan Saved(string name, DateTimeOffset? at = null) =>
        new(Guid.NewGuid(), name, at ?? new DateTimeOffset(2026, 10, 26, 9, 41, 0, TimeSpan.Zero), SavedPlanSource.Explain,
            "SELECT 1", "17.11", "127.0.0.1/postgres", new SavedPlanSummary(true, 13.4, 6));

    [Test]
    public async Task A_saved_plan_comes_back_with_its_text_and_context()
    {
        var store = new SavedPlanStore(NewDirectory());
        var plan = Saved("orders · before index");

        store.Save(plan, RealPlans.BeforeJson);

        await Assert.That(store.Load()).IsEquivalentTo([plan]);
        await Assert.That(store.ReadPlan(plan.Id)).IsEqualTo(RealPlans.BeforeJson);
        await Assert.That(ExplainService.Import(store.ReadPlan(plan.Id)!).Result.Root.NodeType).IsEqualTo("Aggregate");
        await Assert.That(File.Exists(Path.Combine(store.Directory!, plan.Id.ToString("N") + ".plan"))).IsTrue();
    }

    [Test]
    public async Task Saving_the_same_id_replaces_it_and_rename_keeps_the_plan()
    {
        var store = new SavedPlanStore(NewDirectory());
        var plan = Saved("first");
        store.Save(plan, RealPlans.BeforeJson);

        store.Save(plan with { Name = "second" }, RealPlans.AfterJson);
        store.Rename(plan.Id, "third");

        await Assert.That(store.Load().Select(p => p.Name)).IsEquivalentTo(["third"]);
        await Assert.That(store.ReadPlan(plan.Id)).IsEqualTo(RealPlans.AfterJson);
    }

    [Test]
    public async Task Delete_removes_the_entry_and_the_plan_file()
    {
        var store = new SavedPlanStore(NewDirectory());
        var keep = Saved("keep");
        var drop = Saved("drop");
        store.Save(keep, "Result  (cost=0.00..0.01 rows=1 width=4)");
        store.Save(drop, "Result  (cost=0.00..0.01 rows=1 width=4)");

        store.Delete(drop.Id);

        await Assert.That(store.Load().Select(p => p.Id)).IsEquivalentTo([keep.Id]);
        await Assert.That(store.ReadPlan(drop.Id)).IsNull();
        await Assert.That(Directory.GetFiles(store.Directory!, "*.plan").Length).IsEqualTo(1);
    }

    [Test]
    public async Task The_cap_refuses_a_save_and_drops_nothing()
    {
        var store = new SavedPlanStore(NewDirectory());
        for (var i = 0; i < SavedPlanStore.MaxPlans; i++)
        {
            store.Save(Saved($"plan {i}"), "Result  (cost=0.00..0.01 rows=1 width=4)");
        }

        var refused = Assert.Throws<SavedPlanStoreException>(() => store.Save(Saved("one more"), "Result  (cost=0.00..0.01 rows=1 width=4)"));

        await Assert.That(refused.Message).Contains("Delete some");
        await Assert.That(store.Load().Count).IsEqualTo(SavedPlanStore.MaxPlans);
        await Assert.That(store.Load().Any(p => p.Name == "plan 0")).IsTrue();
        // Replacing one of them is still a save, not a new plan.
        var first = store.Load()[0];
        store.Save(first with { Name = "renamed" }, "Result  (cost=0.00..0.01 rows=1 width=4)");
        await Assert.That(store.Load()[0].Name).IsEqualTo("renamed");
    }

    [Test]
    public async Task A_plan_larger_than_the_parser_reads_is_refused()
    {
        var store = new SavedPlanStore(NewDirectory());

        await Assert.That(() => store.Save(Saved("huge"), new string('x', SavedPlanStore.MaxPlanLength + 1)))
            .Throws<SavedPlanStoreException>();
        await Assert.That(store.Load()).IsEmpty();
    }

    [Test]
    public async Task A_corrupt_index_is_moved_aside_and_the_store_starts_over()
    {
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "index.json"), "{ not json");
        var store = new SavedPlanStore(directory);

        await Assert.That(store.Load()).IsEmpty();
        await Assert.That(Directory.GetFiles(directory, "index.json.corrupt-*").Length).IsEqualTo(1);
        store.Save(Saved("after"), "Result  (cost=0.00..0.01 rows=1 width=4)");
        await Assert.That(store.Load().Select(p => p.Name)).IsEquivalentTo(["after"]);
    }

    [Test]
    public async Task An_index_that_cannot_be_read_refuses_the_change_instead_of_overwriting_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("Holding a file unreadable needs a Windows share lock; root reads past a Unix mode.");
        }

        var store = new SavedPlanStore(NewDirectory());
        var plan = Saved("kept");
        store.Save(plan, "Result  (cost=0.00..0.01 rows=1 width=4)");
        var index = Path.Combine(store.Directory!, "index.json");
        var before = await File.ReadAllTextAsync(index);

        using (new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.That(() => store.Save(Saved("lost?"), "Result  (cost=0.00..0.01 rows=1 width=4)"))
                .Throws<SavedPlanStoreException>();
        }

        await Assert.That(await File.ReadAllTextAsync(index)).IsEqualTo(before);
    }

    [Test]
    public async Task Two_windows_stores_keep_each_others_plans()
    {
        var directory = NewDirectory();
        var one = new SavedPlanStore(directory);
        var two = new SavedPlanStore(directory);

        one.Save(Saved("from one"), "Result  (cost=0.00..0.01 rows=1 width=4)");
        two.Save(Saved("from two"), "Result  (cost=0.00..0.01 rows=1 width=4)");
        one.Delete(Guid.NewGuid());

        await Assert.That(one.Load().Select(p => p.Name)).IsEquivalentTo(["from one", "from two"]);
    }

    [Test]
    public async Task Every_change_is_announced()
    {
        var store = new SavedPlanStore(NewDirectory());
        var raised = 0;
        void OnChanged() => raised++;
        SavedPlanStore.Changed += OnChanged;
        try
        {
            var plan = Saved("x");
            store.Save(plan, "Result  (cost=0.00..0.01 rows=1 width=4)");
            store.Rename(plan.Id, "y");
            store.Delete(plan.Id);
            store.Delete(plan.Id);
        }
        finally
        {
            SavedPlanStore.Changed -= OnChanged;
        }

        // The second delete found nothing and changed nothing.
        await Assert.That(raised).IsEqualTo(3);
    }

    [Test]
    public async Task The_source_is_stored_as_a_number()
    {
        var store = new SavedPlanStore(NewDirectory());
        store.Save(Saved("x") with { Source = SavedPlanSource.Imported }, "Result  (cost=0.00..0.01 rows=1 width=4)");

        var json = await File.ReadAllTextAsync(Path.Combine(store.Directory!, "index.json"));

        await Assert.That(json).Contains("\"Source\": 2");
    }

    [Test]
    public async Task The_summary_is_read_from_the_plan()
    {
        var summary = SavedPlanSummary.Of(ExplainService.Import(RealPlans.AfterJson).Result);

        await Assert.That(summary).IsEqualTo(new SavedPlanSummary(true, 0.365, 6));
        await Assert.That(SavedPlanSummary.Of(ExplainService.Import("Seq Scan on t  (cost=0.00..1.00 rows=1 width=4)").Result).Analyzed)
            .IsFalse();
    }
}
