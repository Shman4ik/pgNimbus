using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Backup;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.ViewModels;

/// <summary>Where the restore window is.</summary>
public enum RestoreWindowState
{
    /// <summary>No file yet.</summary>
    ChooseFile,

    /// <summary>Reading the file's table of contents.</summary>
    Inspecting,

    /// <summary>The file can't be restored; the window says why.</summary>
    Unusable,

    /// <summary>What the file holds, where it goes, and the Restore button.</summary>
    Ready,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// Backs the restore window: a pg_dump archive, shown for what it is before
/// anything runs (which database, which versions, when, how many tables), then
/// restored into a new database (the default, which touches nothing that
/// exists) or into the window's own, which asks first. Either way it is one
/// transaction: all of it or none.
/// </summary>
public sealed partial class RestoreViewModel : ObservableObject, IDisposable
{
    private readonly IRestoreService _service;
    private readonly Func<string, Task>? _openDatabase;
    private CancellationTokenSource? _run;
    private PgArchiveListing? _listing;
    private PgVersion? _server;
    private int _nameCheck;
    private string? _takenName;
    private string? _problemText;
    private PgArchiveFormat? _problemFormat;
    private string? _creatingDatabase;
    private RestoreProgress? _progress;
    private RestoreResult? _result;
    private RestorePlan? _resultPlan;
    private bool? _hintTunnelled;
    private (string Database, string Reason)? _openError;

    /// <param name="openDatabase">Opens a window on a database of this server, for after a restore into a new one; null hides the button.</param>
    public RestoreViewModel(
        IRestoreService service,
        string connectionLabel,
        PgToolStatusViewModel? tools = null,
        Func<string, Task>? openDatabase = null)
    {
        _service = service;
        _openDatabase = openDatabase;
        ConnectionLabel = connectionLabel;
        Tools = tools ?? new PgToolStatusViewModel(PgTool.PgRestore);
        Tools.Changed += OnToolsChanged;
        Localizer.WhenChanged(this, static vm => vm.OnLanguageChanged());
    }

    // What is words here is composed from what happened, so it is written again in the new language.
    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ReadingText));
        OnPropertyChanged(nameof(ArchiveSummary));
        OnPropertyChanged(nameof(ContentSummary));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(VersionWarning));
        OnPropertyChanged(nameof(CurrentDatabaseLabel));
        OnPropertyChanged(nameof(NameError));
        OnPropertyChanged(nameof(OwnersNote));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ResultTitle));
        OnPropertyChanged(nameof(ResultDetail));
        OnPropertyChanged(nameof(ResultHint));
    }

    public string Title => Strings.Restore_Title;

    /// <summary>The line under the heading: the connection the restore goes to.</summary>
    public string ConnectionLabel { get; }

    /// <summary>Which pg_restore will run, or how to get one.</summary>
    public PgToolStatusViewModel Tools { get; }

    /// <summary>
    /// Asks before a restore replaces what is in the window's own database;
    /// the view shows a confirm dialog. Null (the tests) answers yes.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmReplace { get; set; }

    /// <summary>Raised after a restore into the window's own database, whose schema tree is then out of date.</summary>
    public event Action? CurrentDatabaseChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosingFile), nameof(IsInspecting), nameof(IsUnusable), nameof(IsReady), nameof(IsRunning),
        nameof(IsFinished), nameof(IsSucceeded), nameof(IsFailed), nameof(ShowToolsMissing), nameof(ShowForm), nameof(CanOpenDatabase))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(OpenDatabaseCommand))]
    private RestoreWindowState _state = RestoreWindowState.ChooseFile;

    public bool IsChoosingFile => State == RestoreWindowState.ChooseFile && !Tools.IsMissing;

    public bool IsInspecting => State == RestoreWindowState.Inspecting;

    public bool IsUnusable => State == RestoreWindowState.Unusable;

    public bool IsReady => State == RestoreWindowState.Ready;

    public bool IsRunning => State == RestoreWindowState.Running;

    public bool IsFinished => State is RestoreWindowState.Succeeded or RestoreWindowState.Failed or RestoreWindowState.Cancelled;

    public bool IsSucceeded => State == RestoreWindowState.Succeeded;

    public bool IsFailed => State is RestoreWindowState.Failed or RestoreWindowState.Cancelled;

    /// <summary>The install steps replace everything else while no pg_restore is found.</summary>
    public bool ShowToolsMissing => Tools.IsMissing && State is RestoreWindowState.ChooseFile or RestoreWindowState.Ready or RestoreWindowState.Unusable;

    /// <summary>The summary and the choices.</summary>
    public bool ShowForm => IsReady && !Tools.IsMissing;

    // --- The file -----------------------------------------------------------

    /// <summary>The archive chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(ReadingText))]
    private string? _archivePath;

    public string FileName => ArchivePath is null ? "" : Path.GetFileName(ArchivePath);

    /// <summary>"Reading shop.dump…", while the file's contents are read.</summary>
    public string ReadingText => Strings.Restore_Reading(FileName);

    /// <summary>"Database shop · PostgreSQL 17.11 · saved 2026-10-10 09:32:12 by pg_dump 18.6".</summary>
    public string ArchiveSummary => _listing is null ? "" : Summarize(_listing);

    /// <summary>"5 tables, with their rows" or "5 tables, structure only".</summary>
    public string ContentSummary => _listing is not { } listing
        ? ""
        : listing.TableCount switch
        {
            0 => listing.HasRows ? Strings.Restore_ContentNoTablesLarge : Strings.Restore_ContentNoTables,
            var n => listing.HasRows ? Strings.Restore_ContentWithRows(n) : Strings.Restore_ContentStructure(n),
        };

    /// <summary>Why the file can't be restored.</summary>
    public string? Problem => _problemFormat is { } format && RestoreInspection.ProblemFor(format) is { } text ? text : _problemText;

    /// <summary>A backup from a newer PostgreSQL than this server.</summary>
    public string? VersionWarning => _listing?.DumpedFrom is { } from && _server is { } server && from.MajorKey > server.MajorKey
        ? Strings.Restore_VersionWarning(from.MajorLabel, server.MajorLabel)
        : null;

    // --- Where it goes ------------------------------------------------------

    /// <summary>Into a new database (true, the default) or the window's own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IntoCurrentDatabase))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _intoNewDatabase = true;

    public bool IntoCurrentDatabase
    {
        get => !IntoNewDatabase;
        set => IntoNewDatabase = !value;
    }

    /// <summary>The window's own database, as the second choice names it.</summary>
    public string CurrentDatabaseLabel => Strings.Restore_CurrentDatabase(_service.DatabaseName);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _newDatabaseName = "";

    /// <summary>Why the name can't be used: empty, too long, already taken.</summary>
    public string? NameError => RestorePlan.ValidateDatabaseName(NewDatabaseName) ?? (_takenName == NewDatabaseName ? Strings.Restore_NameTaken(NewDatabaseName) : null);

    private void SetNameTaken(string? name)
    {
        _takenName = name;
        OnPropertyChanged(nameof(NameError));
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnNewDatabaseNameChanged(string value)
    {
        OnPropertyChanged(nameof(NameError));
        _ = CheckNameAsync(value);
    }

    /// <summary>
    /// Keep the owners and permissions the backup names. Defaults to on only when
    /// every owner exists here: a missing one would stop the restore at its first
    /// <c>ALTER … OWNER TO</c>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnersNote))]
    private bool _keepOwners = true;

    /// <summary>The roles the backup names that this server doesn't have.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnersNote), nameof(HasMissingRoles))]
    private IReadOnlyList<string> _missingRoles = [];

    public bool HasMissingRoles => MissingRoles.Count > 0;

    /// <summary>What happens to ownership, in words under the check box.</summary>
    public string OwnersNote
    {
        get
        {
            var missing = string.Join(", ", MissingRoles);
            if (MissingRoles.Count > 0)
            {
                return KeepOwners
                    ? Strings.Restore_OwnersMissingKeep(MissingRoles.Count, missing)
                    : Strings.Restore_OwnersMissingDrop(MissingRoles.Count, missing);
            }

            return KeepOwners ? Strings.Restore_OwnersKept : Strings.Restore_OwnersNotKept;
        }
    }

    // --- Running and done -------------------------------------------------

    [ObservableProperty]
    private double _progressPercent;

    public string ProgressText => _progress is null
        ? _creatingDatabase is { } name ? Strings.Restore_ProgressCreatingDatabase(name) : Strings.Backup_ProgressConnecting
        : _progress.CurrentItem ?? (_progress.Stage == RestoreStage.Finishing ? Strings.Restore_ProgressFinishing : Strings.Backup_ProgressConnecting);

    [ObservableProperty]
    private string _progressDetail = "";

    public string ResultTitle => _result?.Outcome switch
    {
        null => "",
        RestoreOutcome.Succeeded => Strings.Restore_ResultRestored(_result.Database, BackupViewModel.ClockText(_result.Elapsed)),
        RestoreOutcome.Cancelled => Strings.Restore_ResultStopped,
        _ => Strings.Restore_ResultFailed,
    };

    public string ResultDetail => _result?.Outcome switch
    {
        null => "",
        RestoreOutcome.Succeeded => _restoredIntoNew ? Strings.Restore_ResultNew(_result.Database) : Strings.Restore_ResultRefreshed,
        RestoreOutcome.Cancelled => Unchanged(_result, _resultPlan!),
        _ => (_result.Error ?? Strings.Restore_FailedNoReason) + "\n" + Unchanged(_result, _resultPlan!),
    };

    /// <summary>
    /// What to do about the error, when it is a familiar one. One of this app's own
    /// sentences is written again in the language in use; any other is kept as the
    /// service wrote it.
    /// </summary>
    public string? ResultHint => _openError is { } open
        ? Strings.Restore_OpenFailed(open.Database, open.Reason)
        : _result is not { Outcome: RestoreOutcome.Failed } result
            ? null
            : _hintTunnelled is { } tunnelled ? PgToolErrorHints.For(result.Error, PgTool.PgRestore, tunnelled) : result.Hint;

    [ObservableProperty]
    private string _log = "";

    [ObservableProperty]
    private bool _isLogShown;

    [RelayCommand]
    private void ToggleLogShown() => IsLogShown = !IsLogShown;

    /// <summary>The database the last successful restore went into.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenDatabase))]
    [NotifyCanExecuteChangedFor(nameof(OpenDatabaseCommand))]
    private string? _restoredDatabase;

    private bool _restoredIntoNew;

    /// <summary>Open in New Window: after a restore into a new database.</summary>
    public bool CanOpenDatabase => IsSucceeded && _restoredIntoNew && _openDatabase is not null && RestoredDatabase is not null;

    /// <summary>Reads the server's version and the search for pg_restore. Called when the window opens.</summary>
    public async Task LoadAsync()
    {
        try
        {
            _server = await Task.Run(() => _service.GetServerVersionAsync(CancellationToken.None));
        }
        catch (Exception)
        {
            // Only the version warning needs it.
        }

        await Tools.LoadAsync();
    }

    private void OnToolsChanged()
    {
        OnPropertyChanged(nameof(ShowToolsMissing));
        OnPropertyChanged(nameof(ShowForm));
        OnPropertyChanged(nameof(IsChoosingFile));
        StartCommand.NotifyCanExecuteChanged();
        if (Tools.IsReady && ArchivePath is { } path && State is RestoreWindowState.ChooseFile)
        {
            _ = InspectAsync(path);
        }
    }

    /// <summary>Takes a file the user chose and reads what it holds.</summary>
    public async Task InspectAsync(string path)
    {
        ArchivePath = path;
        if (Tools.Install is not { } tool)
        {
            // Read once pg_restore is found (OnToolsChanged).
            State = RestoreWindowState.ChooseFile;
            return;
        }

        State = RestoreWindowState.Inspecting;
        _problemFormat = null;
        _problemText = null;
        OnPropertyChanged(nameof(Problem));
        RestoreInspection inspection;
        try
        {
            inspection = await Task.Run(() => _service.InspectAsync(tool, path, CancellationToken.None));
        }
        catch (Exception ex)
        {
            inspection = new RestoreInspection(PgArchiveFormat.Unknown, null, Strings.Restore_ReadFailed(ex.Message));
        }

        if (!inspection.CanRestore || inspection.Listing is not { } listing)
        {
            if (inspection.Problem is not null && RestoreInspection.ProblemFor(inspection.Format) == inspection.Problem)
            {
                ShowProblem(path, inspection.Format);
            }
            else
            {
                ShowProblem(path, inspection.Problem ?? Strings.Restore_ReadFailedShort);
            }

            return;
        }

        IReadOnlyList<string> missing;
        try
        {
            missing = await Task.Run(() => _service.MissingRolesAsync(listing.Owners, CancellationToken.None));
        }
        catch (Exception)
        {
            missing = [];
        }

        string name;
        try
        {
            name = await Task.Run(() => _service.SuggestDatabaseNameAsync(listing.DatabaseName, CancellationToken.None));
        }
        catch (Exception)
        {
            name = (listing.DatabaseName ?? _service.DatabaseName) + "_restored";
        }

        ShowInspection(path, listing, missing, name);
    }

    /// <summary>Shows why <paramref name="path"/> can't be restored (also the scenarios' way in).</summary>
    public void ShowProblem(string path, string problem)
    {
        ArchivePath = path;
        _problemFormat = null;
        _problemText = problem;
        OnPropertyChanged(nameof(Problem));
        State = RestoreWindowState.Unusable;
    }

    /// <summary>Shows why a file of a kind that is never restored can't be (written again when the language changes).</summary>
    public void ShowProblem(string path, PgArchiveFormat format)
    {
        ArchivePath = path;
        _problemFormat = format;
        _problemText = null;
        OnPropertyChanged(nameof(Problem));
        State = RestoreWindowState.Unusable;
    }

    /// <summary>
    /// Shows what an archive holds and readies the choices: the owners kept only
    /// when every one exists here, and a new database name nothing uses yet.
    /// <see cref="InspectAsync"/> ends here; the screenshot scenarios start here,
    /// with a listing of their own and no server.
    /// </summary>
    public void ShowInspection(string path, PgArchiveListing listing, IReadOnlyList<string> missingRoles, string suggestedName)
    {
        ArchivePath = path;
        _problemFormat = null;
        _problemText = null;
        OnPropertyChanged(nameof(Problem));
        _listing = listing;
        OnPropertyChanged(nameof(ArchiveSummary));
        OnPropertyChanged(nameof(ContentSummary));
        OnPropertyChanged(nameof(VersionWarning));
        MissingRoles = missingRoles;
        KeepOwners = missingRoles.Count == 0;
        NewDatabaseName = suggestedName;
        State = RestoreWindowState.Ready;
    }

    /// <summary>"Database shop · PostgreSQL 17.11 · saved 2026-10-10 09:32:12 by pg_dump 18.6".</summary>
    public static string Summarize(PgArchiveListing listing)
    {
        var parts = new List<string>();
        if (listing.DatabaseName is { } database)
        {
            parts.Add(Strings.Restore_ArchiveDatabase(database));
        }

        if (listing.DumpedFrom is { } from)
        {
            parts.Add(Strings.Restore_ArchiveVersion(from.ToString()));
        }

        if (listing.CreatedAt is { } created)
        {
            parts.Add(listing.DumpedBy is { } by ? Strings.Restore_ArchiveSavedBy(created.ToString(), by.ToString()) : Strings.Restore_ArchiveSaved(created.ToString()));
        }

        return string.Join(" · ", parts);
    }

    private async Task CheckNameAsync(string name)
    {
        var check = ++_nameCheck;
        if (RestorePlan.ValidateDatabaseName(name) is not null)
        {
            SetNameTaken(null);
            return;
        }

        SetNameTaken(null);
        try
        {
            var exists = await Task.Run(() => _service.DatabaseExistsAsync(name, CancellationToken.None));
            if (check == _nameCheck)
            {
                SetNameTaken(exists ? name : null);
            }
        }
        catch (Exception)
        {
            // The restore says so if it is taken.
        }
    }

    private bool CanStart() =>
        State == RestoreWindowState.Ready
        && Tools.Install is not null
        && _listing is not null
        && (IntoCurrentDatabase || (NameError is null && RestorePlan.ValidateDatabaseName(NewDatabaseName) is null));

    /// <summary>Runs the restore; into the window's own database only after the user confirms.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (Tools.Install is not { } tool || _listing is not { } listing || ArchivePath is not { } path)
        {
            return;
        }

        if (IntoCurrentDatabase && ConfirmReplace is { } confirm
            && !await confirm(Strings.Restore_ConfirmReplace(_service.DatabaseName, _service.ServerLabel)))
        {
            return;
        }

        var plan = new RestorePlan(
            path,
            IntoNewDatabase ? RestoreTarget.NewDatabase : RestoreTarget.CurrentDatabase,
            IntoNewDatabase ? NewDatabaseName : _service.DatabaseName,
            KeepOwners);

        _run = new CancellationTokenSource();
        State = RestoreWindowState.Running;
        ProgressPercent = 0;
        _progress = null;
        _creatingDatabase = IntoNewDatabase ? plan.DatabaseName : null;
        OnPropertyChanged(nameof(ProgressText));
        ProgressDetail = "";

        var progress = new Progress<RestoreProgress>(ShowProgress);
        RestoreResult result;
        try
        {
            result = await Task.Run(() => _service.RunAsync(tool, plan, listing, progress, _run.Token));
        }
        catch (Exception ex)
        {
            result = new RestoreResult(RestoreOutcome.Failed, plan.DatabaseName, TimeSpan.Zero, ex.Message, null, "", false);
        }
        finally
        {
            _run.Dispose();
            _run = null;
        }

        ShowResult(result, plan);
    }

    private bool CanStop() => State == RestoreWindowState.Running;

    /// <summary>Stops pg_restore; nothing it did stays (one transaction), and a database it created is removed.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _run?.Cancel();

    /// <summary>Stops a running restore and waits for it to end, for closing the window.</summary>
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

    /// <summary>Back to the choices after a failure or a stop.</summary>
    [RelayCommand]
    private void TryAgain()
    {
        State = RestoreWindowState.Ready;
        _ = CheckNameAsync(NewDatabaseName);
    }

    [RelayCommand(CanExecute = nameof(CanOpenDatabase))]
    private async Task OpenDatabaseAsync()
    {
        if (_openDatabase is { } open && RestoredDatabase is { } database)
        {
            try
            {
                await open(database);
            }
            catch (Exception ex)
            {
                _openError = (database, ex.Message);
                OnPropertyChanged(nameof(ResultHint));
            }
        }
    }

    /// <summary>Shows how far a running restore is (the service's progress reports, and the screenshot scenarios).</summary>
    public void ShowProgress(RestoreProgress progress)
    {
        if (State != RestoreWindowState.Running)
        {
            return;
        }

        ProgressPercent = (progress.Fraction ?? 0) * 100;
        _progress = progress;
        OnPropertyChanged(nameof(ProgressText));
        ProgressDetail = BackupViewModel.ClockText(progress.Elapsed);
    }

    /// <summary>
    /// Shows how a restore ended. Production comes here from the Restore button;
    /// the screenshot scenarios call it with a result of their own.
    /// </summary>
    public void ShowResult(RestoreResult result, RestorePlan plan)
    {
        Log = result.Log;
        IsLogShown = false;
        _result = result;
        _resultPlan = plan;
        _openError = null;
        // A hint made by PgToolErrorHints is words, so it is written again when the language changes.
        // Which of its two readings (through a tunnel or not) made it is told by what it equals.
        _hintTunnelled = null;
        if (result.Hint is not null)
        {
            foreach (var tunnelled in new[] { false, true })
            {
                if (PgToolErrorHints.For(result.Error, PgTool.PgRestore, tunnelled) == result.Hint)
                {
                    _hintTunnelled = tunnelled;
                    break;
                }
            }
        }

        if (result.Outcome == RestoreOutcome.Succeeded)
        {
            _restoredIntoNew = plan.Target == RestoreTarget.NewDatabase;
            RestoredDatabase = result.Database;
        }

        OnPropertyChanged(nameof(ResultTitle));
        OnPropertyChanged(nameof(ResultDetail));
        OnPropertyChanged(nameof(ResultHint));
        switch (result.Outcome)
        {
            case RestoreOutcome.Succeeded:
                State = RestoreWindowState.Succeeded;
                if (!_restoredIntoNew)
                {
                    CurrentDatabaseChanged?.Invoke();
                }

                break;
            case RestoreOutcome.Cancelled:
                State = RestoreWindowState.Cancelled;
                break;
            default:
                State = RestoreWindowState.Failed;
                break;
        }
    }

    private static string Unchanged(RestoreResult result, RestorePlan plan) => plan.Target switch
    {
        RestoreTarget.NewDatabase when result.CreatedDatabaseRemoved => Strings.Restore_UnchangedRemoved(plan.DatabaseName),
        RestoreTarget.NewDatabase => Strings.Restore_UnchangedMaybeCreated(plan.DatabaseName),
        _ => Strings.Restore_UnchangedCurrent(plan.DatabaseName),
    };

    public void Dispose()
    {
        Tools.Changed -= OnToolsChanged;
        _run?.Cancel();
    }
}

