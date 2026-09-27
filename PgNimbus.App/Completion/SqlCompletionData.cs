using System.Runtime.CompilerServices;
using Avalonia.Controls;
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
    /// <summary>A literal value: an enum's label, a date field.</summary>
    Value,
    Sequence,
    /// <summary>More than a name in one accept: a join with its condition, a column list, a GROUP BY list.</summary>
    Snippet,
    Role,
    Setting,
    Extension,
    Index,
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

    /// <summary>
    /// A row that is only a plausible name, not one this statement asked for:
    /// a column offered from the whole catalog because the statement names no
    /// relation that has it, or one of pg_catalog's thousand functions beyond
    /// the everyday list. Enter leaves such a row alone unless the user chose
    /// it (see <see cref="CompletionAcceptance"/>).
    /// </summary>
    public bool IsGuess { get; init; }

    /// <summary>Where the caret goes inside <see cref="InsertText"/> after an accept; null = after it (or inside a callable's parens).</summary>
    public int? CaretIndex { get; init; }

    /// <summary>
    /// An absolute document offset the replaced range starts at, when that is
    /// before the word under the caret: expanding a <c>*</c> replaces the star.
    /// </summary>
    public int? ReplaceFrom { get; init; }

    /// <summary>
    /// A clause accepting this row also writes at the end of the statement —
    /// <c>FROM customers</c> for a column picked in a select list that has no
    /// FROM yet (E08). Null for everything else.
    /// </summary>
    public string? AppendClause { get; init; }

    /// <summary>
    /// What accepting writes with the caret at <paramref name="caret"/>: a
    /// keyword in <paramref name="keywordCase"/>, judged by what was typed of
    /// the word so far; anything else its <see cref="InsertText"/>.
    /// </summary>
    public string InsertTextFor(string text, int caret, KeywordCase keywordCase)
    {
        if (Kind != SqlCompletionKind.Keyword)
        {
            return InsertText;
        }

        caret = Math.Clamp(caret, 0, text.Length);
        var typed = text[CompletionEdits.TokenAt(text, caret).FilterStart..caret];
        return KeywordCasing.Apply(InsertText, typed, keywordCase);
    }

    /// <summary>The row label the popup binds to.</summary>
    public string Label => DisplayText ?? Text;

    public object Content => Label;

    /// <summary>
    /// The candidate's identity for the "picked it recently" ranking: kind plus
    /// what it writes, so accepting <c>u.id</c> doesn't promote every other
    /// <c>id</c>, and <c>public.users</c> isn't <c>audit.users</c>.
    /// </summary>
    public string StableId => _stableId ??= $"{(int)Kind}:{Detail}:{InsertText}";

    private string? _stableId;

    /// <summary>How accepting writes the item — a callable's parens, a table's optional alias.</summary>
    public CompletionInsertKind InsertKind => Kind switch
    {
        SqlCompletionKind.Function => CompletionInsertKind.Function,
        SqlCompletionKind.Table when AliasTable is not null => CompletionInsertKind.Table,
        _ => CompletionInsertKind.Plain,
    };

    /// <summary>
    /// The tip beside the popup (G03): the name and, for a column, its type as
    /// a title, then what <see cref="DescriptionText"/> says about it — a
    /// column's key, reference, nullability and comment, a relation's kind,
    /// size and comment, a function's signatures and description.
    /// </summary>
    public object Description
    {
        get
        {
            var body = DescriptionText ?? KindLabel;
            var title = Kind is SqlCompletionKind.Column or SqlCompletionKind.Setting && Detail is { } type ? $"{Label}  {type}" : Label;
            var panel = new StackPanel { Spacing = 2, MaxWidth = 480 };
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            return panel;
        }
    }

    private string KindLabel => Kind switch
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
        SqlCompletionKind.Value => "value",
        SqlCompletionKind.Sequence => "sequence",
        SqlCompletionKind.Snippet => "snippet",
        SqlCompletionKind.Role => "role",
        SqlCompletionKind.Setting => "setting",
        SqlCompletionKind.Extension => "extension",
        SqlCompletionKind.Index => "index",
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
    public sealed record AcceptOptions(Func<bool> AutoAliasTables, Action<SqlCompletionData>? Accepted)
    {
        /// <summary>The letter case a keyword row is written in (Preferences, F02).</summary>
        public Func<KeywordCase>? KeywordCase { get; init; }
    }

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
        var text = document.Text;
        var caret = textArea.Caret.Offset;
        var insert = InsertTextFor(text, caret, options?.KeywordCase?.Invoke() ?? KeywordCase.AsTyped);
        var edit = CompletionEdits.Plan(text, caret, insert, InsertKind, aliasSeed, CaretIndex, ReplaceFrom);
        if (AppendClause is { } clause)
        {
            edit = CompletionEdits.AppendClause(text, edit, clause);
        }

        document.Replace(edit.ReplaceStart, edit.ReplaceLength, edit.InsertText);
        textArea.Caret.Offset = Math.Clamp(edit.CaretOffset, 0, document.TextLength);
        options?.Accepted?.Invoke(this);
    }
}
