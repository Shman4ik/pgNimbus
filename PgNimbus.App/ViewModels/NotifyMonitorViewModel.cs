using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Notifications;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Drives a <see cref="NotificationListener"/> from the UI: lets the user
/// build up a channel list, start/stop listening, and see incoming
/// notifications as they arrive. Notifications come in on Npgsql's own
/// background wait loop, not the UI thread, so they're marshalled via
/// <see cref="Dispatcher.UIThread"/> before touching the observable collection.
/// </summary>
public sealed partial class NotifyMonitorViewModel : ObservableObject, IAsyncDisposable
{
    private readonly NotificationListener _listener;

    [ObservableProperty]
    private string _channelName = string.Empty;

    [ObservableProperty]
    private bool _isListening;

    [ObservableProperty]
    private string? _errorMessage;

    public ObservableCollection<string> Channels { get; } = [];

    public ObservableCollection<DatabaseNotification> Notifications { get; } = [];

    public NotifyMonitorViewModel(NotificationListener listener)
    {
        _listener = listener;
        _listener.NotificationReceived += OnNotificationReceived;
    }

    private void OnNotificationReceived(DatabaseNotification notification) =>
        Dispatcher.UIThread.Post(() => Notifications.Insert(0, notification));

    private bool CanAddChannel() => !string.IsNullOrWhiteSpace(ChannelName) && !Channels.Contains(ChannelName.Trim());

    [RelayCommand(CanExecute = nameof(CanAddChannel))]
    private void AddChannel()
    {
        Channels.Add(ChannelName.Trim());
        ChannelName = string.Empty;
        StartListeningCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveChannel(string? channel)
    {
        if (channel is not null)
        {
            Channels.Remove(channel);
            StartListeningCommand.NotifyCanExecuteChanged();
        }
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
    }

    [RelayCommand]
    private void ClearNotifications() => Notifications.Clear();

    partial void OnChannelNameChanged(string value) => AddChannelCommand.NotifyCanExecuteChanged();

    partial void OnIsListeningChanged(bool value)
    {
        StartListeningCommand.NotifyCanExecuteChanged();
        StopListeningCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.NotificationReceived -= OnNotificationReceived;
        await _listener.DisposeAsync();
    }
}
