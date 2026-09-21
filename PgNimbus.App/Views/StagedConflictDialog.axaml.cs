using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Views;

/// <summary>One column of a conflicting row, already rendered for display.</summary>
public sealed record ConflictCellView(
    string Column,
    string Before,
    string Current,
    string Proposed,
    string FullBefore,
    string FullCurrent,
    string FullProposed,
    bool ChangedElsewhere,
    bool HasProposed);

/// <summary>A conflicting row: who it is, what happened to it, and its columns side by side.</summary>
public sealed record ConflictRowView(string Header, string Detail, IReadOnlyList<ConflictCellView> Cells);

/// <summary>
/// Shown after a safe-mode commit was rolled back because another session
/// changed or deleted a staged row (or holds one locked). Lays each conflicting
/// row out as what the user loaded / what the server holds now / what they
/// staged, so the choice that follows is an informed one: restage on top of the
/// current values, drop just the conflicting rows, or leave everything staged.
/// Nothing was committed either way, and the dialog says so — there is no
/// "undo" to offer because nothing happened.
/// </summary>
public partial class StagedConflictDialog : Window
{
    // A 500-row delete that collided with a bulk update elsewhere shouldn't
    // build 500 cards; the summary already carries the counts.
    private const int MaxRowsShown = 50;

    /// <summary>Full opacity for a column the user staged a value for, dim for the rest.</summary>
    public static readonly IValueConverter DimUnlessTrue = new FuncValueConverter<bool, double>(staged => staged ? 1.0 : 0.45);

    public enum Result
    {
        Close,
        Restage,
        UnstageConflicts,
    }

    public StagedConflictDialog()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
    }

    public StagedConflictDialog(StagedChangesConflictException conflict) : this()
    {
        SummaryText.Text = conflict.RowsLocked ? "A staged row is locked by another session"
            : conflict.Conflicts.Count == 0 ? "The staged changes no longer match the table"
            : conflict.Conflicts.Count == 1 ? "1 staged row no longer matches the server"
            : $"{conflict.Conflicts.Count} staged rows no longer match the server";
        MessageText.Text = conflict.Message;

        var rows = conflict.Conflicts.Take(MaxRowsShown).Select(ToView).ToList();
        ConflictList.ItemsSource = rows;
        ConflictList.IsVisible = rows.Count > 0;
        ColumnHeader.IsVisible = rows.Count > 0;
        MoreText.IsVisible = conflict.Conflicts.Count > MaxRowsShown;
        MoreText.Text = $"+{conflict.Conflicts.Count - MaxRowsShown} more conflicting rows not shown.";

        // Restage and unstage act on named rows; a lock or a row-count surprise
        // names none, so the only honest choice left is to close and retry.
        RestageButton.IsVisible = conflict.Conflicts.Count > 0;
        UnstageButton.IsVisible = conflict.Conflicts.Count > 0;
    }

    private static ConflictRowView ToView(RowConflict conflict)
    {
        var header = $"Row {conflict.KeyText}";
        var detail = (conflict.Kind, conflict.StagedAs) switch
        {
            (RowConflictKind.Deleted, StagedRowKind.Delete) => "Already deleted by another session (or its key changed). You staged a delete.",
            (RowConflictKind.Deleted, _) => "Deleted by another session (or its key changed). You staged an edit.",
            (_, StagedRowKind.Delete) => "Changed by another session. You staged a delete.",
            _ => "Changed by another session. You staged an edit.",
        };

        // A gone row and a staged delete are facts about the whole row, so they
        // are said once, on its first line, rather than repeated per column.
        var gone = conflict.Current is null;
        var deleting = conflict.StagedAs == StagedRowKind.Delete;
        var cells = conflict.Columns.Select((c, i) => new ConflictCellView(
            c.Column,
            CellDisplay.Format(c.Before, 120),
            gone ? (i == 0 ? "(row gone)" : "") : CellDisplay.Format(c.Current, 120),
            c.HasProposed ? CellDisplay.Format(c.Proposed, 120) : deleting ? (i == 0 ? "(delete row)" : "") : "—",
            CellDisplay.Format(c.Before, 4000),
            gone ? "(row gone)" : CellDisplay.Format(c.Current, 4000),
            c.HasProposed ? CellDisplay.Format(c.Proposed, 4000) : deleting ? "(delete row)" : "",
            c.ChangedElsewhere || (gone && i == 0),
            c.HasProposed || (deleting && i == 0))).ToList();

        return new ConflictRowView(header, detail, cells);
    }

    private void OnRestageClick(object? sender, RoutedEventArgs e) => Close(Result.Restage);

    private void OnUnstageClick(object? sender, RoutedEventArgs e) => Close(Result.UnstageConflicts);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(Result.Close);
}
