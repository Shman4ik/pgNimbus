using Npgsql;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Commands;
using PgNimbus.Core.Notifications;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The LISTEN/NOTIFY monitor, after it stopped being a permanent sidebar tab:
/// that the command opens the window, and that the four things which made the
/// old panel unusable — channels forgotten on restart, an unbounded feed, a
/// payload shown as a trimmed one-liner, and a failure that left the UI
/// claiming to listen — behave.
/// </summary>
public class NotifyMonitorTests
{
    private static NotifyMonitorViewModel Monitor(
        IEnumerable<string>? channels = null,
        Action<IReadOnlyList<string>>? persist = null) =>
        new(new NotificationListener(Fixtures.DataSource), channels, persist);

    private static DatabaseNotification Notification(string channel = "order_events", string payload = "{}") =>
        new(channel, payload, 4822, DateTimeOffset.Now);

    /// <summary>
    /// The move out of the sidebar, end to end: the chord declared in the
    /// catalog reaches the command, and the window actually opens as a child of
    /// the shell.
    /// </summary>
    [Test]
    public async Task Chord_opens_the_monitor_window()
    {
        await Ui.Run(async () =>
        {
            var (window, _) = Scenarios.Shell();
            Ui.Show(window);

            Ui.Press(window, CommandId.NotifyMonitor);
            Ui.SettleUntil(() => Owned(window) is not null);

            var monitor = Owned(window);
            await Assert.That(monitor).IsNotNull();

            monitor!.Close();
            window.Close();
            Ui.Settle();
        });

        static NotifyMonitorWindow? Owned(Avalonia.Controls.Window window) =>
            window.OwnedWindows.OfType<NotifyMonitorWindow>().FirstOrDefault();
    }

    /// <summary>
    /// Channels survive a restart, and opening the window does not open a
    /// connection: restored subscriptions are a list waiting for Start, not a
    /// listener already running. Same argument as AutoConnectLastProfile.
    /// </summary>
    [Test]
    public async Task Restored_channels_are_listed_without_listening()
    {
        await Ui.Run(async () =>
        {
            var vm = Monitor(["order_events", "cache_invalidation"]);

            await Assert.That(vm.Channels).IsEquivalentTo(new[] { "order_events", "cache_invalidation" });
            await Assert.That(vm.IsListening).IsFalse();
            await Assert.That(vm.ListeningStatus).IsEqualTo("Not listening");

            // The send box aims at the first channel, so publishing a test event
            // to something you already watch takes no retyping.
            await Assert.That(vm.SendChannel).IsEqualTo("order_events");
        });
    }

    [Test]
    public async Task Adding_and_removing_a_channel_is_persisted()
    {
        await Ui.Run(async () =>
        {
            List<string>? persisted = null;
            var vm = Monitor(persist: channels => persisted = [.. channels]);

            vm.ChannelName = "  order_events  ";
            vm.AddChannelCommand.Execute(null);

            await Assert.That(persisted).IsEquivalentTo(new[] { "order_events" });
            await Assert.That(vm.ChannelName).IsEmpty();

            vm.RemoveChannelCommand.Execute("order_events");

            await Assert.That(persisted).IsEmpty();
            await Assert.That(vm.Channels).IsEmpty();
        });
    }

    /// <summary>
    /// A channel published to in a loop must not grow the feed without bound —
    /// the monitor is left open for an afternoon.
    /// </summary>
    [Test]
    public async Task Feed_is_newest_first_and_capped()
    {
        await Ui.Run(async () =>
        {
            var vm = Monitor();

            for (var i = 0; i < NotifyMonitorViewModel.MaxNotifications + 25; i++)
            {
                vm.SeedNotification(Notification(payload: i.ToString()));
            }

            await Assert.That(vm.Notifications).Count().IsEqualTo(NotifyMonitorViewModel.MaxNotifications);
            await Assert.That(vm.Notifications[0].Payload)
                .IsEqualTo((NotifyMonitorViewModel.MaxNotifications + 24).ToString());
            await Assert.That(vm.Notifications[^1].Payload).IsEqualTo("25");
        });
    }

    /// <summary>
    /// The reason the window has a detail pane: a JSON payload arrives as one
    /// long line and has to read as a document. Both halves of the cell
    /// inspector apply — the pretty-print and the collapsible tree.
    /// </summary>
    [Test]
    public async Task Selecting_a_notification_shows_its_payload_as_json()
    {
        await Ui.Run(async () =>
        {
            var vm = Monitor();
            vm.SeedNotification(Notification(payload: """{"event":"order.paid","order_id":4821}"""));

            vm.SelectedNotification = vm.Notifications[0];

            await Assert.That(vm.Payload.IsOpen).IsTrue();
            await Assert.That(vm.Payload.ColumnName).IsEqualTo("order_events");
            await Assert.That(vm.Payload.IsJson).IsTrue();
            await Assert.That(vm.Payload.DisplayText).Contains("\n");
            await Assert.That(vm.Payload.CanShowTreeToggle).IsTrue();

            vm.Payload.IsTreeView = true;
            await Assert.That(vm.Payload.TreeRoots).IsNotEmpty();

            // Nothing selected, nothing shown: the pane says so rather than
            // keeping the payload of a notification that is no longer listed.
            vm.ClearNotificationsCommand.Execute(null);
            await Assert.That(vm.Payload.IsOpen).IsFalse();
        });
    }

    /// <summary>
    /// A payload that is not JSON is still readable — plenty of channels carry
    /// a bare row id — and must not be dressed up as a document.
    /// </summary>
    [Test]
    public async Task A_plain_payload_stays_plain()
    {
        await Ui.Run(async () =>
        {
            var vm = Monitor();
            vm.SeedNotification(Notification("cache_invalidation", "products:24119"));

            vm.SelectedNotification = vm.Notifications[0];

            await Assert.That(vm.Payload.DisplayText).IsEqualTo("products:24119");
            await Assert.That(vm.Payload.IsJson).IsFalse();
            await Assert.That(vm.Payload.CanShowTreeToggle).IsFalse();
        });
    }

    /// <summary>
    /// The failure the window must never lie about. A start that cannot reach
    /// the server leaves the dot grey and the reason on screen — the old panel's
    /// wait loop could die and leave "Listening on N channels" standing.
    /// </summary>
    [Test]
    public async Task A_failed_start_reports_and_stays_not_listening()
    {
        await Ui.Run(async () =>
        {
            // Port 1 on the loopback refuses immediately, unlike the fixtures'
            // deliberately unroutable address, which would sit in a connect
            // timeout for the length of the test.
            await using var dataSource = NpgsqlDataSource.Create(
                "Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=2");
            var vm = new NotifyMonitorViewModel(new NotificationListener(dataSource), ["order_events"]);

            await vm.StartListeningCommand.ExecuteAsync(null);

            await Assert.That(vm.IsListening).IsFalse();
            await Assert.That(vm.ListeningStatus).IsEqualTo("Not listening");
            await Assert.That(vm.ErrorMessage).IsNotNull();
        });
    }
}
