using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed partial class SchemaTreeViewModel : ObservableObject
{
    private readonly SchemaService _schemaService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public ObservableCollection<SchemaTreeNode> Schemas { get; } = [];

    public SchemaTreeViewModel(SchemaService schemaService)
    {
        _schemaService = schemaService;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var schemas = await _schemaService.GetSchemasAsync(CancellationToken.None);
            Schemas.Clear();
            foreach (var schema in schemas)
            {
                Schemas.Add(new SchemaNode(_schemaService, schema.Name));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
