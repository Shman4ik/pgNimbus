using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PgNimbus.App.ViewModels;

/// <summary>One selectable entry in the command palette.</summary>
/// <param name="Title">The primary, fuzzy-matched label (e.g. "sales.orders", "Run query").</param>
/// <param name="Category">A quiet right-aligned tag ("Table", "Saved query", "Action").</param>
/// <param name="Glyph">A single-character icon shown at the leading edge.</param>
/// <param name="InvokeAsync">Runs the entry's effect; awaited after the palette closes.</param>
public sealed record PaletteItem(string Title, string Category, string Glyph, Func<Task> InvokeAsync);

/// <summary>
/// The command palette (Ctrl+K / Ctrl+P): one keyboard-first control to
/// fuzzy-jump to any table, saved query, or action. The full candidate set is
/// supplied by <see cref="MainViewModel"/> when the palette opens; this
/// view-model owns filtering, selection, and invocation.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    // The full candidate set for the current open. Tables arrive asynchronously,
    // so this can be replaced while the palette is already open (see SetItems).
    private IReadOnlyList<PaletteItem> _all = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private PaletteItem? _selectedItem;

    public ObservableCollection<PaletteItem> Results { get; } = [];

    /// <summary>Opens the palette over the given candidates, resetting the query.</summary>
    public void Open(IReadOnlyList<PaletteItem> items)
    {
        _all = items;
        SearchText = string.Empty;
        RebuildResults();
        IsOpen = true;
    }

    /// <summary>Swaps in a fuller candidate set (e.g. once tables have loaded), preserving the typed query.</summary>
    public void SetItems(IReadOnlyList<PaletteItem> items)
    {
        _all = items;
        if (IsOpen)
        {
            RebuildResults();
        }
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        SearchText = string.Empty;
    }

    partial void OnSearchTextChanged(string value) => RebuildResults();

    private void RebuildResults()
    {
        Results.Clear();

        var query = SearchText.Trim();
        IEnumerable<PaletteItem> matches;
        if (query.Length == 0)
        {
            matches = _all;
        }
        else
        {
            matches = _all
                .Select(item => (item, score: FuzzyMatcher.Score($"{item.Title} {item.Category}", query)))
                .Where(x => x.score is not null)
                .OrderByDescending(x => x.score!.Value)
                .Select(x => x.item);
        }

        foreach (var item in matches)
        {
            Results.Add(item);
        }

        SelectedItem = Results.Count > 0 ? Results[0] : null;
    }

    /// <summary>Moves the highlighted row by <paramref name="delta"/>, wrapping around the list.</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var index = SelectedItem is null ? -1 : Results.IndexOf(SelectedItem);
        index = (index + delta + Results.Count) % Results.Count;
        SelectedItem = Results[index];
    }

    /// <summary>Closes the palette and runs the highlighted entry, if any.</summary>
    public async Task AcceptAsync()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        Close();
        await item.InvokeAsync();
    }
}
