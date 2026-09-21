using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.App.Converters;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// One column's input in the "Add row" dialog. A blank value with NULL
/// unchecked means "omit this column", so the server applies its default
/// (serial sequences, <c>now()</c>, etc.); checking NULL inserts an explicit
/// NULL; a non-blank value is cast to the column's declared type server-side.
/// The input control follows the column's Postgres type (see
/// <see cref="ColumnValueEditor"/>): enum columns get a dropdown of their
/// pg_enum labels, booleans a checkbox, date/timestamp a calendar picker, and
/// arrays/composites a syntax-checked text box. Every typed editor writes the
/// canonical text into <see cref="Value"/>, so the INSERT pipeline stays
/// text-in, CAST-server-side regardless of which control produced the value.
/// The same field (and its view, <c>ColumnValueEditorView</c>) is also the
/// value input of the row-detail sidebar and of a browse filter, so all three
/// offer one set of type-aware controls.
/// </summary>
public sealed partial class NewRowField : ObservableObject
{
    /// <summary>A field for <paramref name="column"/>, showing <paramref name="placeholder"/> while blank.</summary>
    public static NewRowField For(ColumnDetail column, string placeholder = "default") => new()
    {
        Name = column.Name,
        DataType = column.DataType,
        NotNull = column.NotNull,
        IsPrimaryKey = column.IsPrimaryKey,
        Editor = column.Editor,
        EnumLabels = column.EnumLabels,
        DomainBaseType = column.DomainBaseType,
        Placeholder = placeholder,
    };

    /// <summary>What a blank input reads as: "default" in the Add-row dialog, where blank means the column default.</summary>
    public string Placeholder { get; init; } = "default";

    public string Name { get; init; } = string.Empty;

    /// <summary>The column's declared Postgres type (e.g. "integer", "numeric(10,2)"), used as the CAST target.</summary>
    public string DataType { get; init; } = string.Empty;

    public bool NotNull { get; init; }

    public bool IsPrimaryKey { get; init; }

    /// <summary>Which input control this column gets; classified from its base type (domains resolved).</summary>
    public ColumnValueEditor Editor { get; init; } = ColumnValueEditor.Text;

    /// <summary>The enum type's labels, in declared order, when <see cref="Editor"/> is Enum.</summary>
    public IReadOnlyList<string> EnumLabels { get; init; } = [];

    /// <summary>The resolved base type when the declared type is a domain; null otherwise.</summary>
    public string? DomainBaseType { get; init; }

    public bool IsTextEditor => Editor is ColumnValueEditor.Text or ColumnValueEditor.Array
        or ColumnValueEditor.Composite or ColumnValueEditor.Json or ColumnValueEditor.CastText;

    public bool IsBooleanEditor => Editor == ColumnValueEditor.Boolean;

    public bool IsEnumEditor => Editor == ColumnValueEditor.Enum;

    public bool IsDateEditor => Editor == ColumnValueEditor.Date;

    public bool IsTimestampEditor => Editor == ColumnValueEditor.Timestamp;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isNull;

    // Typed editor state. Each writes through to Value; null/indeterminate
    // means "leave blank" so the column's default still applies.
    [ObservableProperty]
    private bool? _boolValue;

    [ObservableProperty]
    private string? _enumChoice;

    [ObservableProperty]
    private DateTime? _dateValue;

    /// <summary>Time-of-day text next to the timestamp date picker; blank means midnight.</summary>
    [ObservableProperty]
    private string _timeText = string.Empty;

    partial void OnBoolValueChanged(bool? value) =>
        Value = value switch { true => "true", false => "false", null => string.Empty };

    partial void OnEnumChoiceChanged(string? value) => Value = value ?? string.Empty;

    partial void OnDateValueChanged(DateTime? value) => ComposeDateTimeValue();

    partial void OnTimeTextChanged(string value) => ComposeDateTimeValue();

    private void ComposeDateTimeValue()
    {
        if (DateValue is not { } date)
        {
            Value = string.Empty;
            return;
        }

        var datePart = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (Editor == ColumnValueEditor.Date)
        {
            Value = datePart;
            return;
        }

        var timePart = string.IsNullOrWhiteSpace(TimeText) ? "00:00:00" : TimeText.Trim();
        Value = $"{datePart} {timePart}";
    }

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(HasValidationError));
    }

    /// <summary>
    /// Client-side error for a hand-typed value; null when it's fine or the
    /// field is blank (= use the default). Array/composite literals get a
    /// delimiter/quote structure check; plain scalars get a type check for the
    /// numeric and uuid families. Everything else defers to Postgres, which
    /// stays the real parser via the INSERT's CAST — the checks only front-run
    /// the obvious mistakes so they surface in the editor, not as a failed
    /// statement. A domain column is validated against its resolved base type.
    /// </summary>
    public string? ValidationError => string.IsNullOrEmpty(Value) ? null : Editor switch
    {
        ColumnValueEditor.Array => PgValueSyntax.ValidateArray(Value),
        ColumnValueEditor.Composite => PgValueSyntax.ValidateComposite(Value),
        ColumnValueEditor.Json => PgValueSyntax.ValidateJson(Value),
        ColumnValueEditor.Text => PgValueSyntax.ValidateScalar(DomainBaseType ?? DataType, Value),
        _ => null,
    };

    public bool HasValidationError => ValidationError is not null;

    /// <summary>
    /// Loads an existing value into whichever control this field shows: the
    /// checkbox, the dropdown, the date picker (plus time for a timestamp) or
    /// the text box, which gets the same full text the cell inspector shows.
    /// A null checks NULL rather than blanking the input — a blank here would
    /// mean an empty string, which is a different value.
    /// </summary>
    public void Seed(object? value)
    {
        if (value is null)
        {
            IsNull = true;
            return;
        }

        IsNull = false;
        switch (Editor, value)
        {
            case (ColumnValueEditor.Boolean, bool b):
                BoolValue = b;
                return;
            case (ColumnValueEditor.Enum, string label):
                EnumChoice = label;
                return;
            case (ColumnValueEditor.Date, DateOnly date):
                DateValue = date.ToDateTime(TimeOnly.MinValue);
                return;
            case (ColumnValueEditor.Date, DateTime date):
                DateValue = date.Date;
                return;
            case (ColumnValueEditor.Timestamp, DateTime stamp):
                // A timestamptz arrives as UTC and an offset-less edit is read
                // back as UTC (QueryViewModel.ConvertEditedValue), so the wall
                // clock written here round-trips either way.
                TimeText = stamp.ToString("HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture);
                DateValue = stamp.Date;
                return;
            case (ColumnValueEditor.Timestamp, DateTimeOffset stamp):
                TimeText = stamp.ToString("HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture);
                DateValue = stamp.Date;
                return;
            default:
                Value = CellText.Full(value);
                return;
        }
    }

    public string TypeLabel
    {
        get
        {
            // A domain column shows what it resolves to ("posint → integer").
            var type = DomainBaseType is { } baseType ? $"{DataType} → {baseType}" : DataType;
            return IsPrimaryKey ? $"{type} · PK" : type;
        }
    }
}
