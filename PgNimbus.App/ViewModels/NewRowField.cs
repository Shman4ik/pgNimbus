using CommunityToolkit.Mvvm.ComponentModel;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// One column's input in the "Add row" dialog. A blank value with NULL
/// unchecked means "omit this column", so the server applies its default
/// (serial sequences, <c>now()</c>, etc.); checking NULL inserts an explicit
/// NULL; a non-blank value is cast to the column's declared type server-side.
/// </summary>
public sealed partial class NewRowField : ObservableObject
{
    public string Name { get; init; } = string.Empty;

    /// <summary>The column's declared Postgres type (e.g. "integer", "numeric(10,2)"), used as the CAST target.</summary>
    public string DataType { get; init; } = string.Empty;

    public bool NotNull { get; init; }

    public bool IsPrimaryKey { get; init; }

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isNull;

    public string TypeLabel => IsPrimaryKey ? $"{DataType} · PK" : DataType;
}
