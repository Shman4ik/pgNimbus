using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Json;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Backs the results-grid cell inspector overlay: a detail view of one cell's
/// full value, opened when a <c>text</c>/<c>jsonb</c> value is too long to read
/// inline. <c>jsonb</c>/<c>json</c> values are pretty-printed and can be browsed
/// as a collapsible tree; when the cell belongs to an editable result set they
/// can also be edited in place — formatted, minified, validated client-side, and
/// saved through the same cast-to-<c>jsonb</c> path an inline grid edit uses.
/// </summary>
public sealed partial class CellInspectorViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _columnName = string.Empty;

    [ObservableProperty]
    private string _displayText = string.Empty;

    /// <summary>True when <see cref="DisplayText"/> is pretty-printed JSON - drives monospace display and enables the tree view.</summary>
    [ObservableProperty]
    private bool _isJson;

    /// <summary>Whether the inspector wraps long lines, Notepad++-style. On by default so a long text/jsonb value never scrolls off-screen horizontally.</summary>
    [ObservableProperty]
    private bool _wordWrap = true;

    /// <summary>True when this cell can be edited (an editable, JSON-typed cell in a keyed result set).</summary>
    [ObservableProperty]
    private bool _canEdit;

    /// <summary>True while the inline JSON editor is showing instead of the read-only value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowText))]
    [NotifyPropertyChangedFor(nameof(ShowTree))]
    [NotifyPropertyChangedFor(nameof(CanShowTreeToggle))]
    [NotifyPropertyChangedFor(nameof(ValidationError))]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private bool _isEditing;

    /// <summary>True to show the collapsible tree instead of the raw/pretty text (read mode only, JSON only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowText))]
    [NotifyPropertyChangedFor(nameof(ShowTree))]
    private bool _isTreeView;

    /// <summary>True when the value is JSON - drives monospace display; also gated on edit mode for the tree toggle's visibility.</summary>
    partial void OnIsJsonChanged(bool value) => OnPropertyChanged(nameof(CanShowTreeToggle));

    /// <summary>The editable JSON text, two-way-synced with the AvaloniaEdit editor in the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationError))]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private string _editText = string.Empty;

    /// <summary>A save failure surfaced inline in the editor (null when the last save succeeded or none was attempted).</summary>
    [ObservableProperty]
    private string? _saveError;

    /// <summary>The parsed document tree the tree view binds to (single-element: the root); empty when the value isn't JSON.</summary>
    [ObservableProperty]
    private IReadOnlyList<JsonTreeNode> _treeRoots = [];

    // Read mode shows text unless the tree is toggled on; edit mode replaces both.
    public bool ShowText => !IsEditing && !IsTreeView;

    public bool ShowTree => !IsEditing && IsTreeView;

    /// <summary>The Text/Tree toggle only makes sense for a JSON value in read mode.</summary>
    public bool CanShowTreeToggle => IsJson && !IsEditing;

// Parse the tree lazily the first time it's shown (and only for JSON), then cache it.
    partial void OnIsTreeViewChanged(bool value)
    {
        if (value && IsJson && TreeRoots.Count == 0 && JsonTree.Parse(DisplayText) is { } root)
        {
            TreeRoots = [root];
        }
    }

    /// <summary>Client-side JSON validation of the in-progress edit — null when it parses
    /// or is blank. Only JSON cells are pre-validated; other free-text types (plain text,
    /// arrays, xml, …) are parsed server-side on save (the cast surfaces a precise error).</summary>
    public string? ValidationError => IsEditing && _validatesAsJson ? PgValueSyntax.ValidateJson(EditText) : null;

    public bool HasValidationError => ValidationError is not null;

    // Set when the cell is editable: how to persist a new value (returns null on
    // success, or an error message to show inline) and which grid column it is.
    private Func<int, string, Task<string?>>? _commit;
    private int _columnIndex = -1;

    // Whether the edit buffer has been seeded from the displayed value since the
    // inspector opened. Lets the View/Edit tabs switch back and forth without
    // discarding an in-progress edit — only the first entry into edit mode (or a
    // Cancel/Save reset) reseeds EditText from DisplayText.
    private bool _editSeeded;

    // Whether the column's declared type is json/jsonb (type-derived, unlike the
    // content-derived IsJson). Only then is the edit client-side JSON-validated -
    // a plain text column holding a JSON-looking string must accept any string.
    private bool _validatesAsJson;

    // Re-rendering goes through Utf8JsonWriter + JsonDocument.WriteTo, never
    // JsonSerializer.Serialize(document, ...): the latter is the reflection-based
    // serializer, which NativeAOT disables outright
    // ("Reflection-based serialization has been disabled for this application").
    // Same reason ResultExporter writes its JSON by hand.
    private static readonly JsonWriterOptions PrettyPrintOptions = new()
    {
        Indented = true,
        // Default escaping is ASCII-only (everything else becomes \uXXXX) -
        // relax to all Unicode so e.g. Cyrillic values show as themselves,
        // not escape sequences.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly JsonWriterOptions MinifyOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Opens the inspector read-only (non-editable result sets, text cells).</summary>
    public void Open(string columnName, object? value) =>
        Open(columnName, value, columnIndex: -1, canEdit: false, commit: null);

    /// <summary>
    /// Opens the inspector for one cell. When <paramref name="canEdit"/> is true a
    /// <paramref name="commit"/> delegate persists edits (returning null on success
    /// or an error message); <paramref name="columnIndex"/> identifies the grid
    /// column that delegate targets. <paramref name="validatesAsJson"/> is set when the
    /// column's declared type is json/jsonb, gating client-side JSON validation.
    /// </summary>
    public void Open(string columnName, object? value, int columnIndex, bool canEdit, Func<int, string, Task<string?>>? commit, bool validatesAsJson = false, bool startEditing = false)
    {
        ColumnName = columnName;
        _columnIndex = columnIndex;
        _commit = commit;
        CanEdit = canEdit && commit is not null;
        _validatesAsJson = validatesAsJson;

        IsEditing = false;
        IsTreeView = false;
        SaveError = null;
        _editSeeded = false;

        (DisplayText, IsJson) = Format(value);
        TreeRoots = [];
        IsOpen = true;

        // A double-click on an editable json cell means "let me edit this" -
        // drop straight into the editor rather than the read view.
        if (startEditing)
        {
            Edit();
        }
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Switch to the Edit tab. Seeds the editor from the current value only
    /// on first entry, so toggling back to View and returning keeps in-progress edits.</summary>
    [RelayCommand]
    private void Edit()
    {
        if (!CanEdit)
        {
            return;
        }

        SaveError = null;
        IsTreeView = false;
        if (!_editSeeded)
        {
            EditText = DisplayText;
            _editSeeded = true;
        }
        IsEditing = true;
    }

    /// <summary>Switch to the View tab. Leaves the edit buffer intact so the Edit tab
    /// can be re-selected without losing changes (Cancel is the explicit discard).</summary>
    [RelayCommand]
    private void ViewText()
    {
        IsEditing = false;
        IsTreeView = false;
        SaveError = null;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        _editSeeded = false;
        SaveError = null;
    }

    /// <summary>Pretty-print the in-progress edit; a no-op if it isn't valid JSON.</summary>
    [RelayCommand]
    private void Format()
    {
        if (TryReformat(EditText, PrettyPrintOptions, out var formatted))
        {
            EditText = formatted;
        }
    }

    /// <summary>Collapse the in-progress edit onto a single line; a no-op if it isn't valid JSON.</summary>
    [RelayCommand]
    private void Minify()
    {
        if (TryReformat(EditText, MinifyOptions, out var minified))
        {
            EditText = minified;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanEdit || _commit is null)
        {
            return;
        }

        if (ValidationError is { } error)
        {
            SaveError = error;
            return;
        }

        var failure = await _commit(_columnIndex, EditText);
        if (failure is not null)
        {
            SaveError = failure;
            return;
        }

        // Persisted. Reflect the saved value (pretty-printed, same as the grid's
        // stored text) and drop back to the read view, tree cache invalidated.
        (DisplayText, IsJson) = Format(EditText);
        TreeRoots = [];
        SaveError = null;
        IsEditing = false;
        _editSeeded = false;
    }

    private static bool TryReformat(string text, JsonWriterOptions options, out string result)
    {
        result = text;
        try
        {
            using var document = JsonDocument.Parse(text);
            result = Render(document, options);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Render(JsonDocument document, JsonWriterOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            document.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static (string Text, bool IsJson) Format(object? value)
    {
        var text = value switch
        {
            null => "NULL",
            byte[] bytes => $"\\x{Convert.ToHexString(bytes)}",
            // Same Postgres-literal rendering the grid uses — never "System.String[]".
            Array array => PgValueSyntax.FormatArray(array),
            // hstore materializes as a Dictionary<string,string>; render its
            // literal ("k"=>"v") like the grid, never the CLR type name.
            System.Collections.IDictionary map => PgValueSyntax.FormatHstore(map),
            _ => value.ToString() ?? string.Empty,
        };

        return TryPrettyPrintJson(text, out var pretty) ? (pretty, true) : (text, false);
    }

    // Npgsql returns json/jsonb columns as their raw text by default, so the
    // only signal that a value is JSON (rather than, say, a plain string that
    // happens to start with '{') is that it actually parses as one.
    private static bool TryPrettyPrintJson(string text, out string pretty)
    {
        pretty = text;

        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            pretty = Render(document, PrettyPrintOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
