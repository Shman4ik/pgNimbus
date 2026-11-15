using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core;
using PgNimbus.Core.Backup;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.ViewModels;

/// <summary>Where the backup window is.</summary>
public enum BackupWindowState
{
    /// <summary>The form: where to save, and what.</summary>
    Setup,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// Backs the backup window: two choices (the file, and everything or the
/// structure only), then progress, then where it went. The format follows the
/// file name, so the form needs no third choice: <c>.dump</c> is pg_dump's
/// archive, <c>.sql</c> a script.
/// </summary>
public sealed partial class BackupViewModel : ObservableObject, IDisposable
{
    private readonly IBackupService _service;
    private readonly Action<string?>? _persistFolder;
    private readonly Func<DateTime> _now;
    private CancellationTokenSource? _run;
    private BackupProgress? _progress;
    private BackupResult? _result;
    private bool _stoppedOverFile;
    private bool? _hintTunnelled;
    private string? _setupReason;

    public BackupViewModel(
        IBackupService service,
        BackupScope scope,
        string connectionLabel,
        string? lastFolder,
        Action<string?>? persistFolder = null,
        PgToolStatusViewModel? tools = null,
        Func<DateTime>? now = null)
    {
        _service = service;
        Scope = scope;
        ConnectionLabel = connectionLabel;
        _persistFolder = persistFolder;
        _now = now ?? (() => DateTime.Now);
        Tools = tools ?? new PgToolStatusViewModel(PgTool.PgDump);
        Tools.Changed += OnToolsChanged;
        Localizer.WhenChanged(this, static vm => vm.OnLanguageChanged());
        var folder = !string.IsNullOrWhiteSpace(lastFolder) && Directory.Exists(lastFolder)
            ? lastFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _outputPath = Path.Combine(folder, scope.SuggestedFileName(service.DatabaseName, _now()) + ".dump");
    }

    /// <summary>What this window backs up.</summary>
    public BackupScope Scope { get; }

    /// <summary>The window's heading: "Back up shop", "Back up schema sales", "Back up table sales.orders".</summary>
    public string Title => Scope.Kind switch
    {
        BackupScopeKind.Schema => Strings.Backup_TitleSchema(Scope.Schema ?? ""),
        BackupScopeKind.Table => Strings.Backup_TitleTable($"{Scope.Schema}.{Scope.Table}"),
        _ => Strings.Backup_TitleDatabase(_service.DatabaseName),
    };

    // Everything below that is words is composed from what happened, so it is written again in the new language.
    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FormatHint));
        OnPropertyChanged(nameof(SetupError));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ResultTitle));
        OnPropertyChanged(nameof(ResultDetail));
        OnPropertyChanged(nameof(ResultHint));
    }

    /// <summary>The line under the heading: the connection the backup reads (its profile and host).</summary>
    public string ConnectionLabel { get; }

    /// <summary>Which pg_dump will run, or how to get one.</summary>
    public PgToolStatusViewModel Tools { get; }

    /// <summary>The form shows once a pg_dump that fits is found.</summary>
    public bool ShowForm => IsSetup && Tools.IsReady;

    /// <summary>The install steps replace the form while no pg_dump fits.</summary>
    public bool ShowToolsMissing => IsSetup && Tools.IsMissing;

    /// <summary>The search is still running.</summary>
    public bool ShowSearching => IsSetup && Tools.IsSearching;

    /// <summary>Settings point at a folder that doesn't do: offer the way back to searching.</summary>
    public bool ShowUseAutomaticSearch => IsSetup && Tools.IsMissing && Tools.IsConfigured;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatHint), nameof(CommandPreview), nameof(FileName), nameof(FolderName))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _outputPath;

    public string FileName => Path.GetFileName(OutputPath);

    public string FolderName => Path.GetDirectoryName(OutputPath) ?? "";

    /// <summary>Everything (false) or the structure alone (true).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Everything), nameof(CommandPreview))]
    private bool _structureOnly;

    /// <summary>The other radio button of the pair.</summary>
    public bool Everything
    {
        get => !StructureOnly;
        set => StructureOnly = !value;
    }

    /// <summary>What the chosen file will be, under the file name.</summary>
    public string FormatHint => BackupPlan.FormatFor(OutputPath) == BackupFormat.SqlScript
        ? Strings.Backup_FormatHintScript
        : Strings.Backup_FormatHintArchive;

    /// <summary>
    /// The pg_dump command line, without the password (which never is an
    /// argument). Empty until a pg_dump has been found.
    /// </summary>
    public string CommandPreview => Tools.Install is { } install ? _service.Preview(install, Plan) : "";

    /// <summary>The backup as the form stands.</summary>
    public BackupPlan Plan => new(Scope, StructureOnly ? BackupContent.StructureOnly : BackupContent.Everything, OutputPath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetup), nameof(IsRunning), nameof(IsFinished), nameof(IsSucceeded), nameof(IsFailed),
        nameof(ShowForm), nameof(ShowToolsMissing), nameof(ShowSearching), nameof(ShowUseAutomaticSearch))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private BackupWindowState _state = BackupWindowState.Setup;

    public bool IsSetup => State == BackupWindowState.Setup;

    public bool IsRunning => State == BackupWindowState.Running;

    public bool IsFinished => State is BackupWindowState.Succeeded or BackupWindowState.Failed or BackupWindowState.Cancelled;

    public bool IsSucceeded => State == BackupWindowState.Succeeded;

    public bool IsFailed => State is BackupWindowState.Failed or BackupWindowState.Cancelled;

    /// <summary>Why the form can't run yet (the server's version couldn't be read), or null.</summary>
    public string? SetupError => _setupReason is null ? null : Strings.Backup_SetupErrorVersion(_setupReason);

    private void SetSetupReason(string? reason)
    {
        _setupReason = reason;
        OnPropertyChanged(nameof(SetupError));
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Whether the pg_dump command line is shown under the form.</summary>
    [ObservableProperty]
    private bool _isCommandShown;

    [RelayCommand]
    private void ToggleCommandShown() => IsCommandShown = !IsCommandShown;

    /// <summary>Whether pg_dump's log is shown under a result.</summary>
    [ObservableProperty]
    private bool _isLogShown;

    [RelayCommand]
    private void ToggleLogShown() => IsLogShown = !IsLogShown;

    // --- Progress ---------------------------------------------------------

    /// <summary>0 to 100.</summary>
    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _progressIsIndeterminate = true;

    /// <summary>"Saving sales.orders · table 23 of 41".</summary>
    public string ProgressText => _progress switch
    {
        null => Strings.Backup_ProgressConnecting,
        { Stage: BackupStage.SavingData, CurrentTable: { } table, TablesTotal: > 0 } progress =>
            Strings.Backup_ProgressSavingTable(table, Math.Min(progress.TablesDone + 1, progress.TablesTotal), progress.TablesTotal),
        { Stage: BackupStage.SavingData, CurrentTable: { } table } => Strings.Backup_ProgressSaving(table),
        { Stage: BackupStage.SavingStructure } => Strings.Backup_ProgressStructure,
        { Stage: BackupStage.Finishing } => Strings.Backup_ProgressFinishing,
        _ => Strings.Backup_ProgressReading,
    };

    /// <summary>"1:12 · 184 MB".</summary>
    [ObservableProperty]
    private string _progressDetail = "";

    // --- Result -----------------------------------------------------------

    /// <summary>"Saved 48.2 MB in 0:42", or what went wrong.</summary>
    public string ResultTitle => _result?.Outcome switch
    {
        null => "",
        BackupOutcome.Succeeded => Strings.Backup_ResultSaved(ByteSize.Format(_result.Bytes), ClockText(_result.Elapsed)),
        BackupOutcome.Cancelled => Strings.Backup_ResultStopped,
        _ => Strings.Backup_ResultFailed,
    };

    /// <summary>The line under it: where the file is, or pg_dump's error.</summary>
    public string ResultDetail => _result?.Outcome switch
    {
        null => "",
        BackupOutcome.Succeeded => _result.OutputPath,
        BackupOutcome.Cancelled => _stoppedOverFile ? Strings.Backup_StoppedKept : Strings.Backup_StoppedNothing,
        _ => _result.Error ?? Strings.Backup_FailedNoReason,
    };

    /// <summary>
    /// What to do about the error, when it is a familiar one. One of this app's own
    /// sentences is written again in the language in use; any other is kept as the
    /// service wrote it.
    /// </summary>
    public string? ResultHint => _result is not { Outcome: not BackupOutcome.Succeeded and not BackupOutcome.Cancelled } result
        ? null
        : _hintTunnelled is { } tunnelled ? PgToolErrorHints.For(result.Error, PgTool.PgDump, tunnelled) : result.Hint;

    /// <summary>Everything pg_dump said, for the log under a result.</summary>
    [ObservableProperty]
    private string _log = "";

    /// <summary>The file the last successful backup wrote.</summary>
    public string? SavedPath { get; private set; }

    /// <summary>
    /// Reads the server's version (which decides the pg_dump needed) and the
    /// app's search for pg_dump. Called when the window opens.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            Tools.Server = await Task.Run(() => _service.GetServerVersionAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            SetSetupReason(ex.Message);
        }

        await Tools.LoadAsync();
    }

    private void OnToolsChanged()
    {
        OnPropertyChanged(nameof(CommandPreview));
        OnPropertyChanged(nameof(ShowForm));
        OnPropertyChanged(nameof(ShowToolsMissing));
        OnPropertyChanged(nameof(ShowSearching));
        OnPropertyChanged(nameof(ShowUseAutomaticSearch));
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() =>
        State is BackupWindowState.Setup && Tools.Install is not null && SetupError is null && !string.IsNullOrWhiteSpace(OutputPath);

    /// <summary>Runs the backup.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (Tools.Install is not { } tool)
        {
            return;
        }

        var plan = Plan;
        _run = new CancellationTokenSource();
        State = BackupWindowState.Running;
        ProgressPercent = 0;
        ProgressIsIndeterminate = true;
        _progress = null;
        OnPropertyChanged(nameof(ProgressText));
        ProgressDetail = "";
        _persistFolder?.Invoke(Path.GetDirectoryName(plan.OutputPath));

        var progress = new Progress<BackupProgress>(ShowProgress);
        BackupResult result;
        try
        {
            result = await Task.Run(() => _service.RunAsync(tool, plan, progress, _run.Token));
        }
        catch (Exception ex)
        {
            result = new BackupResult(BackupOutcome.Failed, plan.OutputPath, 0, TimeSpan.Zero, 0, ex.Message, null, "");
        }
        finally
        {
            _run.Dispose();
            _run = null;
        }

        ShowResult(result);
    }

    private bool CanStop() => State == BackupWindowState.Running;

    /// <summary>Stops pg_dump; the unfinished file is deleted and an older one at the same path stays.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _run?.Cancel();

    /// <summary>Stops a running backup and waits for it to end, for the window and the main window closing.</summary>
    public async Task StopAndWaitAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        var ended = new TaskCompletionSource();
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(State) && !IsRunning)
            {
                ended.TrySetResult();
            }
        }

        PropertyChanged += OnChanged;
        try
        {
            _run?.Cancel();
            if (IsRunning)
            {
                await ended.Task;
            }
        }
        finally
        {
            PropertyChanged -= OnChanged;
        }
    }

    /// <summary>Back to the form after a failure or a stop, with the same choices.</summary>
    [RelayCommand]
    private void TryAgain() => State = BackupWindowState.Setup;

    /// <summary>Back to the form for another backup, with a fresh file name in the same folder.</summary>
    [RelayCommand]
    private void BackUpAgain()
    {
        var folder = Path.GetDirectoryName(OutputPath) ?? "";
        var extension = Path.GetExtension(OutputPath);
        OutputPath = Path.Combine(folder, Scope.SuggestedFileName(_service.DatabaseName, _now()) + (extension.Length == 0 ? ".dump" : extension));
        State = BackupWindowState.Setup;
    }

    /// <summary>Shows how far a running backup is (the service's progress reports, and the screenshot scenarios).</summary>
    public void ShowProgress(BackupProgress progress)
    {
        if (State != BackupWindowState.Running)
        {
            return;
        }

        ProgressIsIndeterminate = progress.Fraction is null;
        ProgressPercent = (progress.Fraction ?? 0) * 100;
        _progress = progress;
        OnPropertyChanged(nameof(ProgressText));
        ProgressDetail = $"{ClockText(progress.Elapsed)} · {ByteSize.Format(progress.BytesWritten)}";
    }

    /// <summary>Shows how a backup ended (the service's result, and the screenshot scenarios).</summary>
    public void ShowResult(BackupResult result)
    {
        Log = result.Log;
        _result = result;
        _stoppedOverFile = result.Outcome == BackupOutcome.Cancelled && File.Exists(result.OutputPath);
        // A hint made by PgToolErrorHints is words, so it is written again when the language changes.
        // Which of its two readings (through a tunnel or not) made it is told by what it equals.
        _hintTunnelled = null;
        if (result.Hint is not null)
        {
            foreach (var tunnelled in new[] { false, true })
            {
                if (PgToolErrorHints.For(result.Error, PgTool.PgDump, tunnelled) == result.Hint)
                {
                    _hintTunnelled = tunnelled;
                    break;
                }
            }
        }

        switch (result.Outcome)
        {
            case BackupOutcome.Succeeded:
                SavedPath = result.OutputPath;
                State = BackupWindowState.Succeeded;
                break;
            case BackupOutcome.Cancelled:
                State = BackupWindowState.Cancelled;
                break;
            default:
                State = BackupWindowState.Failed;
                break;
        }

        OnPropertyChanged(nameof(ResultTitle));
        OnPropertyChanged(nameof(ResultDetail));
        OnPropertyChanged(nameof(ResultHint));
    }

    /// <summary>"0:42", "12:05", "1:02:05".</summary>
    public static string ClockText(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        Tools.Changed -= OnToolsChanged;
        _run?.Cancel();
    }
}
