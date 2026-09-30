using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Text;

namespace PgNimbus.App.ViewModels;

/// <summary>One selectable entry in the command palette.</summary>
/// <param name="Title">The primary, fuzzy-matched label (e.g. "sales.orders", "Run query").</param>
/// <param name="Category">A quiet right-aligned tag ("Table", "Saved query", "Action").</param>
/// <param name="Glyph">A single-character icon shown at the leading edge.</param>
/// <param name="InvokeAsync">Runs the entry's effect; awaited after the palette closes.</param>
/// <param name="Shortcut">The action's hotkey ("Ctrl+Enter"), shown so users learn it; null when it has none.</param>
public sealed record PaletteItem(string Title, string Category, string Glyph, Func<Task> InvokeAsync, string? Shortcut = null);

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

    // What each candidate is matched against ("title category"), built once per
    // candidate set instead of per candidate per keystroke.
    private string[] _haystacks = [];

    // The previous keystroke's query (lower case) and the indexes into _all it
    // matched. One more character can only narrow a subsequence match, so the
    // next keystroke scores only those: every relation of the database (the
    // palette lists them all, partitions included) was scored on every key.
    private string? _matchedQuery;
    private List<int>? _matched;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private PaletteItem? _selectedItem;

    /// <summary>
    /// The candidates the query matches, best first. Replaced with one Reset per
    /// keystroke: a Clear and an Add per match re-added tens of thousands of rows.
    /// </summary>
    public RangeObservableCollection<PaletteItem> Results { get; } = [];

    /// <summary>Opens the palette over the given candidates, resetting the query.</summary>
    public void Open(IReadOnlyList<PaletteItem> items)
    {
        UseItems(items);
        SearchText = string.Empty;
        RebuildResults();
        IsOpen = true;
    }

    /// <summary>Swaps in a fuller candidate set (e.g. once tables have loaded), preserving the typed query.</summary>
    public void SetItems(IReadOnlyList<PaletteItem> items)
    {
        UseItems(items);
        if (IsOpen)
        {
            RebuildResults();
        }
    }

    private void UseItems(IReadOnlyList<PaletteItem> items)
    {
        _all = items;
        _haystacks = new string[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            _haystacks[i] = $"{items[i].Title} {items[i].Category}";
        }

        _matchedQuery = null;
        _matched = null;
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
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            _matchedQuery = null;
            _matched = null;
            Results.ReplaceAll(_all);
        }
        else
        {
            var lower = query.ToLowerInvariant();
            var narrowing = _matched is not null && _matchedQuery is { } previous
                && lower.Length > previous.Length && lower.StartsWith(previous, StringComparison.Ordinal);
            var candidates = narrowing ? _matched! : null;
            var count = candidates?.Count ?? _all.Count;

            var matched = new List<int>();
            var scored = new List<(int Index, int Score)>();
            for (var n = 0; n < count; n++)
            {
                var i = candidates?[n] ?? n;
                if (FuzzyMatcher.Score(_haystacks[i], query) is { } score)
                {
                    matched.Add(i);
                    scored.Add((i, score));
                }
            }

            // Best score first; ties keep the candidates' own order, as the stable
            // OrderByDescending this replaced did.
            scored.Sort(static (a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index));
            _matchedQuery = lower;
            _matched = matched;
            Results.ReplaceAll(scored.Select(s => _all[s.Index]));
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
