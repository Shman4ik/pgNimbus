using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Notifications;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Drives a <see cref="NotificationListener"/> from the UI: the channel list,
/// start/stop, the live feed, one selected notification's payload, and a
/// send box for publishing one back.
///
/// Notifications arrive on Npgsql's own background wait loop, not the UI
/// thread, so everything the listener raises — the notifications themselves and
/// the connection-lost / reconnected signals — is marshalled through
/// <see cref="Dispatcher.UIThread"/> before it touches an observable.
/// </summary>
public sealed partial class NotifyMonitorViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>
    /// How many notifications the feed keeps. A chatty channel publishes
    /// thousands an hour, and a monitor left open all afternoon must not grow
    /// without bound; the oldest fall off the end.
    /// </summary>
    public const int MaxNotifications = 500;

    private readonly NotificationListener _listener;
    private readonly Action<IReadOnlyList<string>>? _persistChannels;

    [ObservableProperty]
    private string _channelName = string.Empty;

    [ObservableProperty]
    private bool _isListening;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>A non-failure note: a reconnect, or a notification just sent. Cleared by the next action.</summary>
    [ObservableProperty]
    private string? _notice;

    /// <summary>The feed row whose payload the detail pane is showing.</summary>
    [ObservableProperty]
    private DatabaseNotification? _selectedNotification;

    /// <summary>The channel the send box publishes to; follows the channel list selection.</summary>
    [ObservableProperty]
    private string _sendChannel = string.Empty;

    [ObservableProperty]
    private string _sendPayload = string.Empty;

    /// <summary>The channel list's own selection — picks what the send box targets, and what ✕ removes.</summary>
    [ObservableProperty]
    private string? _selectedChannel;

    public ObservableCollection<string> Channels { get; } = [];

    public ObservableCollection<DatabaseNotification> Notifications { get; } = [];

    /// <summary>
    /// The payload of <see cref="SelectedNotification"/>, shown through the same
    /// view model the results grid's cell inspector uses — so a JSON payload
    /// (which is what most NOTIFY payloads are) gets the pretty-printing and the
    /// collapsible tree for free, instead of being a one-line string. Read-only
    /// here: there is nothing to write a notification back to.
    /// </summary>
    public CellInspectorViewModel Payload { get; } = new();

    /// <summary>Human status line ("Listening on 2 channels" / "Not listening") instead of a raw bool.</summary>
    public string ListeningStatus => IsListening
        ? $"Listening on {Channels.Count} channel{(Channels.Count == 1 ? "" : "s")}"
        : "Not listening";

    /// <param name="channels">Channels remembered for this connection, restored on open.</param>
    /// <param name="persistChannels">Writes the list back when it changes; null for a connection with nowhere to persist to.</param>
    public NotifyMonitorViewModel(
        NotificationListener listener,
        IEnumerable<string>? channels = null,
        Action<IReadOnlyList<string>>? persistChannels = null)
    {
        _listener = listener;
        _listener.NotificationReceived += OnNotificationReceived;
        _listener.Stopped += OnListenerStopped;
        _listener.Reconnected += OnListenerReconnected;
        _persistChannels = persistChannels;

        foreach (var channel in channels ?? [])
        {
            Channels.Add(channel);
        }

        // Restored channels are subscriptions waiting to happen, not a live
        // listener: opening a connection on window open is a surprise nobody
        // asked for, the same argument that keeps AutoConnectLastProfile off.
        SelectedChannel = Channels.FirstOrDefault();
    }

    private void OnNotificationReceived(DatabaseNotification notification) =>
        Dispatcher.UIThread.Post(() => Add(notification));

    /// <summary>
    /// Adds a notification to the feed as if the listener had raised it, with no
    /// server involved. A harness seam like <c>QueryViewModel.SeedResult</c>:
    /// the screenshot fixtures and the UI tests use it, production always
    /// arrives through the listener's event.
    /// </summary>
    public void SeedNotification(DatabaseNotification notification) => Add(notification);

    private void Add(DatabaseNotification notification)
    {
        Notifications.Insert(0, notification);

        while (Notifications.Count > MaxNotifications)
        {
            Notifications.RemoveAt(Notifications.Count - 1);
        }
    }

    // The listener gave up: the connection dropped and could not be re-established.
    // The point of this handler is that the UI stops claiming to listen — before
    // it, a dead wait loop left the dot green and the status line lying.
    private void OnListenerStopped(Exception error) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsListening = false;
            Notice = null;
            ErrorMessage = $"Stopped listening: {error.Message}";
        });

    private void OnListenerReconnected() =>
        Dispatcher.UIThread.Post(() =>
        {
            ErrorMessage = null;
            Notice = $"Reconnected at {DateTime.Now:HH:mm:ss} — notifications published while the connection was down were not delivered.";
        });

    private bool CanAddChannel() => !string.IsNullOrWhiteSpace(ChannelName) && !Channels.Contains(ChannelName.Trim());

    [RelayCommand(CanExecute = nameof(CanAddChannel))]
    private void AddChannel()
    {
        var channel = ChannelName.Trim();
        Channels.Add(channel);
        ChannelName = string.Empty;
        SelectedChannel = channel;
        ChannelsChanged();
    }

    [RelayCommand]
    private void RemoveChannel(string? channel)
    {
        if (channel is not null && Channels.Remove(channel))
        {
            ChannelsChanged();
        }
    }

    // A change to the list has to reach three places: the persisted settings,
    // the Start command's availability, and the status line's channel count.
    private void ChannelsChanged()
    {
        _persistChannels?.Invoke(Channels.ToList());
        StartListeningCommand.NotifyCanExecuteChanged();
        AddChannelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ListeningStatus));
    }

    private bool CanStartListening() => Channels.Count > 0 && !IsListening;

    [RelayCommand(CanExecute = nameof(CanStartListening))]
    private async Task StartListeningAsync()
    {
        try
        {
            await _listener.StartAsync(Channels.ToList(), CancellationToken.None);
            IsListening = true;
            ErrorMessage = null;
            Notice = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private bool CanStopListening() => IsListening;

    [RelayCommand(CanExecute = nameof(CanStopListening))]
    private async Task StopListeningAsync()
    {
        await _listener.StopAsync();
        IsListening = false;
        Notice = null;
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(SendChannel);

    /// <summary>
    /// Publishes a notification from here. pgAdmin's monitor needs a second
    /// session open to produce one, which makes "is my plumbing wired up?"
    /// a two-window job; this makes it one button.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var channel = SendChannel.Trim();
        try
        {
            await _listener.SendAsync(channel, SendPayload, CancellationToken.None);
            ErrorMessage = null;
            Notice = $"Sent on {channel} at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            Notice = null;
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void ClearNotifications()
    {
        Notifications.Clear();
        SelectedNotification = null;
    }

    partial void OnChannelNameChanged(string value) => AddChannelCommand.NotifyCanExecuteChanged();

    partial void OnSendChannelChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    // Picking a channel aims the send box at it, so publishing a test event to a
    // channel you are already watching takes no retyping.
    partial void OnSelectedChannelChanged(string? value)
    {
        if (value is not null)
        {
            SendChannel = value;
        }
    }

    partial void OnSelectedNotificationChanged(DatabaseNotification? value)
    {
        if (value is null)
        {
            Payload.IsOpen = false;
            return;
        }

        Payload.Open(value.Channel, value.Payload);
    }

    partial void OnIsListeningChanged(bool value)
    {
        StartListeningCommand.NotifyCanExecuteChanged();
        StopListeningCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ListeningStatus));
    }

    public async ValueTask DisposeAsync()
    {
        _listener.NotificationReceived -= OnNotificationReceived;
        _listener.Stopped -= OnListenerStopped;
        _listener.Reconnected -= OnListenerReconnected;
        await _listener.DisposeAsync();
    }
}
