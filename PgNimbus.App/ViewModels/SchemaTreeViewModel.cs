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

    /// <summary>Case-insensitive substring typed into the sidebar filter box. Filters schemas and their already-loaded tables live.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    public ObservableCollection<SchemaTreeNode> Schemas { get; } = [];

    public SchemaTreeViewModel(SchemaService schemaService)
    {
        _schemaService = schemaService;
    }

    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// Walks the loaded tree and toggles <see cref="SchemaTreeNode.IsFilteredIn"/> so a schema shows when its
    /// own name matches (all its tables stay visible) or when any loaded table matches (only the matches show,
    /// and the schema auto-expands to reveal them). An empty filter reveals everything. Only schema and table
    /// names participate; unloaded (lazily-expandable) tables can't be matched until their schema is expanded.
    /// </summary>
    private void ApplyFilter()
    {
        var query = FilterText.Trim();

        if (query.Length == 0)
        {
            foreach (var schema in Schemas)
            {
                schema.IsFilteredIn = true;
                foreach (var table in schema.Children)
                {
                    table.IsFilteredIn = true;
                }
            }

            return;
        }

        foreach (var schema in Schemas)
        {
            var schemaMatches = Contains(schema.Name, query);
            var anyTableMatches = false;

            foreach (var table in schema.Children)
            {
                // Placeholder/error rows have no meaningful name to match; ride the schema's own visibility.
                var tableMatches = table is TableNode && Contains(table.Name, query);
                table.IsFilteredIn = schemaMatches || tableMatches;
                anyTableMatches |= tableMatches;
            }

            schema.IsFilteredIn = schemaMatches || anyTableMatches;

            // Reveal deep matches: if the schema only survives because a table inside it matched, expand it.
            if (anyTableMatches && !schemaMatches)
            {
                schema.IsExpanded = true;
            }
        }
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

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

            // Server-wide groups after the schemas: installed/available
            // extensions and non-system roles, both lazily loaded.
            Schemas.Add(new ExtensionsGroupNode(_schemaService));
            Schemas.Add(new RolesGroupNode(_schemaService));

            // A fresh catalog invalidates any prior filter pass; re-apply so a lingering query still holds.
            ApplyFilter();
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
