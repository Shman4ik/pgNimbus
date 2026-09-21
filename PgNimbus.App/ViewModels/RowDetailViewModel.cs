using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.App.Converters;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// One name/value line of the row-detail sidebar. Editable lines carry a
/// <see cref="NewRowField"/> (the Add-row dialog's type-aware input, seeded with
/// the cell's value); read-only lines show the value as text and say why.
/// </summary>
public sealed partial class RowDetailField : ObservableObject
{
    private readonly string _baselineValue;
    private readonly bool _baselineNull;

    public RowDetailField(int columnIndex, string name, string typeLabel, object? value, NewRowField? editor, string? readOnlyReason)
    {
        ColumnIndex = columnIndex;
        Name = name;
        TypeLabel = typeLabel;
        IsNull = value is null;
        DisplayText = CellText.Preview(value)?.ToString() ?? string.Empty;
        ReadOnlyReason = readOnlyReason;
        Editor = editor;

        if (editor is not null)
        {
            editor.Seed(value);
            // The baseline is what seeding produced, not the raw value: a typed
            // control can spell a value differently from the grid (a date
            // picker's "2026-07-14" for a timestamp's midnight), and an
            // untouched field must never read as changed.
            _baselineValue = editor.Value;
            _baselineNull = editor.IsNull;
            editor.PropertyChanged += OnEditorChanged;
        }
        else
        {
            _baselineValue = string.Empty;
        }
    }

    public int ColumnIndex { get; }

    public string Name { get; }

    public string TypeLabel { get; }

    /// <summary>Non-null when the value can be changed here.</summary>
    public NewRowField? Editor { get; }

    public bool IsEditable => Editor is not null;

    /// <summary>The value as the grid shows it, for read-only lines.</summary>
    public string DisplayText { get; }

    /// <summary>True when the value is SQL NULL — read-only lines dim it like the grid does.</summary>
    public bool IsNull { get; }

    /// <summary>Why a read-only line can't be edited here ("primary key", "large value…"); null for editable ones.</summary>
    public string? ReadOnlyReason { get; }

    /// <summary>True for a read-only line whose full value the cell inspector can show (and, if the table is editable, edit).</summary>
    public bool CanInspect { get; init; }

    [ObservableProperty]
    private bool _isChanged;

    /// <summary>The edit to stage: the typed text, or null for NULL. Only meaningful when <see cref="IsChanged"/>.</summary>
    public string? EditText => Editor is { IsNull: true } ? null : Editor?.Value;

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NewRowField.Value) or nameof(NewRowField.IsNull) && Editor is { } editor)
        {
            IsChanged = editor.IsNull != _baselineNull || (!editor.IsNull && editor.Value != _baselineValue);
        }
    }
}

/// <summary>
/// Row details: the selected result row as a name/value form (an overlay over
/// the window, like the cell inspector), one
/// line per column, with the grid's type-aware editors for the columns of an
/// editable result. Changes are collected here and go nowhere until Stage,
/// which hands them to <see cref="QueryViewModel.StageRowEdits"/> — the same
/// staged set, review dialog and conflict-checked commit as every other staged
/// edit, whether or not safe mode is on. Nothing in the sidebar writes to the
/// database on its own.
/// </summary>
public sealed partial class RowDetailViewModel : ObservableObject
{
    private readonly QueryViewModel _owner;

    public RowDetailViewModel(QueryViewModel owner)
    {
        _owner = owner;
        _owner.PropertyChanged += OnOwnerPropertyChanged;
    }

    public ObservableCollection<RowDetailField> Fields { get; } = [];

    /// <summary>The row on show — the grid's current row, pinned while the sidebar holds unstaged changes.</summary>
    public object?[]? Row { get; private set; }

    public bool HasRow => Row is not null;

    [ObservableProperty]
    private string _heading = "Row details";

    /// <summary>The table the row belongs to ("public.orders"), when the result maps to one.</summary>
    [ObservableProperty]
    private string? _source;

    /// <summary>What the sidebar says when there's no row, or why the row can't be edited.</summary>
    [ObservableProperty]
    private string? _note = "Select a row to see its fields.";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StageCommand), nameof(RevertCommand), nameof(PreviousRowCommand), nameof(NextRowCommand))]
    private int _changedCount;

    public bool HasChanges => ChangedCount > 0;

    public string StageLabel => ChangedCount switch
    {
        0 => "Stage",
        1 => "Stage 1 change",
        var n => $"Stage {n} changes",
    };

    /// <summary>Raised after Stage with the row instance the grid now holds, so the view can keep it selected.</summary>
    public event Action<object?[]>? RowReplaced;

    /// <summary>Raised for "open in inspector" on a value too large for the form.</summary>
    public event Action<object?[], int>? InspectRequested;

    /// <summary>
    /// Raised with the row to show next (the grid's previous or next row). The
    /// overlay covers the grid, so this is how a user walks the rows without
    /// closing it; the view selects that row in the grid, which loads it here.
    /// </summary>
    public event Action<object?[]>? NavigateRequested;

    // Stepping away from unstaged edits would either drop them or leave the
    // form showing a row the grid no longer has selected; Stage or Revert first.
    private bool CanStep(int delta) =>
        !HasChanges && Row is { } row && _owner.Rows.IndexOf(row) is var index and >= 0
        && index + delta >= 0 && index + delta < _owner.Rows.Count;

    private bool CanGoPrevious() => CanStep(-1);

    private bool CanGoNext() => CanStep(+1);

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousRow() => Step(-1);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextRow() => Step(+1);

    private void Step(int delta)
    {
        if (Row is { } row && _owner.Rows.IndexOf(row) is var index and >= 0)
        {
            NavigateRequested?.Invoke(_owner.Rows[index + delta]);
        }
    }

    partial void OnChangedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(StageLabel));
    }

    /// <summary>
    /// Shows <paramref name="row"/>. Ignored while the sidebar holds unstaged
    /// changes to a different row (unless <paramref name="force"/>): moving the
    /// grid's selection must not throw away what was typed.
    /// </summary>
    public void Load(object?[]? row, bool force = false)
    {
        if (!force && HasChanges && !ReferenceEquals(row, Row))
        {
            return;
        }

        foreach (var field in Fields)
        {
            field.PropertyChanged -= OnFieldChanged;
        }

        Fields.Clear();
        ChangedCount = 0;
        Error = null;
        Row = row;
        OnPropertyChanged(nameof(Row));
        OnPropertyChanged(nameof(HasRow));
        PreviousRowCommand.NotifyCanExecuteChanged();
        NextRowCommand.NotifyCanExecuteChanged();

        var context = _owner.EditContext;
        Source = context is not null ? $"{context.Schema}.{context.Table}"
            : _owner.Browse is { } browse ? $"{browse.Schema}.{browse.Name}"
            : null;

        if (row is null)
        {
            Heading = "Row details";
            Note = "Select a row in the grid to see its fields.";
            return;
        }

        var editable = _owner.IsEditable;
        var index = _owner.Rows.IndexOf(row);
        var offset = _owner.Browse?.Offset ?? 0;
        Heading = index >= 0 ? $"Row {offset + index + 1:N0} of {offset + _owner.Rows.Count:N0}" : "Row details";
        Note = editable ? null : _owner.ReadOnlyHint is { } hint ? $"Read-only: {hint}" : "Read-only: this result isn't mapped to one table.";

        for (var i = 0; i < _owner.ColumnNames.Count && i < row.Length; i++)
        {
            var name = _owner.ColumnNames[i];
            var value = row[i];
            var meta = context?.Column(name);
            var typeLabel = meta?.DataType ?? _owner.ColumnTypeName(i) ?? string.Empty;

            string? reason = null;
            if (!editable || meta is null)
            {
                reason = editable ? "not a column of the table" : null;
            }
            else if (context!.PrimaryKeyColumns.Contains(name))
            {
                // The type label carries it ("bigint · primary key"); a second
                // line saying the same would only push the form down.
                reason = string.Empty;
                typeLabel += " · primary key";
            }
            else if (CellText.IsShortened(value) || QueryEngine.IsUnreadableCell(value))
            {
                // The form would hold the value in a one-box editor at best and
                // a preview at worst; the inspector carries the whole thing.
                reason = QueryEngine.IsUnreadableCell(value) ? "unreadable type" : "large value: open it in the inspector";
            }

            var field = reason is null && editable && meta is not null
                ? new RowDetailField(i, name, typeLabel, value, NewRowField.For(meta, placeholder: "empty string"), null)
                : new RowDetailField(i, name, typeLabel, value, null, reason is { Length: 0 } ? null : reason) { CanInspect = CellText.IsShortened(value) };
            field.PropertyChanged += OnFieldChanged;
            Fields.Add(field);
        }
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void Stage()
    {
        if (Row is not { } row)
        {
            return;
        }

        if (Fields.FirstOrDefault(f => f.IsChanged && f.Editor is { IsNull: false, HasValidationError: true }) is { } invalid)
        {
            Error = $"{invalid.Name}: {invalid.Editor!.ValidationError}";
            return;
        }

        var edits = Fields.Where(f => f.IsChanged).Select(f => (f.ColumnIndex, f.EditText)).ToList();
        var (updated, error) = _owner.StageRowEdits(row, edits);
        if (error is not null)
        {
            Error = error;
            return;
        }

        Load(updated, force: true);
        RowReplaced?.Invoke(updated);
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void Revert() => Load(Row, force: true);

    [RelayCommand]
    private void Inspect(RowDetailField field)
    {
        if (Row is { } row)
        {
            InspectRequested?.Invoke(row, field.ColumnIndex);
        }
    }

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RowDetailField.IsChanged))
        {
            ChangedCount = Fields.Count(f => f.IsChanged);
            Error = null;
        }
    }

    // A new edit context means a new result (a run, a page, a commit's reload):
    // the column indexes the fields point at may mean something else now, so
    // edits to the old result can't be carried over. Show the row again, fresh,
    // if it's still on screen.
    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueryViewModel.EditContext) or nameof(QueryViewModel.Rows))
        {
            Load(Row is { } row && _owner.Rows.Contains(row) ? row : null, force: true);
        }
    }
}
