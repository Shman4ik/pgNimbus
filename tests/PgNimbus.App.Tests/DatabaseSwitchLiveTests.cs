using Npgsql;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// What the database switch says it will end, it ends, on a real server. An
/// ordinary close of a main window does neither: disposing the pool leaves a
/// rented connection alone, so a transaction stayed "idle in transaction" and a
/// running statement ran on to its end (measured 2026-11).
/// </summary>
[NotInParallel(nameof(DatabaseSwitchLiveTests))]
public class DatabaseSwitchLiveTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task Ending_the_old_windows_work_rolls_back_its_transaction_and_cancels_its_query()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to hold a transaction on.");
        }

        var tag = $"pgn-switch-{Guid.NewGuid():N}"[..24];
        await using var observer = NpgsqlDataSource.Create(ConnectionString!);
        await using var dataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(ConnectionString) { ApplicationName = tag }.ConnectionString);

        await Ui.Run(async () =>
        {
            var vm = Fixtures.MainWindowViewModel(liveDataSource: dataSource);

            // (b) An open transaction is rolled back, and its session goes idle.
            await vm.BeginTransactionCommand.ExecuteAsync(null);
            await Assert.That(await Ui.WaitUntilAsync(() => vm.IsInTransaction)).IsTrue();
            await Assert.That(await StatesAsync(observer, tag)).Contains("idle in transaction");
            await Assert.That(vm.DescribeSwitchLosses()).IsEqualTo("The open transaction will be rolled back.");

            await vm.EndConnectionWorkAsync();
            await Assert.That(await Ui.WaitUntilAsync(() => !vm.IsInTransaction)).IsTrue();
            await Assert.That(await StatesAsync(observer, tag)).DoesNotContain("idle in transaction");

            // (c) A running query is cancelled on the server, not left to run.
            var tab = vm.ActiveTab;
            tab.Sql = "SELECT pg_sleep(60)";
            var run = tab.RunCommand.ExecuteAsync(null);
            await Assert.That(await Ui.WaitUntilAsync(() => tab.IsRunning)).IsTrue();
            var active = false;
            for (var i = 0; i < 100 && !active; i++)
            {
                active = (await StatesAsync(observer, tag)).Contains("active");
                Ui.Settle(1);
                await Task.Delay(50);
            }

            await Assert.That(active).IsTrue();
            await Assert.That(vm.DescribeSwitchLosses()).IsEqualTo("The running query will be cancelled.");

            await vm.EndConnectionWorkAsync();
            await Assert.That(await Ui.WaitUntilAsync(() => !tab.IsRunning, seconds: 15)).IsTrue();
            await run;
            await Assert.That(await StatesAsync(observer, tag)).DoesNotContain("active");
        });
    }

    private static async Task<List<string>> StatesAsync(NpgsqlDataSource observer, string applicationName)
    {
        await using var connection = await observer.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT coalesce(state, '') FROM pg_stat_activity WHERE application_name = @name", connection);
        command.Parameters.AddWithValue("name", applicationName);
        await using var reader = await command.ExecuteReaderAsync();
        var states = new List<string>();
        while (await reader.ReadAsync())
        {
            states.Add(reader.GetString(0));
        }

        return states;
    }
}
