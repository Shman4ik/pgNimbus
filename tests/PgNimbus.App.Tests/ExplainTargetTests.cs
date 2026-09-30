using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// What the Explain commands send. A selection of several statements is refused
/// on the status line before the service is called (2026-09 security audit,
/// finding 3): the server would run every statement in it while planning only
/// the first. The fixture data source never answers, so a test that reached the
/// server would hang for the connect timeout; a refusal that lands at once is
/// the proof that nothing was sent.
/// </summary>
public class ExplainTargetTests
{
    private static QueryViewModel OfflineTab() =>
        new(new QueryEngine(Fixtures.DataSource), new ExplainService(Fixtures.DataSource));

    [Test]
    public async Task A_selection_of_several_statements_is_refused_on_the_status_line()
    {
        await Ui.Run(async () =>
        {
            var tab = OfflineTab();
            tab.Sql = "SELECT 1;\nCREATE TABLE t (id int);";
            tab.SelectedSql = tab.Sql;

            await tab.ExplainCommand.ExecuteAsync(null);

            await Assert.That(tab.HasError).IsTrue();
            await Assert.That(tab.Status).Contains("2 were given");
            await Assert.That(tab.IsShowingPlan).IsFalse();
            await Assert.That(tab.IsRunning).IsFalse();
        });
    }

    [Test]
    public async Task Explain_analyze_refuses_the_same_selection()
    {
        await Ui.Run(async () =>
        {
            var tab = OfflineTab();
            tab.Sql = "UPDATE t SET n = 1;\nCOMMIT;";
            tab.SelectedSql = tab.Sql;

            await tab.ExplainAnalyzeCommand.ExecuteAsync(null);

            await Assert.That(tab.HasError).IsTrue();
            await Assert.That(tab.Status).Contains("2 were given");
        });
    }
}
