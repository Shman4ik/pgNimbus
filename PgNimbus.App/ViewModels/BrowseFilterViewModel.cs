using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

/// <summary>One entry of the operator picker: the operator and the label it reads as.</summary>
public sealed record FilterOperatorOption(FilterOperator Operator, string Label)
{
    public static FilterOperatorOption Of(FilterOperator op) => new(op, RowFilterSql.Label(op));

    public override string ToString() => Label;
}

/// <summary>
/// One row of the browse filter bar: column, operator, value. A draft until the
/// bar is applied — editing it composes nothing and runs nothing, it only
/// refreshes the bar's SQL preview (<see cref="Changed"/>). The operator list
/// follows the column's type (<see cref="RowFilterSql.OperatorsFor"/>), and the
/// value input is the same type-aware <see cref="NewRowField"/> the Add-row
/// dialog and the row-detail sidebar use: an enum gets its labels, a date a
/// picker, a number a text box that flags "abc" before anything is sent.
/// </summary>
public sealed partial class BrowseFilterViewModel : ObservableObject
{
    private readonly IReadOnlyList<ColumnDetail> _columns;

    public BrowseFilterViewModel(IReadOnlyList<ColumnDetail> columns, string column, FilterOperator? op = null, string? value = null)
    {
        _columns = columns;
        ColumnNames = columns.Select(c => c.Name).ToList();
        _column = column;
        _operators = OperatorsForColumn(column);
        _operator = op is { } chosen ? FilterOperatorOption.Of(chosen) : _operators.FirstOrDefault();
        _value = CreateValueField(value);
    }

    /// <summary>Raised when anything that changes the predicate changes — the bar's cue to refresh its preview.</summary>
    public event Action? Changed;

    public IReadOnlyList<string> ColumnNames { get; }

    /// <summary>
    /// The word in front of the row: "where" for the first, "and" after it, so
    /// the bar reads as the clause it builds. Set by the owning bar as rows come
    /// and go.
    /// </summary>
    [ObservableProperty]
    private string _connector = "where";

    [ObservableProperty]
    private string _column;

    [ObservableProperty]
    private IReadOnlyList<FilterOperatorOption> _operators;

    [ObservableProperty]
    private FilterOperatorOption? _operator;

    /// <summary>The value input, rebuilt whenever the column or the kind of comparison changes.</summary>
    [ObservableProperty]
    private NewRowField _value;

    /// <summary>False for the operators that take no value (is null, is true, …) — the value input hides.</summary>
    public bool ShowsValue => Operator is { } op && RowFilterSql.TakesValue(op.Operator);

    private ColumnDetail? Detail => _columns.FirstOrDefault(c => c.Name == Column);

    /// <summary>The filter as the Core model sees it.</summary>
    public RowFilter ToFilter() =>
        new(Column, Operator?.Operator ?? FilterOperator.IsNotNull, ShowsValue ? Value.Value : null);

    /// <summary>What's wrong with this filter, or null when it can be applied.</summary>
    public string? Validate()
    {
        if (Detail is not { } detail || Operator is null)
        {
            return $"{Column}: pick a column and a comparison.";
        }

        // The input's own check (a malformed number, bad JSON) is the one the
        // user already sees under the box; report it rather than a second phrasing.
        if (ShowsValue && Value.ValidationError is { } inputError)
        {
            return $"{Column}: {inputError}";
        }

        return RowFilterSql.Validate(ToFilter(), detail.Editor, detail.DataType, detail.DomainBaseType);
    }

    /// <summary>The <c>WHERE</c> predicate for this filter, or null while it isn't valid.</summary>
    public string? Predicate() =>
        Validate() is null && Detail is { } detail ? RowFilterSql.ToPredicate(ToFilter(), detail.Editor, detail.DataType) : null;

    partial void OnColumnChanged(string value)
    {
        Operators = OperatorsForColumn(value);
        Operator = Operators.FirstOrDefault();
        Value = CreateValueField(null);
        Changed?.Invoke();
    }

    partial void OnOperatorChanged(FilterOperatorOption? oldValue, FilterOperatorOption? newValue)
    {
        OnPropertyChanged(nameof(ShowsValue));
        // A text search wants a plain box whatever the column's type (searching
        // a date column for "2026-07" is fine, and a JSON column's search text
        // isn't JSON), so crossing between a search and a comparison swaps the
        // input — keeping what was typed when both sides are text boxes.
        if (IsTextSearch(oldValue) != IsTextSearch(newValue))
        {
            var carried = Value.IsTextEditor ? Value.Value : null;
            Value = CreateValueField(carried);
        }

        Changed?.Invoke();
    }

    // CreateValueField subscribes the new field; only the old one needs letting go.
    partial void OnValueChanged(NewRowField? oldValue, NewRowField newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnValueFieldChanged;
        }
    }

    private void OnValueFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NewRowField.Value))
        {
            Changed?.Invoke();
        }
    }

    private IReadOnlyList<FilterOperatorOption> OperatorsForColumn(string column) =>
        _columns.FirstOrDefault(c => c.Name == column) is { } detail
            ? RowFilterSql.OperatorsFor(detail.Editor, detail.DataType).Select(FilterOperatorOption.Of).ToList()
            : [];

    private NewRowField CreateValueField(string? initial)
    {
        var field = Detail is { } detail && !IsTextSearch(Operator)
            ? NewRowField.For(detail, "value")
            : new NewRowField { Name = Column, DataType = "text", Placeholder = "value" };

        if (!string.IsNullOrEmpty(initial))
        {
            SeedText(field, initial);
        }

        field.PropertyChanged += OnValueFieldChanged;
        return field;
    }

    // Puts filter text into whichever control the field shows, so a quick
    // filter's "= Milan" lands in the enum dropdown rather than a hidden box.
    private static void SeedText(NewRowField field, string text)
    {
        switch (field.Editor)
        {
            case ColumnValueEditor.Enum:
                field.EnumChoice = text;
                break;
            case ColumnValueEditor.Boolean:
                field.BoolValue = bool.TryParse(text, out var b) ? b : null;
                break;
            case ColumnValueEditor.Date or ColumnValueEditor.Timestamp
                when DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var stamp):
                field.TimeText = field.Editor == ColumnValueEditor.Timestamp && text.Length > 10 ? text[11..] : string.Empty;
                field.DateValue = stamp.Date;
                break;
            default:
                field.Value = text;
                break;
        }
    }

    private static bool IsTextSearch(FilterOperatorOption? op) =>
        op?.Operator is FilterOperator.Contains or FilterOperator.NotContains or FilterOperator.StartsWith or FilterOperator.EndsWith;
}
