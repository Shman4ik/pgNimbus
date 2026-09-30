using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AvaloniaEdit;

namespace PgNimbus.App.Views;

/// <summary>
/// Read-only, selectable text that stays cheap however long it gets: a
/// <see cref="SelectableTextBlock"/> in a <see cref="ScrollViewer"/> up to
/// <see cref="LargeTextThreshold"/> characters, a read-only AvaloniaEdit editor past it.
/// </summary>
/// <remarks>
/// A text block lays out every line it holds, on the UI thread, before the first one
/// is drawn: a 300 KB jsonb value pretty-printed in the cell inspector, the text of
/// an EXPLAIN ANALYZE over a thousand partitions, or a review of 100,000 staged
/// DELETEs froze the window for seconds. The editor lays out only the lines on screen.
/// Short text keeps the text block, so what most of these panes show, and the
/// screenshots of them, is unchanged. The editor is built the first time it's needed.
/// </remarks>
public sealed class ReadOnlyTextView : UserControl
{
    /// <summary>The length from which the text goes to the editor instead of a text block.</summary>
    public const int LargeTextThreshold = 32 * 1024;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ReadOnlyTextView, string?>(nameof(Text));

    public static readonly StyledProperty<bool> WordWrapProperty =
        AvaloniaProperty.Register<ReadOnlyTextView, bool>(nameof(WordWrap));

    public static readonly StyledProperty<Thickness> TextMarginProperty =
        AvaloniaProperty.Register<ReadOnlyTextView, Thickness>(nameof(TextMargin));

    private readonly ScrollViewer _scroller;
    private readonly SelectableTextBlock _block;
    private TextEditor? _editor;

    public ReadOnlyTextView()
    {
        _block = new SelectableTextBlock();
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _block,
        };
        Content = new Panel { Children = { _scroller } };
    }

    /// <summary>The text shown.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Whether long lines wrap.</summary>
    public bool WordWrap
    {
        get => GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    /// <summary>The space around the text, inside the scrolling area.</summary>
    public Thickness TextMargin
    {
        get => GetValue(TextMarginProperty);
        set => SetValue(TextMarginProperty, value);
    }

    /// <summary>True while the text is long enough to be shown in the editor (for tests).</summary>
    public bool IsShowingEditor => _editor is { IsVisible: true };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == WordWrapProperty || change.Property == TextMarginProperty)
        {
            Update();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplySelectionBrush();
    }

    private void Update()
    {
        var text = Text ?? string.Empty;
        if (text.Length < LargeTextThreshold)
        {
            _block.Text = text;
            _block.TextWrapping = WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
            _block.Margin = TextMargin;
            _scroller.IsVisible = true;
            if (_editor is not null)
            {
                _editor.IsVisible = false;
                _editor.Text = string.Empty;
            }

            return;
        }

        var editor = EnsureEditor();
        _block.Text = null;
        _scroller.IsVisible = false;
        editor.WordWrap = WordWrap;
        editor.Padding = TextMargin;
        if (!string.Equals(editor.Text, text, StringComparison.Ordinal))
        {
            editor.Text = text;
            editor.ScrollToHome();
        }

        editor.IsVisible = true;
    }

    private TextEditor EnsureEditor()
    {
        if (_editor is not null)
        {
            return _editor;
        }

        _editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = false,
            Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        ((Panel)Content!).Children.Add(_editor);
        ApplySelectionBrush();
        return _editor;
    }

    // The fixed brand-blue wash every text box and editor selects with (see
    // QueryEditorPanel.ApplyTextSelectionBrush), resolved once attached.
    private void ApplySelectionBrush()
    {
        if (_editor is not null
            && this.TryFindResource("AppTextSelectionBrush", ActualThemeVariant, out var resource)
            && resource is IBrush brush)
        {
            _editor.TextArea.SelectionBrush = brush;
        }
    }
}
