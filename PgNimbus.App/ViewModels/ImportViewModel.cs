using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Import;

namespace PgNimbus.App.ViewModels;

/// <summary>One target column in the import dialog: renameable, retypeable (types locked to the inference allow-list).</summary>
public sealed partial class ImportColumnViewModel(string name, string dataType) : ObservableObject
{
    public static IReadOnlyList<string> TypeChoices => TypeInferrer.Types;

    [ObservableProperty]
    private string _name = name;

    [ObservableProperty]
    private string _dataType = dataType;
}

/// <summary>
/// Drives the CSV/JSON import dialog: parsed data in, target
/// schema/table/columns tweaked by the user, then a COPY-based load. Raises
/// <see cref="Completed"/> so the opener can refresh the tree and show the result.
/// </summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private readonly ImportService _service;
    private readonly TabularData _data;

    [ObservableProperty]
    private string _schema;

    [ObservableProperty]
    private string _tableName;

    /// <summary>True (default): CREATE TABLE from the column list; false: append into an existing table by column names.</summary>
    [ObservableProperty]
    private bool _createNewTable = true;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isImporting;

    public IReadOnlyList<string> Schemas { get; }

    public ObservableCollection<ImportColumnViewModel> Columns { get; } = [];

    public string Summary { get; }

    /// <summary>Raised after a successful load with (schema, table, rows imported).</summary>
    public event Action<string, string, long>? Completed;

    public ImportViewModel(ImportService service, TabularData data, string suggestedTable, IReadOnlyList<string> schemas)
    {
        _service = service;
        _data = data;
        _tableName = suggestedTable;
        Schemas = schemas.Count > 0 ? schemas : ["public"];
        _schema = Schemas.Contains("public") ? "public" : Schemas[0];
        Summary = $"{data.Rows.Count:N0} row{(data.Rows.Count == 1 ? "" : "s")} · {data.Columns.Count} column{(data.Columns.Count == 1 ? "" : "s")} parsed";

        for (var i = 0; i < data.Columns.Count; i++)
        {
            var index = i;
            Columns.Add(new ImportColumnViewModel(
                data.Columns[i],
                TypeInferrer.Infer(data.Rows.Select(r => index < r.Length ? r[index] : null))));
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (string.IsNullOrWhiteSpace(TableName))
        {
            ErrorMessage = "Table name is required.";
            return;
        }

        ErrorMessage = null;
        IsImporting = true;
        try
        {
            var columns = Columns.Select(c => new ImportColumn(c.Name.Trim(), c.DataType)).ToList();
            var count = await _service.ImportAsync(Schema, TableName.Trim(), columns, _data.Rows, CreateNewTable, CancellationToken.None);
            Completed?.Invoke(Schema, TableName.Trim(), count);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsImporting = false;
        }
    }
}
