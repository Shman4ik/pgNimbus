using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using PgNimbus.Core.Text;

namespace PgNimbus.App.Completion;

/// <summary>
/// A completion row's name with the letters the typed text matched drawn in
/// bold (docs/dev/design/sql-completion-audit-2.md G01): with fuzzy matching, a row
/// like <c>order_items</c> for "oi" otherwise gives no clue why it is there.
/// <see cref="Query"/> is what the popup filters on (the list's Tag), and
/// <see cref="MatchText"/> the name the ranker matched, which a qualified
/// label (<c>u.id</c>) contains.
/// </summary>
public sealed class CompletionLabel : TextBlock
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<CompletionLabel, string?>(nameof(Label));

    public static readonly StyledProperty<string?> MatchTextProperty =
        AvaloniaProperty.Register<CompletionLabel, string?>(nameof(MatchText));

    public static readonly StyledProperty<object?> QueryProperty =
        AvaloniaProperty.Register<CompletionLabel, object?>(nameof(Query));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? MatchText
    {
        get => GetValue(MatchTextProperty);
        set => SetValue(MatchTextProperty, value);
    }

    public object? Query
    {
        get => GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty || change.Property == MatchTextProperty || change.Property == QueryProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var label = Label ?? "";
        var query = Query as string ?? "";
        var match = MatchText ?? label;
        var offset = label.IndexOf(match, StringComparison.Ordinal);
        var positions = query.Length == 0 || offset < 0 ? [] : CompletionRanker.MatchedPositions(match, query);
        if (positions.Count == 0)
        {
            Inlines = null;
            Text = label;
            return;
        }

        var bold = new HashSet<int>(positions.Select(p => p + offset));
        var inlines = new InlineCollection();
        var start = 0;
        for (var i = 1; i <= label.Length; i++)
        {
            if (i == label.Length || bold.Contains(i) != bold.Contains(start))
            {
                inlines.Add(new Run(label[start..i]) { FontWeight = bold.Contains(start) ? FontWeight.Bold : FontWeight.Normal });
                start = i;
            }
        }

        Inlines = inlines;
    }
}
