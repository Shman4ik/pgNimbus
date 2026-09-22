using System.Runtime.CompilerServices;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using PgNimbus.Core.Text;

namespace PgNimbus.App.Completion;

/// <summary>
/// What a completion item names. Drives the kind glyph + color at the left of
/// the popup row (see <see cref="CompletionKindVisuals"/>) and the default
/// tooltip label.
/// </summary>
public enum SqlCompletionKind
{
    Keyword,
    Function,
    Schema,
    Table,
    Column,
    Alias,
    Cte,
    JoinCondition,
    Type,
}

/// <param name="text">The name shown in the list and matched against what the user typed.</param>
/// <param name="kind">What the item names — picks the row glyph/color and the default tooltip label.</param>
/// <param name="insertText">
/// What actually gets written when the item is accepted. Defaults to
/// <paramref name="text"/>; a schema/table/column passes its quote-if-needed
/// form (<c>"Spells"</c>) so the user filters on the bare name but inserts a
/// spelling Postgres will resolve.
/// </param>
/// <param name="priority">
/// Ranking hint the completion list uses to pre-select the best match among
/// equally-good textual matches — higher wins. Lets context-aware
/// completion float the current table's columns above the rest of the
/// catalog (see <see cref="SqlCompletionProvider"/>).
/// </param>
public sealed class SqlCompletionData(string text, SqlCompletionKind kind, string? insertText = null, double priority = 0) : ICompletionData
{
    public IImage? Image => null;

    public string Text { get; } = text;

    public SqlCompletionKind Kind { get; } = kind;

    /// <summary>The literal inserted on completion — may be quoted even when <see cref="Text"/> isn't.</summary>
    public string InsertText { get; } = insertText ?? text;

    /// <summary>
    /// Dim right-aligned text in the popup row: a column's data type, a table's
    /// schema, an alias's target table. Null renders nothing.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Tooltip text next to the list; defaults to the kind's plain label
    /// ("keyword", "table", …) when not set.
    /// </summary>
    public string? DescriptionText { get; init; }

    /// <summary>
    /// The bare table name when this item completes a table — the seed for the
    /// auto-alias appended after an accept in FROM/JOIN position (see
    /// <c>MainWindow.MaybeInsertTableAlias</c>). Null for anything that isn't a
    /// table, which opts the item out of aliasing entirely.
    /// </summary>
    public string? AliasTable { get; init; }

    /// <summary>
    /// What the row shows when it isn't just <see cref="Text"/> — a column two
    /// sources share reads <c>u.id</c> / <c>o.id</c> while both still filter on
    /// <c>id</c>.
    /// </summary>
    public string? DisplayText { get; init; }

    /// <summary>The row label the popup binds to.</summary>
    public string Label => DisplayText ?? Text;

    public object Content => Label;

    /// <summary>
    /// The candidate's identity for the "picked it recently" ranking: kind plus
    /// what it writes, so accepting <c>u.id</c> doesn't promote every other
    /// <c>id</c>, and <c>public.users</c> isn't <c>audit.users</c>.
    /// </summary>
    public string StableId => $"{(int)Kind}:{Detail}:{InsertText}";

    /// <summary>How accepting writes the item — a callable's parens, a table's optional alias.</summary>
    public CompletionInsertKind InsertKind => Kind switch
    {
        SqlCompletionKind.Function => CompletionInsertKind.Function,
        SqlCompletionKind.Table when AliasTable is not null => CompletionInsertKind.Table,
        _ => CompletionInsertKind.Plain,
    };

    public object Description => DescriptionText ?? Kind switch
    {
        SqlCompletionKind.Keyword => "keyword",
        SqlCompletionKind.Function => "function",
        SqlCompletionKind.Schema => "schema",
        SqlCompletionKind.Table => "table",
        SqlCompletionKind.Column => "column",
        SqlCompletionKind.Alias => "alias",
        SqlCompletionKind.Cte => "CTE",
        SqlCompletionKind.JoinCondition => "FK join condition",
        SqlCompletionKind.Type => "type",
        _ => "item",
    };

    // Bound by the popup row template in Theme.axaml.
    public Geometry? KindGlyph => CompletionKindVisuals.Glyph(Kind);

    public IBrush KindBrush => CompletionKindVisuals.Brush(Kind);

    public double Priority { get; } = priority;

    // The provider's identity for "the same candidate" (see its DedupeKey),
    // cached here because snapshot items are shared by every popup.
    internal string? DedupeKey { get; set; }

    // Per-editor accept settings, attached to the TextArea rather than stored on
    // the items: the provider's items are shared across popups and tabs.
    private static readonly ConditionalWeakTable<TextArea, AcceptOptions> Options = new();

    /// <summary>What an editor needs from an accept: whether tables get an auto-alias, and a callback once the text is in.</summary>
    public sealed record AcceptOptions(Func<bool> AutoAliasTables, Action<SqlCompletionData>? Accepted);

    /// <summary>Attaches <paramref name="options"/> to every accept in <paramref name="textArea"/>.</summary>
    public static void Configure(TextArea textArea, AcceptOptions options) =>
        Options.AddOrUpdate(textArea, options);

    /// <summary>
    /// Writes the item as one edit — the replaced token (the whole word, or the
    /// whole quoted identifier, not just the part the popup filtered on), a
    /// callable's parens (reusing one already there), and a table's auto-alias —
    /// so a single Undo takes all of it back. The popup's own
    /// <paramref name="completionSegment"/> is deliberately not what gets
    /// replaced: it stops at the caret.
    /// </summary>
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        Options.TryGetValue(textArea, out var options);
        var document = textArea.Document;
        var aliasSeed = options?.AutoAliasTables() == true ? AliasTable : null;
        var edit = CompletionEdits.Plan(document.Text, textArea.Caret.Offset, InsertText, InsertKind, aliasSeed);

        document.Replace(edit.ReplaceStart, edit.ReplaceLength, edit.InsertText);
        textArea.Caret.Offset = Math.Clamp(edit.CaretOffset, 0, document.TextLength);
        options?.Accepted?.Invoke(this);
    }
}
