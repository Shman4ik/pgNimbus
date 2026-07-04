using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

/// <summary>Drives the no-SQL "alter table" dialog: lists a table's columns and lets the user add/drop/rename one without writing DDL by hand.</summary>
public sealed partial class AlterTableViewModel : ObservableObject
{
    private readonly SchemaEditor _schemaEditor;
    private readonly SchemaService _schemaService;

    public string Schema { get; }

    public string Table { get; }

    public ObservableCollection<ColumnDetail> Columns { get; } = [];

    public IReadOnlyList<string> ColumnTypeOptions { get; } = ColumnTypes.All;

    [ObservableProperty]
    private ColumnDetail? _selectedColumn;

    [ObservableProperty]
    private string _newColumnName = string.Empty;

    [ObservableProperty]
    private string _newColumnType = ColumnTypes.All[0];

    [ObservableProperty]
    private bool _newColumnNullable = true;

    [ObservableProperty]
    private string _renameTo = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Raised after a successful ALTER TABLE so the schema tree node for this table can refresh.</summary>
    public event Action? SchemaChanged;

    public AlterTableViewModel(SchemaEditor schemaEditor, SchemaService schemaService, string schema, string table)
    {
        _schemaEditor = schemaEditor;
        _schemaService = schemaService;
        Schema = schema;
        Table = table;
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var columns = await _schemaService.GetColumnsAsync(Schema, Table, CancellationToken.None);
            Columns.Clear();
            foreach (var column in columns)
            {
                Columns.Add(column);
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

    private bool CanAddColumn() => !string.IsNullOrWhiteSpace(NewColumnName) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAddColumn))]
    private async Task AddColumnAsync()
    {
        var name = NewColumnName.Trim();
        IsBusy = true;
        try
        {
            await _schemaEditor.AddColumnAsync(Schema, Table, name, NewColumnType, NewColumnNullable, CancellationToken.None);
            StatusMessage = $"Added column \"{name}\".";
            NewColumnName = string.Empty;
            await LoadAsync();
            SchemaChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to add column: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDropColumn() => SelectedColumn is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDropColumn))]
    private async Task DropColumnAsync()
    {
        if (SelectedColumn is not { } column)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _schemaEditor.DropColumnAsync(Schema, Table, column.Name, CancellationToken.None);
            StatusMessage = $"Dropped column \"{column.Name}\".";
            SelectedColumn = null;
            await LoadAsync();
            SchemaChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to drop column: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRenameColumn() => SelectedColumn is not null && !string.IsNullOrWhiteSpace(RenameTo) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRenameColumn))]
    private async Task RenameColumnAsync()
    {
        if (SelectedColumn is not { } column)
        {
            return;
        }

        var newName = RenameTo.Trim();
        IsBusy = true;
        try
        {
            await _schemaEditor.RenameColumnAsync(Schema, Table, column.Name, newName, CancellationToken.None);
            StatusMessage = $"Renamed \"{column.Name}\" to \"{newName}\".";
            RenameTo = string.Empty;
            SelectedColumn = null;
            await LoadAsync();
            SchemaChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to rename column: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnNewColumnNameChanged(string value) => AddColumnCommand.NotifyCanExecuteChanged();

    partial void OnRenameToChanged(string value) => RenameColumnCommand.NotifyCanExecuteChanged();

    partial void OnSelectedColumnChanged(ColumnDetail? value)
    {
        DropColumnCommand.NotifyCanExecuteChanged();
        RenameColumnCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        AddColumnCommand.NotifyCanExecuteChanged();
        DropColumnCommand.NotifyCanExecuteChanged();
        RenameColumnCommand.NotifyCanExecuteChanged();
    }
}
