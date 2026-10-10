using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.App.Platform;
using PgNimbus.Core.Backup;

namespace PgNimbus.App.ViewModels;

/// <summary>Where the search for the PostgreSQL client programs stands.</summary>
public enum PgToolState
{
    Searching,
    Ready,
    Missing,
}

/// <summary>One install step as the window shows it: the text, a command to copy, a page to open.</summary>
public sealed class PgToolGuideStepViewModel(PgToolGuideStep step, int number)
{
    public string Number { get; } = number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Text => step.Text;

    public string? Command => step.Command;

    public string? LinkUrl => step.LinkUrl;

    public string? LinkText => step.LinkText;

    public bool HasCommand => step.Command is not null;

    public bool HasLink => step.LinkUrl is not null;
}

/// <summary>
/// Which pg_dump (or pg_restore) a window will run, and, when none fits, why
/// not and how to get one: the line under the backup form, the screen that
/// replaces the form when the programs are missing, and the Settings card.
/// The user asked for exact instructions when nothing is found, so a missing
/// install is never just "not found": it names the version needed, what was
/// found instead, and the steps for this platform.
/// </summary>
public sealed partial class PgToolStatusViewModel : ObservableObject
{
    private readonly PgTool _tool;
    private readonly Func<bool, Task<PgToolScan>> _scan;
    private readonly Func<string?, Task<PgToolScan>> _setFolder;
    private readonly Func<PgVersion?, IReadOnlyList<PgToolGuideStep>> _guide;
    private PgToolScan? _lastScan;

    /// <param name="tool">The program the owner runs.</param>
    /// <param name="scan">The search (true: search again); <see cref="PgToolCatalog"/> in the app.</param>
    /// <param name="setFolder">Points the search at a folder (null: back to searching).</param>
    /// <param name="guide">
    /// The install steps for a server version; this machine's in the app, one
    /// platform's in the screenshots, so a frame doesn't depend on the OS
    /// rendering it.
    /// </param>
    public PgToolStatusViewModel(
        PgTool tool,
        Func<bool, Task<PgToolScan>>? scan = null,
        Func<string?, Task<PgToolScan>>? setFolder = null,
        Func<PgVersion?, IReadOnlyList<PgToolGuideStep>>? guide = null)
    {
        _tool = tool;
        _scan = scan ?? (refresh => refresh ? PgToolCatalog.RescanAsync() : PgToolCatalog.GetAsync());
        _setFolder = setFolder ?? PgToolCatalog.SetConfiguredDirectory;
        _guide = guide ?? PgToolInstallGuide.ForThisMachine;
    }

    /// <summary>
    /// The server's release, which decides the oldest pg_dump that can back it
    /// up. Null where there is no server to fit (Settings, and pg_restore,
    /// which any release can run).
    /// </summary>
    public PgVersion? Server
    {
        get;
        set
        {
            field = value;
            if (_lastScan is { } scan)
            {
                Apply(scan);
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(IsReady), nameof(IsMissing))]
    private PgToolState _state = PgToolState.Searching;

    public bool IsSearching => State == PgToolState.Searching;

    public bool IsReady => State == PgToolState.Ready;

    public bool IsMissing => State == PgToolState.Missing;

    /// <summary>The install that will run, when there is one.</summary>
    [ObservableProperty]
    private PgToolInstall? _install;

    /// <summary>"pg_dump 18.6 from pgAdmin 4 · C:\…\runtime", or what is being done.</summary>
    [ObservableProperty]
    private string _statusLine = "";

    /// <summary>The missing screen's heading: "pgNimbus needs pg_dump 17 or newer".</summary>
    [ObservableProperty]
    private string _headline = "";

    /// <summary>The paragraph under it: what was found, and why it doesn't do.</summary>
    [ObservableProperty]
    private string _explanation = "";

    /// <summary>The install steps for this machine.</summary>
    public ObservableCollection<PgToolGuideStepViewModel> Steps { get; } = [];

    /// <summary>True when Settings points at a folder instead of searching.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderLabel))]
    private string? _configuredDirectory;

    public bool IsConfigured => ConfiguredDirectory is not null;

    /// <summary>The Settings card's folder line.</summary>
    public string FolderLabel => ConfiguredDirectory is { } folder
        ? $"From the folder you chose: {folder}"
        : "Found automatically";

    /// <summary>Raised when <see cref="Install"/> may have changed, so the owner can re-check what it can run.</summary>
    public event Action? Changed;

    /// <summary>Reads the current search (the app's, shared by every window).</summary>
    public async Task LoadAsync(bool refresh = false)
    {
        State = PgToolState.Searching;
        StatusLine = $"Looking for {PgToolInstall.ToolName(_tool)}…";
        Changed?.Invoke();
        Apply(await _scan(refresh));
    }

    [RelayCommand]
    private Task LookAgain() => LoadAsync(refresh: true);

    /// <summary>Points the search at a folder the user picked (Choose Folder…).</summary>
    public async Task UseFolderAsync(string? directory)
    {
        State = PgToolState.Searching;
        StatusLine = $"Looking for {PgToolInstall.ToolName(_tool)}…";
        Changed?.Invoke();
        Apply(await _setFolder(directory));
    }

    [RelayCommand]
    private Task UseAutomaticSearch() => UseFolderAsync(null);

    /// <summary>Applies a finished search.</summary>
    public void Apply(PgToolScan scan)
    {
        _lastScan = scan;
        ConfiguredDirectory = scan.ConfiguredDirectory;
        OnPropertyChanged(nameof(IsConfigured));
        var name = PgToolInstall.ToolName(_tool);
        var install = _tool == PgTool.PgDump && Server is { } server ? scan.For(server) : scan.Newest;
        Install = install;

        if (install is not null)
        {
            State = PgToolState.Ready;
            StatusLine = install.Describe(_tool);
            Headline = "";
            Explanation = "";
            Steps.Clear();
            Changed?.Invoke();
            return;
        }

        State = PgToolState.Missing;
        var needed = _tool == PgTool.PgDump && Server is { } s ? $"{name} {s.MajorLabel} or newer" : name;
        Headline = $"pgNimbus needs {needed}";
        StatusLine = $"{name} not found";
        Explanation = Explain(scan);

        var steps = _guide(_tool == PgTool.PgDump ? Server : null);
        Steps.Clear();
        for (var i = 0; i < steps.Count; i++)
        {
            Steps.Add(new PgToolGuideStepViewModel(steps[i], i + 1));
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// What was found and why it doesn't do, in the words the missing screen
    /// shows. Public for the tests, which hold each case to its sentence.
    /// </summary>
    public string Explain(PgToolScan scan)
    {
        var name = PgToolInstall.ToolName(_tool);
        var what = _tool == PgTool.PgDump
            ? "Backups are made by pg_dump, PostgreSQL's own backup program, which comes with pg_restore."
            : "Backups are restored by pg_restore, PostgreSQL's own restore program, which comes with pg_dump.";
        var parts = new List<string> { what };

        if (scan.ConfiguredDirectory is { } folder)
        {
            parts.Add(scan.Problems.FirstOrDefault() is { } problem
                ? $"Settings point pgNimbus at {folder}, but {problem.Reason}."
                : $"Settings point pgNimbus at {folder}.");
        }

        if (_tool == PgTool.PgDump && Server is { } server && scan.Newest is { } newest)
        {
            parts.Add($"The newest {name} on this computer is {newest.Version}, in {newest.Directory}. "
                      + $"This server runs PostgreSQL {server}, and {name} has to be {server.MajorLabel} or newer to back it up.");
        }
        else if (scan.ConfiguredDirectory is null)
        {
            parts.Add($"pgNimbus didn't find {name} on this computer.");
        }

        foreach (var problem in scan.ConfiguredDirectory is null ? scan.Problems.Take(3) : [])
        {
            parts.Add($"There's a {name} in {problem.Directory}, but {problem.Reason}.");
        }

        // One thought per line: what the program is, what was found, what's wrong with it.
        return string.Join("\n", parts);
    }
}
