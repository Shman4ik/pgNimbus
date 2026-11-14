using Avalonia.Controls;
using Npgsql;
using PgNimbus.App.Views;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// A closed main window leaves nothing of its own running on the server. Before
/// this, disposing the pool left the transaction's connection alone, so the
/// session stayed "idle in transaction" holding its locks until the process
/// exited, and a running query ran on to its end (measured 2026-11 on PG 17).
/// The window is closed for real; the question it asks is answered Close.
/// </summary>
[NotInParallel(nameof(DatabaseSwitchLiveTests))]
public class WindowCloseLiveTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task Closing_a_window_rolls_back_its_transaction_and_frees_its_locks()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to hold a transaction on.");
        }

        var tag = $"pgn-close-{Guid.NewGuid():N}"[..24];
        var table = $"close_lock_{Guid.NewGuid():N}"[..24];
        await using var observer = NpgsqlDataSource.Create(ConnectionString!);
        await ExecAsync(observer, $"CREATE TABLE {table} (id int PRIMARY KEY, v int); INSERT INTO {table} VALUES (1, 0);");
        await using var dataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(ConnectionString) { ApplicationName = tag }.ConnectionString);

        try
        {
            await Ui.Run(async () =>
            {
                var vm = Fixtures.MainWindowViewModel(liveDataSource: dataSource);
                var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
                Ui.Show(window);

                await vm.BeginTransactionCommand.ExecuteAsync(null);
                await Assert.That(await Ui.WaitUntilAsync(() => vm.IsInTransaction)).IsTrue();
                var tab = vm.ActiveTab;
                tab.Sql = $"UPDATE {table} SET v = 1 WHERE id = 1";
                await tab.RunCommand.ExecuteAsync(null);
                await Assert.That(await StatesAsync(observer, tag)).Contains("idle in transaction");

                await CloseAnsweringCloseAsync(window);

                await Assert.That(await EventuallyAsync(async () => !(await StatesAsync(observer, tag)).Contains("idle in transaction")))
                    .IsTrue();
            });

            // The row it had locked is free at once, and its update never landed.
            await using var connection = await observer.OpenConnectionAsync();
            await using (var timeout = new NpgsqlCommand("SET lock_timeout = '1s'", connection))
            {
                await timeout.ExecuteNonQueryAsync();
            }

            await using var update = new NpgsqlCommand($"UPDATE {table} SET v = v + 10 WHERE id = 1 RETURNING v", connection);
            await Assert.That((int)(await update.ExecuteScalarAsync())!).IsEqualTo(10);
        }
        finally
        {
            await ExecAsync(observer, $"DROP TABLE IF EXISTS {table}");
        }
    }

    [Test]
    public async Task Closing_a_window_cancels_its_running_query_on_the_server()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to run a query on.");
        }

        var tag = $"pgn-close-{Guid.NewGuid():N}"[..24];
        await using var observer = NpgsqlDataSource.Create(ConnectionString!);
        await using var dataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(ConnectionString) { ApplicationName = tag }.ConnectionString);

        await Ui.Run(async () =>
        {
            var vm = Fixtures.MainWindowViewModel(liveDataSource: dataSource);
            var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
            Ui.Show(window);

            var tab = vm.ActiveTab;
            tab.Sql = "SELECT pg_sleep(120)";
            var run = tab.RunCommand.ExecuteAsync(null);
            await Assert.That(await Ui.WaitUntilAsync(() => tab.IsRunning)).IsTrue();
            await Assert.That(await EventuallyAsync(async () => (await StatesAsync(observer, tag)).Contains("active"))).IsTrue();

            await CloseAnsweringCloseAsync(window);

            await Assert.That(await EventuallyAsync(async () => !(await StatesAsync(observer, tag)).Contains("active"))).IsTrue();
            await run;
        });
    }

    // Closes the window and, if it asks (it does: something would be lost),
    // answers Close. Without the question the window simply closes.
    private static async Task CloseAnsweringCloseAsync(Window window)
    {
        window.Close();
        Ui.Settle();
        if (window.OwnedWindows.OfType<ConfirmDialog>().SingleOrDefault() is { } dialog)
        {
            dialog.Close(true);
        }

        await Assert.That(await Ui.WaitUntilAsync(() => !window.IsVisible)).IsTrue();
    }

    // Polls for up to 10 s, keeping the UI thread pumping between reads.
    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return true;
            }

            Ui.Settle(1);
            await Task.Delay(50);
        }

        return false;
    }

    private static async Task ExecAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
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
