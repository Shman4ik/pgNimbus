using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// </summary>
public sealed partial class NewRowField : ObservableObject
{
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

    public bool IsTextEditor => Editor is ColumnValueEditor.Text or ColumnValueEditor.Array or ColumnValueEditor.Composite;

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
        ColumnValueEditor.Text => PgValueSyntax.ValidateScalar(DomainBaseType ?? DataType, Value),
        _ => null,
    };

    public bool HasValidationError => ValidationError is not null;

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
