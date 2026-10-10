using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Backup;

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
    }

    public string Title => "Restore a backup";

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
    [NotifyPropertyChangedFor(nameof(FileName))]
    private string? _archivePath;

    public string FileName => ArchivePath is null ? "" : Path.GetFileName(ArchivePath);

    /// <summary>"Database shop · PostgreSQL 17.11 · saved 2026-10-10 09:32:12 by pg_dump 18.6".</summary>
    [ObservableProperty]
    private string _archiveSummary = "";

    /// <summary>"5 tables, with their rows" or "5 tables, structure only".</summary>
    [ObservableProperty]
    private string _contentSummary = "";

    /// <summary>Why the file can't be restored.</summary>
    [ObservableProperty]
    private string? _problem;

    /// <summary>A backup from a newer PostgreSQL than this server.</summary>
    [ObservableProperty]
    private string? _versionWarning;

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
    public string CurrentDatabaseLabel => $"This database: {_service.DatabaseName}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _newDatabaseName = "";

    /// <summary>Why the name can't be used: empty, too long, already taken.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string? _nameError;

    partial void OnNewDatabaseNameChanged(string value) => _ = CheckNameAsync(value);

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
                var roles = MissingRoles.Count == 1 ? $"Role {missing} doesn't" : $"Roles {missing} don't";
                return KeepOwners
                    ? $"{roles} exist on this server, so keeping owners will stop the restore."
                    : $"{roles} exist on this server, so you'll own everything restored.";
            }

            return KeepOwners
                ? "Objects keep their owners and permissions from the backup."
                : "You'll own everything restored, and the backup's permissions are left out.";
        }
    }

    // --- Running and done -------------------------------------------------

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _progressDetail = "";

    [ObservableProperty]
    private string _resultTitle = "";

    [ObservableProperty]
    private string _resultDetail = "";

    [ObservableProperty]
    private string? _resultHint;

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
        Problem = null;
        RestoreInspection inspection;
        try
        {
            inspection = await Task.Run(() => _service.InspectAsync(tool, path, CancellationToken.None));
        }
        catch (Exception ex)
        {
            inspection = new RestoreInspection(PgArchiveFormat.Unknown, null, $"The file couldn't be read: {ex.Message}");
        }

        if (!inspection.CanRestore || inspection.Listing is not { } listing)
        {
            ShowProblem(path, inspection.Problem ?? "The file couldn't be read.");
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
        Problem = problem;
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
        Problem = null;
        _listing = listing;
        ArchiveSummary = Summarize(listing);
        ContentSummary = listing.TableCount switch
        {
            0 => listing.HasRows ? "No tables, and large objects" : "No tables",
            1 => listing.HasRows ? "1 table, with its rows" : "1 table, structure only",
            var n => listing.HasRows ? $"{n} tables, with their rows" : $"{n} tables, structure only",
        };
        VersionWarning = listing.DumpedFrom is { } from && _server is { } server && from.MajorKey > server.MajorKey
            ? $"This backup is from PostgreSQL {from.MajorLabel}, and this server runs {server.MajorLabel}. Something the newer release supports can stop the restore."
            : null;
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
            parts.Add($"Database {database}");
        }

        if (listing.DumpedFrom is { } from)
        {
            parts.Add($"PostgreSQL {from}");
        }

        if (listing.CreatedAt is { } created)
        {
            parts.Add(listing.DumpedBy is { } by ? $"saved {created} by pg_dump {by}" : $"saved {created}");
        }

        return string.Join(" · ", parts);
    }

    private async Task CheckNameAsync(string name)
    {
        var check = ++_nameCheck;
        if (RestorePlan.ValidateDatabaseName(name) is { } invalid)
        {
            NameError = invalid;
            return;
        }

        NameError = null;
        try
        {
            var exists = await Task.Run(() => _service.DatabaseExistsAsync(name, CancellationToken.None));
            if (check == _nameCheck)
            {
                NameError = exists ? $"There's already a database called {name}." : null;
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
            && !await confirm($"Restore into \"{_service.DatabaseName}\" on {_service.ServerLabel}? What the backup holds is dropped and created "
                              + "again from it, rows included. Other objects in the database stay. It happens in one transaction, but once it's done it can't be undone."))
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
        ProgressText = IntoNewDatabase ? $"Creating database {plan.DatabaseName}…" : "Connecting…";
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
                ResultHint = $"Couldn't open {database}: {ex.Message}";
            }
        }
    }

    private void ShowProgress(RestoreProgress progress)
    {
        if (State != RestoreWindowState.Running)
        {
            return;
        }

        ProgressPercent = (progress.Fraction ?? 0) * 100;
        ProgressText = progress.CurrentItem ?? progress.Stage switch
        {
            RestoreStage.Finishing => "Finishing…",
            _ => "Connecting…",
        };
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
        switch (result.Outcome)
        {
            case RestoreOutcome.Succeeded:
                _restoredIntoNew = plan.Target == RestoreTarget.NewDatabase;
                RestoredDatabase = result.Database;
                ResultTitle = $"Restored into {result.Database} in {BackupViewModel.ClockText(result.Elapsed)}";
                ResultDetail = _restoredIntoNew
                    ? $"{result.Database} is a new database on this server."
                    : "The schema tree has been refreshed.";
                ResultHint = null;
                State = RestoreWindowState.Succeeded;
                if (!_restoredIntoNew)
                {
                    CurrentDatabaseChanged?.Invoke();
                }

                break;
            case RestoreOutcome.Cancelled:
                ResultTitle = "Restore stopped";
                ResultDetail = Unchanged(result, plan);
                ResultHint = null;
                State = RestoreWindowState.Cancelled;
                break;
            default:
                ResultTitle = "Restore failed";
                ResultDetail = (result.Error ?? "pg_restore stopped without saying why.") + "\n" + Unchanged(result, plan);
                ResultHint = result.Hint;
                State = RestoreWindowState.Failed;
                break;
        }
    }

    private static string Unchanged(RestoreResult result, RestorePlan plan) => plan.Target switch
    {
        RestoreTarget.NewDatabase when result.CreatedDatabaseRemoved => $"Nothing was restored, and the new database {plan.DatabaseName} was removed again.",
        RestoreTarget.NewDatabase => $"Nothing was restored. If database {plan.DatabaseName} was created, it is empty; drop it when you like.",
        _ => $"Nothing was changed: the restore runs as one transaction, and {plan.DatabaseName} is as it was.",
    };

    public void Dispose()
    {
        Tools.Changed -= OnToolsChanged;
        _run?.Cancel();
    }
}

