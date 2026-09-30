using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.App.Converters;
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
/// The notify monitor's payload pane is the same view model, read-only.
/// </summary>
public sealed partial class CellInspectorViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _columnName = string.Empty;

    [ObservableProperty]
    private string _displayText = string.Empty;

    /// <summary>True when <see cref="DisplayText"/> is pretty-printed JSON - drives highlighting, folding and the tree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowText))]
    [NotifyPropertyChangedFor(nameof(ShowTree))]
    [NotifyPropertyChangedFor(nameof(CanShowTreeToggle))]
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

    /// <summary>
    /// The Tree toggle. It is a preference, not a property of one value: it
    /// survives opening the next cell, so Space down a column of jsonb stays in
    /// the tree once you picked it, and a value that isn't JSON just shows as
    /// text meanwhile. It used to be reset on every open.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowText))]
    [NotifyPropertyChangedFor(nameof(ShowTree))]
    private bool _isTreeView;

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

    /// <summary>The tree row last selected, whose path the tree's footer shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPath))]
    private JsonTreeNode? _selectedNode;

    // Read mode shows the tree when it is toggled on and the value is JSON,
    // the text otherwise; edit mode replaces both.
    public bool ShowText => !IsEditing && !(IsTreeView && IsJson);

    public bool ShowTree => !IsEditing && IsTreeView && IsJson;

    /// <summary>The Text/Tree toggle only makes sense for a JSON value in read mode.</summary>
    public bool CanShowTreeToggle => IsJson && !IsEditing;

    /// <summary>
    /// Where the selected tree row is, spelled the way it would be used: as a
    /// Postgres expression over the column when there is one (the results grid),
    /// else as an SQL/JSON path (the notify monitor's payload has no column).
    /// </summary>
    public string? SelectedPath => SelectedNode is not { } node ? null
        : CanCopySqlPath ? SqlPath(node) : JsonPath(node);

    /// <summary>True when the value came from a column, so a path can be written as SQL over it.</summary>
    public bool CanCopySqlPath => _pathColumn is not null;

    partial void OnIsTreeViewChanged(bool value) => EnsureTree();

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

    // The column a tree path is written over, and whether it needs ::jsonb
    // first (a text column holding JSON has no -> operator). Null outside the
    // results grid.
    private string? _pathColumn;
    private bool _castPathToJsonb;

    /// <summary>Opens the inspector read-only (non-editable result sets, text cells).</summary>
    public void Open(string columnName, object? value) =>
        Open(columnName, value, columnIndex: -1, canEdit: false, commit: null);

    /// <summary>
    /// Opens the inspector for one cell. When <paramref name="canEdit"/> is true a
    /// <paramref name="commit"/> delegate persists edits (returning null on success
    /// or an error message); <paramref name="columnIndex"/> identifies the grid
    /// column that delegate targets. <paramref name="validatesAsJson"/> is set when the
    /// column's declared type is json/jsonb, gating client-side JSON validation.
    /// <paramref name="dataTypeName"/> is the column's wire type, which the text
    /// needs to agree with the grid cell's (a date and a timestamp arrive as one
    /// CLR type; see <see cref="CellText.Preview"/>). Passing it also says the
    /// value came from a column called <paramref name="columnName"/>, which is
    /// what a copied SQL path is written over.
    /// </summary>
    public void Open(string columnName, object? value, int columnIndex, bool canEdit, Func<int, string, Task<string?>>? commit, bool validatesAsJson = false, bool startEditing = false, string? dataTypeName = null)
    {
        ColumnName = columnName;
        _columnIndex = columnIndex;
        _commit = commit;
        CanEdit = canEdit && commit is not null;
        _validatesAsJson = validatesAsJson;
        _pathColumn = dataTypeName is null ? null : columnName;
        _castPathToJsonb = dataTypeName is not ("json" or "jsonb");
        OnPropertyChanged(nameof(CanCopySqlPath));

        IsEditing = false;
        SaveError = null;
        _editSeeded = false;

        SetValue(Format(value, dataTypeName));
        IsOpen = true;

        // A double-click on an editable json cell means "let me edit this" -
        // drop straight into the editor rather than the read view.
        if (startEditing)
        {
            Edit();
        }
    }

    /// <summary>The value at a tree row, as a copy wants it (see <see cref="JsonTree.ValueAt"/>).</summary>
    public string? NodeValue(JsonTreeNode node) => JsonTree.ValueAt(DisplayText, node.Path);

    /// <summary>
    /// A Postgres expression reading the row's value out of the column:
    /// <c>-&gt;&gt;</c> (text) for a scalar, <c>-&gt;</c> (json) for an object or
    /// array. Null outside the results grid.
    /// </summary>
    public string? SqlPath(JsonTreeNode node) => _pathColumn is { } column
        ? JsonPaths.ToSql(column, _castPathToJsonb, node.Path, asText: !node.IsContainer)
        : null;

    /// <summary>The SQL/JSON path to the row's value (<c>$.items[0].sku</c>).</summary>
    public static string JsonPath(JsonTreeNode node) => JsonPaths.ToJsonPath(node.Path);

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
        if (!_editSeeded)
        {
            EditText = DisplayText;
            _editSeeded = true;
        }

        IsEditing = true;
    }

    /// <summary>Switch to the View tab, text or tree as last chosen. Leaves the edit
    /// buffer intact so the Edit tab can be re-selected without losing changes
    /// (Cancel is the explicit discard).</summary>
    [RelayCommand]
    private void ViewText()
    {
        IsEditing = false;
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
        if (JsonText.TryFormat(EditText, indented: true, out var formatted))
        {
            EditText = formatted;
        }
    }

    /// <summary>Collapse the in-progress edit onto a single line; a no-op if it isn't valid JSON.</summary>
    [RelayCommand]
    private void Minify()
    {
        if (JsonText.TryFormat(EditText, indented: false, out var minified))
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
        // stored text) and drop back to the read view, tree rebuilt.
        SetValue(Format(EditText, null));
        SaveError = null;
        IsEditing = false;
        _editSeeded = false;
    }

    private void SetValue((string Text, bool IsJson) value)
    {
        SelectedNode = null;
        TreeRoots = [];
        // Kind first: the viewer resets its text on DisplayText, and colours
        // and folds it by what IsJson says at that moment.
        IsJson = value.IsJson;
        DisplayText = value.Text;
        EnsureTree();
    }

    // The tree is parsed when it is first shown for a value, then kept.
    private void EnsureTree()
    {
        if (IsTreeView && IsJson && TreeRoots.Count == 0 && JsonTree.Parse(DisplayText) is { } root)
        {
            TreeRoots = [root];
        }
    }

    private static (string Text, bool IsJson) Format(object? value, string? dataTypeName)
    {
        // Exactly what the grid cell renders, minus the length cap: the two must
        // agree on what a value *is* (bytea as \x-hex, an array as a Postgres
        // literal, hstore as "k"=>"v"), and this is the view that shows all of
        // it. See CellText.
        var text = CellText.Full(value, dataTypeName);

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

        return JsonText.TryFormat(text, indented: true, out pretty);
    }
}
