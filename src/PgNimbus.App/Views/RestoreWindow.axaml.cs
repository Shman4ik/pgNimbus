using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

/// <summary>
/// Restores a pg_dump archive with pg_restore (see <see cref="RestoreViewModel"/>).
/// Opens straight onto the file picker, since a restore starts with a file;
/// the archive is shown for what it is before anything runs. Not modal, like the
/// backup window, and closing it while pg_restore runs asks first.
/// </summary>
public partial class RestoreWindow : Window
{
    private bool _closingConfirmed;
    private bool _pickedOnOpen;

    public RestoreWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += (_, _) => Model?.Dispose();
    }

    /// <summary>Whether the file picker opens by itself when the window does; the tests turn it off.</summary>
    public bool PickFileOnOpen { get; set; } = true;

    private RestoreViewModel? Model => DataContext as RestoreViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Model is { } model)
        {
            model.ConfirmReplace = ConfirmReplaceAsync;
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        var loading = model.LoadAsync();
        if (PickFileOnOpen && !_pickedOnOpen && model.ArchivePath is null)
        {
            _pickedOnOpen = true;
            await PickFileAsync();
        }

        await loading;
    }

    private async Task<bool> ConfirmReplaceAsync(string message) =>
        await new ConfirmDialog(message, "Restore and Replace").ShowDialog<bool>(this);

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed || Model is not { IsRunning: true } model)
        {
            return;
        }

        e.Cancel = true;
        var stop = await new ConfirmDialog(
            "A restore is still running. Stop it and close the window? It runs as one transaction, so nothing it did stays.",
            "Stop Restore").ShowDialog<bool>(this);
        if (!stop)
        {
            return;
        }

        await model.StopAndWaitAsync();
        _closingConfirmed = true;
        Close();
    }

    private async void OnChooseFileClick(object? sender, RoutedEventArgs e) => await PickFileAsync();

    private async Task PickFileAsync()
    {
        if (Model is not { } model || StorageProvider is not { CanOpen: true } storage)
        {
            return;
        }

        var folder = model.ArchivePath is { } current ? Path.GetDirectoryName(current) : App.LoadSettings().LastBackupFolder;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a backup to restore",
            AllowMultiple = false,
            SuggestedStartLocation = string.IsNullOrEmpty(folder) ? null : await storage.TryGetFolderFromPathAsync(folder),
            FileTypeFilter =
            [
                new FilePickerFileType("pg_dump archive") { Patterns = ["*.dump", "*.backup", "*.tar", "*.pgdump"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await model.InspectAsync(path);
        }
    }

    private async void OnChooseToolsFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model && await BackupWindow.ChooseToolsFolderAsync(this) is { } folder)
        {
            await model.Tools.UseFolderAsync(folder);
        }
    }

    private async void OnCopyLogClick(object? sender, RoutedEventArgs e)
    {
        if (Model?.Log is not { Length: > 0 } log || Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(log);
            if (sender is Button button)
            {
                CopyFeedback.Show(button);
            }
        }
        catch (Exception)
        {
            // The log is selectable too.
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
