using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using PgNimbus.Core.Localization;
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
        DialogChrome.Attach(this);
    }

    public StagedConflictDialog(StagedChangesConflictException conflict) : this()
    {
        _conflict = conflict;
        Render();

        // Every text below is composed from the conflict, so a change of language
        // while the dialog is open writes it again.
        Localizer.WhenChanged(this, static dialog => dialog.Render());

        // Restage and unstage act on named rows; a lock or a row-count surprise
        // names none, so the only honest choice left is to close and retry.
        RestageButton.IsVisible = conflict.Conflicts.Count > 0;
        UnstageButton.IsVisible = conflict.Conflicts.Count > 0;
    }

    private readonly StagedChangesConflictException? _conflict;

    private void Render()
    {
        if (_conflict is not { } conflict)
        {
            return;
        }

        SummaryText.Text = conflict.RowsLocked ? Strings.Dialogs_ConflictSummaryLocked
            : conflict.Conflicts.Count == 0 ? Strings.Dialogs_ConflictSummaryMismatch
            : Strings.Dialogs_ConflictSummaryRows(conflict.Conflicts.Count);
        MessageText.Text = conflict.Message;

        var rows = conflict.Conflicts.Take(MaxRowsShown).Select(ToView).ToList();
        ConflictList.ItemsSource = rows;
        ConflictList.IsVisible = rows.Count > 0;
        ColumnHeader.IsVisible = rows.Count > 0;
        MoreText.IsVisible = conflict.Conflicts.Count > MaxRowsShown;
        MoreText.Text = Strings.Dialogs_ConflictMore(conflict.Conflicts.Count - MaxRowsShown);
    }

    private static ConflictRowView ToView(RowConflict conflict)
    {
        var header = Strings.Dialogs_ConflictRowHeader(conflict.KeyText);
        var detail = (conflict.Kind, conflict.StagedAs) switch
        {
            (RowConflictKind.Deleted, StagedRowKind.Delete) => Strings.Dialogs_ConflictDetailDeletedDelete,
            (RowConflictKind.Deleted, _) => Strings.Dialogs_ConflictDetailDeletedEdit,
            (_, StagedRowKind.Delete) => Strings.Dialogs_ConflictDetailChangedDelete,
            _ => Strings.Dialogs_ConflictDetailChangedEdit,
        };

        // A gone row and a staged delete are facts about the whole row, so they
        // are said once, on its first line, rather than repeated per column.
        var gone = conflict.Current is null;
        var deleting = conflict.StagedAs == StagedRowKind.Delete;
        var cells = conflict.Columns.Select((c, i) => new ConflictCellView(
            c.Column,
            CellDisplay.Format(c.Before, 120),
            gone ? (i == 0 ? Strings.Dialogs_ConflictRowGone : "") : CellDisplay.Format(c.Current, 120),
            c.HasProposed ? CellDisplay.Format(c.Proposed, 120) : deleting ? (i == 0 ? Strings.Dialogs_ConflictDeleteRow : "") : "—",
            CellDisplay.Format(c.Before, 4000),
            gone ? Strings.Dialogs_ConflictRowGone : CellDisplay.Format(c.Current, 4000),
            c.HasProposed ? CellDisplay.Format(c.Proposed, 4000) : deleting ? Strings.Dialogs_ConflictDeleteRow : "",
            c.ChangedElsewhere || (gone && i == 0),
            c.HasProposed || (deleting && i == 0))).ToList();

        return new ConflictRowView(header, detail, cells);
    }

    private void OnRestageClick(object? sender, RoutedEventArgs e) => Close(Result.Restage);

    private void OnUnstageClick(object? sender, RoutedEventArgs e) => Close(Result.UnstageConflicts);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(Result.Close);
}
