using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Drives the "Add row" dialog: one input per column, assembled into a single
/// parameterized INSERT. Values are passed as text parameters cast to each
/// column's declared type server-side (<c>CAST(@p AS numeric(10,2))</c>), so
/// Postgres does the parsing and the statement stays injection-safe. Columns
/// left blank are omitted so their defaults apply.
/// </summary>
public sealed partial class AddRowViewModel : ObservableObject
{
    private readonly QueryEngine _engine;
    private readonly SchemaService _schemaService;

    // Safe mode's staging hook: non-null means Insert stages the row into the
    // owning tab's pending change set (returning an error message, or null on
    // success) instead of executing. Supplied by the view at dialog-open time.
    private readonly Func<IReadOnlyList<PendingInsertValue>, string?>? _stageInsert;

    public string Schema { get; }

    public string Table { get; }

    public string QualifiedName => $"{Schema}.{Table}";

    /// <summary>True when Insert stages the row for later commit instead of executing it — relabels the dialog's primary button.</summary>
    public bool IsStaging => _stageInsert is not null;

    public string InsertButtonText => IsStaging ? "Stage Row" : "Insert Row";

    public ObservableCollection<NewRowField> Fields { get; } = [];

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Raised after a successful INSERT so the grid can refresh and the dialog can close.</summary>
    public event Action? Inserted;

    public AddRowViewModel(
        QueryEngine engine,
        SchemaService schemaService,
        string schema,
        string table,
        Func<IReadOnlyList<PendingInsertValue>, string?>? stageInsert = null)
    {
        _engine = engine;
        _schemaService = schemaService;
        Schema = schema;
        Table = table;
        _stageInsert = stageInsert;
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var columns = await _schemaService.GetColumnsAsync(Schema, Table, CancellationToken.None);
            Fields.Clear();
            foreach (var column in columns)
            {
                Fields.Add(new NewRowField
                {
                    Name = column.Name,
                    DataType = column.DataType,
                    NotNull = column.NotNull,
                    IsPrimaryKey = column.IsPrimaryKey,
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load columns: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InsertAsync()
    {
        IsBusy = true;
        StatusMessage = null;

        // The columns that participate in the INSERT: an explicit NULL, or a
        // typed value. Blank + not-NULL fields are omitted entirely so their
        // defaults apply. Same shape safe mode stages, so both paths agree.
        var values = Fields
            .Where(f => f.IsNull || !string.IsNullOrEmpty(f.Value))
            .Select(f => new PendingInsertValue(f.Name, f.DataType, f.IsNull ? null : f.Value))
            .ToList();

        if (_stageInsert is { } stage)
        {
            StatusMessage = stage(values) ?? "Row staged — commit or discard it from the status bar.";
            IsBusy = false;
            return;
        }

        var columns = new List<string>();
        var valueExpressions = new List<string>();
        var parameters = new Dictionary<string, object?>();

        foreach (var value in values)
        {
            columns.Add(SqlIdentifier.Quote(value.Column));
            if (value.ValueText is null)
            {
                valueExpressions.Add("NULL");
            }
            else
            {
                var name = $"p{parameters.Count}";
                // Cast the text parameter to the column's declared type so
                // Postgres parses "42"/"2024-01-01"/... into the real type.
                valueExpressions.Add($"CAST(@{name} AS {value.DataType})");
                parameters[name] = value.ValueText;
            }
        }

        var target = $"{SqlIdentifier.Quote(Schema)}.{SqlIdentifier.Quote(Table)}";
        var sql = columns.Count == 0
            ? $"INSERT INTO {target} DEFAULT VALUES"
            : BuildInsert(target, columns, valueExpressions);

        try
        {
            await _engine.ExecuteNonQueryAsync(sql, parameters, CancellationToken.None);
            StatusMessage = "Row inserted.";
            Inserted?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Insert failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildInsert(string target, IReadOnlyList<string> columns, IReadOnlyList<string> valueExpressions)
    {
        var sb = new StringBuilder();
        sb.Append("INSERT INTO ").Append(target)
          .Append(" (").Append(string.Join(", ", columns)).Append(')')
          .Append(" VALUES (").Append(string.Join(", ", valueExpressions)).Append(')');
        return sb.ToString();
    }
}
