using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Commands;
using PgNimbus.Core.Json;

namespace PgNimbus.App.Views;

/// <summary>
/// Shows one cell's whole value for a <see cref="CellInspectorViewModel"/>: the
/// read-only viewer, the JSON tree and the edit-mode editor, each drawing JSON
/// in the same colours (<see cref="JsonSyntax"/>). Hosted by the results grid's
/// cell inspector and by the notify monitor's payload pane.
///
/// AvaloniaEdit's <c>Text</c> isn't a bindable property, so the two editors are
/// synced by hand: the viewer follows <see cref="CellInspectorViewModel.DisplayText"/>,
/// and the editor is the two-way half of <see cref="CellInspectorViewModel.EditText"/>
/// under a re-entrancy guard (the same shape as the SQL editor's).
/// </summary>
public partial class CellValueView : UserControl
{
    // How long typing has to pause before the editor's folds are worked out
    // again: a pass is linear in the text, and a large document is typed into
    // a keystroke at a time.
    private static readonly TimeSpan RefoldDelay = TimeSpan.FromMilliseconds(300);

    // The editor's floor, in lines, when it sizes to its text.
    private const int MinEditorLines = 8;

    // The text height the last measure sized to; a change re-measures.
    private double _measuredTextHeight;

    private CellInspectorViewModel? _model;
    private bool _suppressEditorSync;
    private FoldingManager? _viewerFolds;
    private FoldingManager? _editorFolds;
    private readonly DispatcherTimer _refoldTimer;

    public CellValueView()
    {
        InitializeComponent();

        EditorDefaults.Apply(Viewer);
        EditorDefaults.Apply(JsonInspectorEditor);
        // A value is read to its end, not scrolled past it: AvaloniaEdit's
        // default room below the last line (all but one line of the text again)
        // put a scroll bar beside a value that fits.
        Viewer.Options.AllowScrollBelowDocument = false;
        JsonInspectorEditor.Options.AllowScrollBelowDocument = false;
        // Each editor's own find bar exists once its template is applied.
        foreach (var editor in new[] { Viewer, JsonInspectorEditor })
        {
            editor.TemplateApplied += (_, _) =>
            {
                if (editor.SearchPanel is { } panel)
                {
                    EditorDefaults.WireSearchButtons(panel);
                }
            };
        }

        _refoldTimer = new DispatcherTimer { Interval = RefoldDelay };
        _refoldTimer.Tick += (_, _) =>
        {
            _refoldTimer.Stop();
            UpdateFolds(JsonInspectorEditor, ref _editorFolds, _model?.IsJson == true);
        };

        // Editor → ViewModel half of the manual two-way sync.
        JsonInspectorEditor.TextChanged += (_, _) =>
        {
            if (_model is null || _suppressEditorSync)
            {
                return;
            }

            _model.EditText = JsonInspectorEditor.Text;
            _refoldTimer.Stop();
            _refoldTimer.Start();
        };

        // The Run chord commits what is being typed, the way it runs what is
        // typed in the SQL editor. Tunnelled, because AvaloniaEdit would take the
        // Enter. The key only gets here because the window's own Run binding is
        // off while the inspector is open (MainWindow.ResolveCommand): a window
        // binding sees a key before the focused editor, and used to re-run the
        // query behind the overlay, reloading the grid under the cell.
        JsonInspectorEditor.TextArea.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);

        // The card is sized to the text (see MeasureOverride), and the text's
        // height is known only as its lines are laid out.
        foreach (var editor in new[] { Viewer, JsonInspectorEditor })
        {
            editor.TextArea.TextView.VisualLinesChanged += (_, _) =>
            {
                if (Math.Abs(editor.TextArea.TextView.DocumentHeight - _measuredTextHeight) > 0.5)
                {
                    InvalidateMeasure();
                }
            };
        }

        Tree.SelectionChanged += (_, _) =>
        {
            if (_model is not null)
            {
                _model.SelectedNode = Tree.SelectedItem as JsonTreeNode;
            }
        };
        // A right-click acts on the row under the pointer, so it selects it first.
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);

        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        DataContextChanged += OnDataContextChanged;
    }

    // App-level resources (the selection brush, the JSON palette) resolve only
    // once the control is in a live tree: see ResultsGridPanel's
    // ApplyTextSelectionBrush for the landmine this avoids.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyTheme();
    }

    /// <summary>
    /// Asks for the height the text needs, not all there is. AvaloniaEdit's view
    /// scrolls logically and wants every pixel it is offered, so a three-line
    /// value made the inspector card as tall as the window; the old text block
    /// sized to its text, and so does this, up to what the host allows. The
    /// editor keeps room for a few lines more than it holds, to type into.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var editor = _model switch
        {
            { IsEditing: true } => JsonInspectorEditor,
            { ShowText: true } => Viewer,
            _ => null,
        };
        if (editor is null)
        {
            return base.MeasureOverride(availableSize);
        }

        var view = editor.TextArea.TextView;
        _measuredTextHeight = view.DocumentHeight;
        var lines = editor == JsonInspectorEditor
            ? Math.Max(_measuredTextHeight, MinEditorLines * view.DefaultLineHeight)
            : _measuredTextHeight;
        // The border and padding around the text, and room for the horizontal
        // scroll bar a line too long to wrap brings.
        var chrome = editor.Padding.Top + editor.Padding.Bottom + 6 + (editor.WordWrap ? 0 : 18);

        // Measured at that height, not trimmed to it afterwards: the view takes
        // its viewport from the size it is measured with, and one measured tall
        // then arranged short kept scrolling as if it were tall.
        return base.MeasureOverride(availableSize.WithHeight(Math.Min(availableSize.Height, lines + chrome)));
    }

    /// <summary>
    /// Opens the find bar of whichever text is showing (the viewer or the
    /// editor). False when the tree is showing, which has no text to search.
    /// </summary>
    public bool OpenSearch()
    {
        var editor = _model switch
        {
            { IsEditing: true } => JsonInspectorEditor,
            { ShowText: true } => Viewer,
            _ => null,
        };
        if (editor?.SearchPanel is not { } panel)
        {
            return false;
        }

        var selection = editor.SelectedText;
        if (!string.IsNullOrEmpty(selection) && !selection.Contains('\n'))
        {
            panel.SearchPattern = selection;
        }

        panel.Open();
        Dispatcher.UIThread.Post(panel.Reactivate);
        return true;
    }

    /// <summary>
    /// True while the viewer's or the editor's find bar is open. Escape belongs
    /// to the find bar then, wherever in that editor focus is: AvaloniaEdit
    /// closes it on Escape from the bar and from the text alike.
    /// </summary>
    public bool IsSearchOpen =>
        Viewer.SearchPanel is { IsClosed: false } || JsonInspectorEditor.SearchPanel is { IsClosed: false };

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
        }

        _model = DataContext as CellInspectorViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
        }

        ShowValue();
        ShowEditText();
        Tree.SelectedItem = _model?.SelectedNode;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CellInspectorViewModel.DisplayText):
                ShowValue();
                break;
            case nameof(CellInspectorViewModel.IsJson):
                ApplyValueKind();
                break;
            case nameof(CellInspectorViewModel.EditText):
                ShowEditText();
                break;
            case nameof(CellInspectorViewModel.IsEditing) when _model?.IsEditing == true:
                // Focus the editor once it is on screen, so typing starts at once.
                Dispatcher.UIThread.Post(() => JsonInspectorEditor.TextArea.Focus());
                break;
            case nameof(CellInspectorViewModel.TreeRoots):
                // A new document: nothing in it is selected yet.
                Tree.SelectedItem = null;
                break;
            case nameof(CellInspectorViewModel.SelectedNode) when !ReferenceEquals(Tree.SelectedItem, _model?.SelectedNode):
                Tree.SelectedItem = _model?.SelectedNode;
                break;
        }
    }

    private void ShowValue()
    {
        Viewer.Text = _model?.DisplayText ?? string.Empty;
        Viewer.ScrollToHome();
        ApplyValueKind();
    }

    // Colour and folds follow whether the value is JSON; the editor shares the
    // value's kind, so a JSON cell is edited in colour too.
    private void ApplyValueKind()
    {
        var isJson = _model?.IsJson == true;
        var highlighting = isJson ? JsonSyntax.For(ActualThemeVariant) : null;
        Viewer.SyntaxHighlighting = highlighting;
        JsonInspectorEditor.SyntaxHighlighting = highlighting;
        UpdateFolds(Viewer, ref _viewerFolds, isJson);
        UpdateFolds(JsonInspectorEditor, ref _editorFolds, isJson);
    }

    // ViewModel → editor half: entering edit mode, Format, Minify.
    private void ShowEditText()
    {
        var text = _model?.EditText ?? string.Empty;
        if (JsonInspectorEditor.Text == text)
        {
            return;
        }

        _suppressEditorSync = true;
        JsonInspectorEditor.Text = text;
        _suppressEditorSync = false;
        UpdateFolds(JsonInspectorEditor, ref _editorFolds, _model?.IsJson == true);
    }

    // Folding for JSON only: installed for a JSON value, uninstalled for any
    // other, whose gutter would otherwise keep an empty folding margin.
    private static void UpdateFolds(TextEditor editor, ref FoldingManager? manager, bool isJson)
    {
        if (!isJson)
        {
            if (manager is not null)
            {
                FoldingManager.Uninstall(manager);
                manager = null;
            }

            return;
        }

        manager ??= FoldingManager.Install(editor.TextArea);
        var folds = JsonFolding.Find(editor.Text);
        var sections = new List<NewFolding>(folds.Count);
        foreach (var fold in folds)
        {
            sections.Add(new NewFolding(fold.Start, fold.End) { Name = fold.Title });
        }

        manager.UpdateFoldings(sections, -1);
    }

    private void ApplyTheme()
    {
        var variant = ActualThemeVariant;
        if (this.TryFindResource("AppTextSelectionBrush", variant, out var selection) && selection is IBrush brush)
        {
            Viewer.TextArea.SelectionBrush = brush;
            JsonInspectorEditor.TextArea.SelectionBrush = brush;
        }

        Viewer.SearchResultsBrush = EditorDefaults.SearchResultsBrush(variant);
        JsonInspectorEditor.SearchResultsBrush = EditorDefaults.SearchResultsBrush(variant);

        // Reassigning is what makes the TextView drop its cached line visuals.
        var highlighting = _model?.IsJson == true ? JsonSyntax.For(variant) : null;
        Viewer.SyntaxHighlighting = null;
        Viewer.SyntaxHighlighting = highlighting;
        JsonInspectorEditor.SyntaxHighlighting = null;
        JsonInspectorEditor.SyntaxHighlighting = highlighting;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (!CommandBindings.Matches(CommandId.Run, e))
        {
            return;
        }

        e.Handled = true;
        if (_model is { IsEditing: true, HasValidationError: false })
        {
            _model.SaveCommand.Execute(null);
        }
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(Tree).Properties.IsRightButtonPressed
            && e.Source is Visual source
            && source.FindAncestorOfType<TreeViewItem>(includeSelf: true) is { DataContext: JsonTreeNode node })
        {
            Tree.SelectedItem = node;
        }
    }

    // --- Tree context menu -------------------------------------------------

    private void OnCopyValueClick(object? sender, RoutedEventArgs e) =>
        Copy(node => _model?.NodeValue(node));

    private void OnCopySqlPathClick(object? sender, RoutedEventArgs e) =>
        Copy(node => _model?.SqlPath(node));

    private void OnCopyJsonPathClick(object? sender, RoutedEventArgs e) =>
        Copy(CellInspectorViewModel.JsonPath);

    private async void Copy(Func<JsonTreeNode, string?> text)
    {
        if (Tree.SelectedItem is not JsonTreeNode node
            || text(node) is not { } value
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(value);
        }
        catch
        {
            // Another app can hold the clipboard locked. This is an async void
            // handler, so a throw would crash the app: a failed copy isn't worth it.
        }
    }
}
