using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Nimbus.Ui.Fonts;
using PgNimbus.App.Platform;
using PgNimbus.App.Views;
using PgNimbus.Core.Commands;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// One row of the code font list: a family by name, or the bundled face (<see cref="Name"/>
/// null, which is what <c>AppSettings.CodeFont</c> stores for it). <see cref="Family"/> is
/// the face the row draws its own name in.
/// </summary>
public sealed record CodeFontOption(string? Name)
{
    /// <summary>The bundled JetBrains Mono NL.</summary>
    public static CodeFontOption Bundled { get; } = new((string?)null);

    /// <summary>What the list shows.</summary>
    public string Label => Name ?? "JetBrains Mono (built in)";

    /// <summary>The face the row is drawn in, so the list previews each one.</summary>
    public FontFamily Family => NimbusFonts.Mono(Name);
}

/// <summary>
/// Backs the preferences window. Every change applies immediately and persists
/// through the same per-setting App helpers the inline toggles use, so the
/// page and the rest of the UI can't disagree.
/// </summary>
public sealed partial class PreferencesViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    /// <summary>0 = system (follow the OS), 1 = light, 2 = dark.</summary>
    [ObservableProperty]
    private int _themeIndex;

    /// <summary>0 = auto (Cmd on macOS, Ctrl elsewhere), 1 = always Ctrl, 2 = always Cmd.</summary>
    [ObservableProperty]
    private int _hotkeySchemeIndex;

    /// <summary>Skip the connection dialog at startup and open the last-used connection outright.</summary>
    [ObservableProperty]
    private bool _autoConnectLastProfile;

    /// <summary>0 = the system face, 1 = Inter. Opens on what "auto" means on this platform.</summary>
    [ObservableProperty]
    private int _interfaceFontIndex;

    /// <summary>The code font choices: the bundled face first, then the installed monospace families once they are read.</summary>
    public RangeObservableCollection<CodeFontOption> CodeFonts { get; } = [];

    /// <summary>The chosen code font. Never null once the page is built.</summary>
    [ObservableProperty]
    private CodeFontOption? _selectedCodeFont;

    /// <summary>The SQL editor's font size (a <c>decimal</c> for <c>NumericUpDown</c>).</summary>
    [ObservableProperty]
    private decimal? _editorFontSize;

    // Set while the list is rebuilt: the ComboBox reports a selection change for
    // the old item leaving, which is not a choice.
    private bool _loadingCodeFonts;

    public PreferencesViewModel(MainViewModel main)
    {
        _main = main;
        var settings = App.LoadSettings();
        _themeIndex = settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        _hotkeySchemeIndex = settings.HotkeyScheme switch { "windows" => 1, "mac" => 2, _ => 0 };
        _autoConnectLastProfile = settings.AutoConnectLastProfile;
        _interfaceFontIndex = App.InterfaceFontFromString(settings.InterfaceFont) == InterfaceFont.System ? 0 : 1;
        _editorFontSize = (decimal)QueryEditorPanel.ClampEditorFontSize(settings.EditorFontSize);
        SetCodeFonts([], settings.CodeFont);
        _ = LoadInstalledCodeFontsAsync();
        Hotkeys.Changed += OnHotkeysChanged;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.SchemaTree.PropertyChanged += OnSchemaTreePropertyChanged;
        _main.SavedQueries.PropertyChanged += OnSavedQueriesPropertyChanged;
    }

    /// <summary>
    /// The editor size's explanation, naming the reset gesture in the live scheme
    /// (re-spelled when the shortcut modifier changes on this same page).
    /// </summary>
    public string EditorFontSizeHint =>
        $"The size every SQL editor starts at, and returns to on {CommandBindings.LabelFor(CommandId.ResetEditorZoom)}. Zooming changes only the editor you are in.";

    private void OnHotkeysChanged() => OnPropertyChanged(nameof(EditorFontSizeHint));

    /// <summary>The smallest and largest editor font size the page offers (the zoom gestures' range).</summary>
    public static decimal MinEditorFontSize => (decimal)QueryEditorPanel.MinEditorFontSize;

    /// <inheritdoc cref="MinEditorFontSize"/>
    public static decimal MaxEditorFontSize => (decimal)QueryEditorPanel.MaxEditorFontSize;

    /// <summary>
    /// Fills <see cref="CodeFonts"/> with the installed monospace families, read once per
    /// process off the UI thread. The page opens with the bundled face and the saved one
    /// already listed, so the box never shows empty while the scan runs.
    /// </summary>
    public async Task LoadInstalledCodeFontsAsync()
    {
        IReadOnlyList<string> installed;
        try
        {
            installed = await MonospaceFonts.InstalledAsync();
        }
        catch (Exception ex)
        {
            // The list then offers the bundled face and the saved one, which is enough
            // to keep working; the crash log says why there was nothing else.
            Core.Diagnostics.CrashLogger.LogCritical("Reading the installed monospace fonts", ex);
            return;
        }

        SetCodeFonts(installed, SelectedCodeFont?.Name);
    }

    private void SetCodeFonts(IReadOnlyList<string> installed, string? selected)
    {
        var options = new List<CodeFontOption> { CodeFontOption.Bundled };
        options.AddRange(installed.Select(name => new CodeFontOption(name)));

        // A saved family that is not installed (any more) stays listed, so the box
        // shows what is saved; the face itself falls back to the bundled one.
        if (selected is not null && !options.Any(option => option.Name == selected))
        {
            options.Add(new CodeFontOption(selected));
        }

        _loadingCodeFonts = true;
        try
        {
            CodeFonts.ReplaceAll(options);
            SelectedCodeFont = options.First(option => option.Name == selected || (selected is null && option.Name is null));
        }
        finally
        {
            _loadingCodeFonts = false;
        }
    }

    partial void OnInterfaceFontIndexChanged(int value) =>
        App.SetInterfaceFont(value == 0 ? "system" : "inter");

    partial void OnSelectedCodeFontChanged(CodeFontOption? value)
    {
        if (!_loadingCodeFonts && value is not null)
        {
            App.SetCodeFont(value.Name);
        }
    }

    partial void OnEditorFontSizeChanged(decimal? value)
    {
        if (value is { } size)
        {
            App.SetEditorFontSize((double)size);
        }
    }

    /// <summary>
    /// Proxies the main view-model's setting (rather than duplicating it) so
    /// its own persistence hook runs and the command-palette toggle and this
    /// checkbox stay in sync while the window is open.
    /// </summary>
    public bool AutoAliasTables
    {
        get => _main.AutoAliasTables;
        set => _main.AutoAliasTables = value;
    }

    /// <summary>0 = as typed, 1 = UPPER, 2 = lower: the case completion writes keywords in. Proxies the main view-model's setting.</summary>
    public int KeywordCaseIndex
    {
        get => (int)_main.CompletionKeywordCase;
        set => _main.CompletionKeywordCase = (Core.Text.KeywordCase)value;
    }

    /// <summary>Completion writes every table's schema. Same proxy pattern as <see cref="AutoAliasTables"/>.</summary>
    public bool CompletionAlwaysQualifyTables
    {
        get => _main.CompletionAlwaysQualifyTables;
        set => _main.CompletionAlwaysQualifyTables = value;
    }

    /// <summary>Enter accepts a suggestion (off: only Tab does). Same proxy pattern as <see cref="AutoAliasTables"/>.</summary>
    public bool CompletionEnterAccepts
    {
        get => _main.CompletionEnterAccepts;
        set => _main.CompletionEnterAccepts = value;
    }

    /// <summary>Safe mode: stage grid changes for review instead of executing them immediately. Same proxy pattern as <see cref="AutoAliasTables"/>.</summary>
    public bool SafeModeEdits
    {
        get => _main.SafeModeEdits;
        set => _main.SafeModeEdits = value;
    }

    /// <summary>Always show the browse filter bar, off by default. Same proxy pattern as <see cref="AutoAliasTables"/>.</summary>
    public bool ShowFilterBar
    {
        get => _main.ShowFilterBar;
        set => _main.ShowFilterBar = value;
    }

    /// <summary>
    /// Whether the schema tree shows each relation's on-disk size. Proxies the
    /// schema tree's own flag (which persists and re-renders the loaded rows),
    /// same pattern as <see cref="AutoAliasTables"/>.
    /// </summary>
    public bool ShowSchemaSizes
    {
        get => _main.SchemaTree.ShowSizes;
        set => _main.SchemaTree.ShowSizes = value;
    }

    /// <summary>
    /// Whether run statements are filed in the query history. Proxies the
    /// history list's own flag (which persists it), same pattern as
    /// <see cref="AutoAliasTables"/>.
    /// </summary>
    public bool RecordQueryHistory
    {
        get => _main.SavedQueries.RecordHistory;
        set => _main.SavedQueries.RecordHistory = value;
    }

    /// <summary>Unhooks from the main view-model when the window closes.</summary>
    public void Detach()
    {
        Hotkeys.Changed -= OnHotkeysChanged;
        _main.PropertyChanged -= OnMainPropertyChanged;
        _main.SchemaTree.PropertyChanged -= OnSchemaTreePropertyChanged;
        _main.SavedQueries.PropertyChanged -= OnSavedQueriesPropertyChanged;
    }

    private void OnSavedQueriesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SavedQueriesViewModel.RecordHistory))
        {
            OnPropertyChanged(nameof(RecordQueryHistory));
        }
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.AutoAliasTables))
        {
            OnPropertyChanged(nameof(AutoAliasTables));
        }
        else if (e.PropertyName == nameof(MainViewModel.SafeModeEdits))
        {
            OnPropertyChanged(nameof(SafeModeEdits));
        }
        else if (e.PropertyName == nameof(MainViewModel.ShowFilterBar))
        {
            OnPropertyChanged(nameof(ShowFilterBar));
        }
        else if (e.PropertyName == nameof(MainViewModel.CompletionKeywordCase))
        {
            OnPropertyChanged(nameof(KeywordCaseIndex));
        }
        else if (e.PropertyName == nameof(MainViewModel.CompletionAlwaysQualifyTables))
        {
            OnPropertyChanged(nameof(CompletionAlwaysQualifyTables));
        }
        else if (e.PropertyName == nameof(MainViewModel.CompletionEnterAccepts))
        {
            OnPropertyChanged(nameof(CompletionEnterAccepts));
        }
    }

    private void OnSchemaTreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SchemaTreeViewModel.ShowSizes))
        {
            OnPropertyChanged(nameof(ShowSchemaSizes));
        }
    }

    partial void OnThemeIndexChanged(int value) =>
        App.SetTheme(value switch { 1 => "light", 2 => "dark", _ => "system" });

    partial void OnAutoConnectLastProfileChanged(bool value) =>
        App.SetAutoConnectLastProfile(value);

    partial void OnHotkeySchemeIndexChanged(int value) =>
        App.SetHotkeyScheme(value switch { 1 => "windows", 2 => "mac", _ => "auto" });
}
