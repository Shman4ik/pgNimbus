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

    /// <param name="inferredTypes">
    /// Each column's type as <see cref="InferTypes"/> reads it, when the caller already
    /// did that off the UI thread; null to infer here (fine for the handful of rows a
    /// test or the screenshot harness passes).
    /// </param>
    public ImportViewModel(ImportService service, TabularData data, string suggestedTable, IReadOnlyList<string> schemas,
        IReadOnlyList<string>? inferredTypes = null)
    {
        _service = service;
        _data = data;
        _tableName = suggestedTable;
        Schemas = schemas.Count > 0 ? schemas : ["public"];
        _schema = Schemas.Contains("public") ? "public" : Schemas[0];
        Summary = $"{data.Rows.Count:N0} row{(data.Rows.Count == 1 ? "" : "s")} · {data.Columns.Count} column{(data.Columns.Count == 1 ? "" : "s")} parsed";

        var types = inferredTypes ?? InferTypes(data);
        for (var i = 0; i < data.Columns.Count; i++)
        {
            Columns.Add(new ImportColumnViewModel(data.Columns[i], types[i]));
        }
    }

    /// <summary>Each column's Postgres type as <see cref="TypeInferrer"/> reads it from every row. Pure; safe off the UI thread.</summary>
    public static IReadOnlyList<string> InferTypes(TabularData data)
    {
        var types = new string[data.Columns.Count];
        for (var i = 0; i < types.Length; i++)
        {
            var index = i;
            types[i] = TypeInferrer.Infer(data.Rows.Select(r => index < r.Length ? r[index] : null));
        }

        return types;
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
            // On the thread pool: the COPY loop formats every row, and its writes
            // complete synchronously while Npgsql's buffer has room, so awaited from
            // the UI thread it ran there, a million rows at a time.
            var (schema, table, rows, create) = (Schema, TableName.Trim(), _data.Rows, CreateNewTable);
            var count = await Task.Run(() => _service.ImportAsync(schema, table, columns, rows, create, CancellationToken.None));
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
