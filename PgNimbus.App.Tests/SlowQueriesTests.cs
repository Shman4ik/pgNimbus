using Avalonia.Controls;
using Avalonia.VisualTree;
using Npgsql;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Monitoring;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The slow-query shortlist (ROADMAP Q2): ranking and scope switches, the
/// states that explain a missing extension, and opening a statement as a tab
/// rather than running it. The server half lives in Core's
/// <c>StatementStatsServiceTests</c>; the end-to-end test here is gated on a
/// server that preloads pg_stat_statements.
/// </summary>
[NotInParallel]
public class SlowQueriesTests
{
    private static SlowQueriesViewModel Loaded()
    {
        var vm = Fixtures.MainWindowViewModel().SlowQueries;
        vm.Load(new StatementStatsRead(StatementStatsProblem.None, Fixtures.SlowQueriesSnapshot()));
        return vm;
    }

    [Test]
    public async Task Statements_rank_by_the_chosen_measure()
    {
        await Ui.Run(async () =>
        {
            var vm = Loaded();
            await Assert.That(vm.Rows[0].Activity.Statement.QueryId).IsEqualTo(1L);
            await Assert.That(vm.Rows[0].Share).IsEqualTo("56.2 %");

            vm.SetRankingCommand.Execute(StatementRanking.MeanTime);
            await Assert.That(vm.Rows[0].Activity.Statement.QueryId).IsEqualTo(2L);

            vm.SetRankingCommand.Execute(StatementRanking.Calls);
            await Assert.That(vm.Rows[0].Activity.Statement.QueryId).IsEqualTo(4L);
        });
    }

    [Test]
    public async Task The_interval_since_the_window_opened_starts_empty()
    {
        await Ui.Run(async () =>
        {
            var vm = Loaded();
            vm.SetScopeCommand.Execute(true);

            // The first read is its own baseline: nothing has run since.
            await Assert.That(vm.Rows.Count).IsEqualTo(0);
            await Assert.That(vm.EmptyText).StartsWith("Nothing ran between");
        });
    }

    [Test]
    public async Task A_missing_extension_explains_itself_and_offers_the_script_not_an_action()
    {
        await Ui.Run(async () =>
        {
            var main = Fixtures.MainWindowViewModel();
            var vm = main.SlowQueries;
            vm.Load(new StatementStatsRead(StatementStatsProblem.NotInstalled, null));
            await Assert.That(vm.HasProblem).IsTrue();
            await Assert.That(vm.ProblemTitle).Contains("isn't installed");

            var tabs = main.Tabs.Count;
            vm.OpenSetupScriptCommand.Execute(null);
            await Assert.That(main.Tabs.Count).IsEqualTo(tabs + 1);
            await Assert.That(main.ActiveTab.Sql).Contains("CREATE EXTENSION IF NOT EXISTS pg_stat_statements");

            // Security audit 2026-09, finding 18: the ALTER SYSTEM line replaces
            // the whole list, so running the tab whole must not run it.
            var alter = main.ActiveTab.Sql.Split('\n').Single(l => l.Contains("ALTER SYSTEM SET shared_preload_libraries", StringComparison.Ordinal));
            await Assert.That(alter.TrimStart()).StartsWith("--");

            vm.Load(new StatementStatsRead(StatementStatsProblem.NotLoaded, null));
            await Assert.That(vm.ProblemTitle).Contains("hasn't loaded it");
        });
    }

    [Test]
    public async Task The_palette_command_opens_one_window_and_asking_again_reuses_it()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            vm.ShowSlowQueriesCommand.Execute(null);
            Ui.SettleUntil(() => window.OwnedWindows.OfType<SlowQueriesWindow>().Any());
            vm.ShowSlowQueriesCommand.Execute(null);
            Ui.Settle();

            var owned = window.OwnedWindows.OfType<SlowQueriesWindow>().ToList();
            await Assert.That(owned.Count).IsEqualTo(1);
            await Assert.That(owned[0].DataContext).IsSameReferenceAs(vm.SlowQueries);

            owned[0].Close();
            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task The_chips_show_the_current_choice()
    {
        await Ui.Run(async () =>
        {
            var window = new SlowQueriesWindow { DataContext = Loaded() };
            Ui.Show(window);
            try
            {
                var chips = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("chip")).ToList();
                var total = chips.Single(b => b.Content as string == "Total time");
                var mean = chips.Single(b => b.Content as string == "Mean");
                var sinceReset = chips.Single(b => b.Content as string == "Since reset");

                await Assert.That(total.IsEffectivelyEnabled).IsTrue();
                await Assert.That(total.Classes.Contains("active")).IsTrue();
                await Assert.That(mean.Classes.Contains("active")).IsFalse();
                await Assert.That(sinceReset.Classes.Contains("active")).IsTrue();
            }
            finally
            {
                window.Close();
            }
        });
    }

    // --- Against a server that preloads pg_stat_statements -----------------

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task What_ran_since_the_window_opened_opens_as_a_tab_with_its_placeholders()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to read pg_stat_statements from.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using (var show = dataSource.CreateCommand("SHOW shared_preload_libraries"))
        {
            if (!((string)(await show.ExecuteScalarAsync())!).Contains("pg_stat_statements", StringComparison.Ordinal))
            {
                Skip.Test("The test server doesn't preload pg_stat_statements.");
            }
        }

        await using (var create = dataSource.CreateCommand("CREATE EXTENSION IF NOT EXISTS pg_stat_statements"))
        {
            await create.ExecuteNonQueryAsync();
        }

        await Ui.Run(async () =>
        {
            var vm = new SlowQueriesViewModel(new StatementStatsService(dataSource));
            string? opened = null;
            vm.OpenSqlRequested += (_, sql) => opened = sql;

            await vm.RefreshCommand.ExecuteAsync(null);
            for (var i = 0; i < 3; i++)
            {
                await using var run = dataSource.CreateCommand($"SELECT {i} AS pgnimbus_slow_probe, pg_sleep(0.01)");
                await run.ExecuteNonQueryAsync();
            }

            vm.SetScopeCommand.Execute(true);
            await vm.RefreshCommand.ExecuteAsync(null);

            var probe = vm.Rows.Single(r => r.Query.Contains("pgnimbus_slow_probe", StringComparison.Ordinal));
            await Assert.That(probe.Activity.Calls).IsEqualTo(3L);

            await vm.OpenInNewTabCommand.ExecuteAsync(probe);
            await Assert.That(opened).IsNotNull();
            await Assert.That(opened!).StartsWith("-- From pg_stat_statements: 3 calls");
            await Assert.That(opened).Contains("SELECT $1 AS pgnimbus_slow_probe");
        });
    }
}
