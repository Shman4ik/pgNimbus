using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.Views;

/// <summary>
/// Backs up the database, a schema or a table with pg_dump (see
/// <see cref="BackupViewModel"/>). A window rather than an overlay because a
/// backup runs for minutes and the work behind it shouldn't stop: it is not
/// modal, and closing it while pg_dump runs asks first.
/// </summary>
public partial class BackupWindow : Window
{
    private bool _closingConfirmed;

    public BackupWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
        Opened += (_, _) => _ = Model?.LoadAsync();
        Closing += OnClosing;
        Closed += (_, _) => Model?.Dispose();
    }

    private BackupViewModel? Model => DataContext as BackupViewModel;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Closing with its main window (or the app's quit through it): the main
        // window asks one question for both (MainWindow.CloseQuestion) and
        // stops the backup itself, so asking here too would ask twice.
        if (_closingConfirmed || Model is not { IsRunning: true } model
            || (Owner is MainWindow && e.CloseReason != WindowCloseReason.WindowClosing))
        {
            return;
        }

        e.Cancel = true;
        var stop = await new ConfirmDialog(
            Strings.Backup_ConfirmStop,
            Strings.Backup_ConfirmStopButton).ShowDialog<bool>(this);
        if (!stop)
        {
            return;
        }

        await model.StopAndWaitAsync();
        _closingConfirmed = true;
        Close();
    }

    private async void OnChangeFileClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || StorageProvider is not { CanSave: true } storage)
        {
            return;
        }

        var archive = new FilePickerFileType(Strings.Backup_FilterArchive) { Patterns = ["*.dump"] };
        var script = new FilePickerFileType(Strings.Backup_FilterScript) { Patterns = ["*.sql"] };
        var isScript = string.Equals(Path.GetExtension(model.OutputPath), ".sql", StringComparison.OrdinalIgnoreCase);
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Backup_SaveDialogTitle,
            SuggestedFileName = Path.GetFileNameWithoutExtension(model.OutputPath),
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(model.FolderName),
            DefaultExtension = isScript ? "sql" : "dump",
            // The first choice is the one the dialog opens on.
            FileTypeChoices = isScript ? [script, archive] : [archive, script],
            ShowOverwritePrompt = true,
        });

        if (file?.TryGetLocalPath() is { } path)
        {
            model.OutputPath = path;
        }
    }

    private async void OnChooseToolsFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        if (await ChooseToolsFolderAsync(this) is { } folder)
        {
            await model.Tools.UseFolderAsync(folder);
        }
    }

    /// <summary>
    /// Asks for the folder holding pg_dump and pg_restore. Shared with the
    /// Settings page, which offers the same choice.
    /// </summary>
    public static async Task<string?> ChooseToolsFolderAsync(TopLevel owner)
    {
        if (owner.StorageProvider is not { CanPickFolder: true } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Backup_ChooseToolsFolderTitle,
            AllowMultiple = false,
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private async void OnCopyCommandClick(object? sender, RoutedEventArgs e) =>
        await CopyAsync(sender as Button, Model?.CommandPreview);

    private async void OnCopyLogClick(object? sender, RoutedEventArgs e) =>
        await CopyAsync(sender as Button, Model?.Log);

    private async Task CopyAsync(Button? button, string? text)
    {
        if (string.IsNullOrEmpty(text) || Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            if (button is not null)
            {
                CopyFeedback.Show(button);
            }
        }
        catch (Exception)
        {
            // Both texts are selectable too.
        }
    }

    private async void OnShowInFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Model?.SavedPath is not { } path || Path.GetDirectoryName(path) is not { } folder)
        {
            return;
        }

        try
        {
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        }
        catch (Exception)
        {
            // The path is on screen, selectable.
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
