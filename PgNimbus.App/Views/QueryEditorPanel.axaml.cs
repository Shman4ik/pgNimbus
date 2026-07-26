using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Search;
using PgNimbus.App.Completion;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;
using PgNimbus.Core.Text;

namespace PgNimbus.App.Views;

/// <summary>
/// The SQL editor surface, peeled out of MainWindow (UI design rule 7). It owns
/// every editor interaction: the ViewModel↔AvaloniaEdit text sync, completion
/// popup + fuzzy filter + auto-alias, syntax highlighting + theme rewrite,
/// matching-bracket highlight, auto-close pairs, font zoom, find/replace, and
/// the drag-drop identifier drop target.
///
/// DataContext is inherited from the host window (a <see cref="MainViewModel"/>):
/// the editor edits whichever tab is active and draws on window-level services
/// (the shared completion provider, the global word-wrap preference, and the
/// Format / Expand-* / Find palette events), so a per-tab sub-ViewModel wouldn't
/// carry what it needs. The host resolves the panel by name for the two things
/// only it can drive: F6 focus hand-off and Ctrl+F/Ctrl+H open-search.
/// </summary>
public partial class QueryEditorPanel : UserControl
{
    private MainViewModel? _model;
    // The tab the shared editor currently reflects. Each tab keeps its own Sql;
    // this is re-pointed as MainViewModel.ActiveTab changes.
    private QueryViewModel? _activeQuery;

    // The ViewModel↔AvaloniaEdit two-way sync is manual (AvaloniaEdit's Text
    // isn't a bindable AvaloniaProperty); this guards the echo so an edit on one
    // side doesn't loop back through the other.
    private bool _suppressEditorSync;

    private CompletionWindow? _completionWindow;
    // "Accepted a moment ago" tie-breaker for the completion ranking; session-scoped.
    private readonly CompletionRecency _completionRecency = new();
    // Closer promised by OnSqlTextEntering's InsertPair verdict, written by
    // OnSqlTextEntered once the opener is in the document. '\0' = none pending.
    private char _pendingAutoCloser;
    private IHighlightingDefinition? _sqlHighlighting;
    // AvaloniaEdit's stock find/replace panel, installed on the SQL editor;
    // opened via Ctrl+F / Ctrl+H (from the host's OnKeyDown) or the palette.
    private SearchPanel? _searchPanel;
    private readonly BracketHighlightRenderer _bracketRenderer;

    private const double MinEditorFontSize = 8;
    private const double MaxEditorFontSize = 32;
    private const double DefaultEditorFontSize = 14;

    public QueryEditorPanel()
    {
        InitializeComponent();

        // Must exist before LoadSqlHighlighting - the theme pass that call
        // triggers also resolves this renderer's brush.
        _bracketRenderer = new BracketHighlightRenderer(SqlEditor.TextArea.TextView);
        LoadSqlHighlighting();

        SqlEditor.TextChanged += (_, _) =>
        {
            if (_activeQuery is null || _suppressEditorSync)
            {
                return;
            }

            _activeQuery.Sql = SqlEditor.Text;
        };

        // Feed the editor's live selection to the active tab so "Run" executes
        // just the highlighted SQL when there is a selection (see RunAsync).
        // Empty selection -> null, i.e. run the whole buffer.
        SqlEditor.TextArea.SelectionChanged += (_, _) =>
        {
            if (_activeQuery is null)
            {
                return;
            }

            var selected = SqlEditor.SelectedText;
            _activeQuery.SelectedSql = string.IsNullOrEmpty(selected) ? null : selected;
        };

        // The schema tree (drag source) lives in SchemaTreePanel; the editor is
        // the drop target — drop a quoted identifier at the pointer.
        DragDrop.SetAllowDrop(SqlEditor, true);
        SqlEditor.AddHandler(DragDrop.DragOverEvent, OnEditorDragOver);
        SqlEditor.AddHandler(DragDrop.DropEvent, OnEditorDrop);

        SqlEditor.TextArea.TextEntering += OnSqlTextEntering;
        SqlEditor.TextArea.TextEntered += OnSqlTextEntered;
        // Tunnel on the TextArea: AvaloniaEdit's editing input handler consumes
        // Enter (inserts a newline) and marks the event handled before it bubbles
        // up to the editor, so a plain bubbling KeyDown handler never sees
        // Shift+Enter. Tunneling runs us first, so our shortcuts win.
        SqlEditor.TextArea.AddHandler(KeyDownEvent, OnSqlEditorKeyDown, RoutingStrategies.Tunnel);

        // Editor niceties: current-line wash (brushes are theme-resolved in
        // ApplySqlHighlightingTheme), matching-bracket highlight, and
        // Ctrl+wheel font zoom. The wheel handler tunnels because the
        // TextView claims wheel events for scrolling before they'd bubble.
        SqlEditor.Options.HighlightCurrentLine = true;
        // Lock the text-selection wash to the fixed brand-blue token
        // (AppTextSelectionBrush in Theme.axaml, shared with every plain
        // TextBox's Style setter) instead of AvaloniaEdit's theme/OS-derived
        // default, so it matches the app's other selection surfaces and reads
        // the same on both themes. SelectionBrush lives on TextArea, not
        // TextEditor, so it can't be a XAML attribute on SqlEditor.
        if (this.TryFindResource("AppTextSelectionBrush", out var selectionBrush)
            && selectionBrush is IBrush brush)
        {
            SqlEditor.TextArea.SelectionBrush = brush;
        }
        SqlEditor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            UpdateBracketHighlight();
            // Feed the caret to the active tab too: with no selection, Explain uses it
            // to pick which statement of the buffer to explain (see ExplainTarget).
            if (_activeQuery is not null)
            {
                _activeQuery.CaretOffset = SqlEditor.CaretOffset;
            }
        };
        SqlEditor.AddHandler(PointerWheelChangedEvent, OnSqlEditorPointerWheel, RoutingStrategies.Tunnel);

        // Find & replace: AvaloniaEdit's SearchPanel handles matching,
        // highlighting, and its own Enter/Shift+Enter/Esc bindings; only
        // opening it (Ctrl/Cmd+F, Ctrl/Cmd+H) goes through the host's OnKeyDown.
        // Its match-highlight brush is theme-resolved in ApplySqlHighlightingTheme.
        // The panel wears the compact template from Theme.axaml, whose buttons are
        // wired below by name: the stock template's RoutedCommand buttons raise
        // their command from a static "last focused element" and silently no-op
        // when that routing misses the panel, so the buttons call the panel's
        // methods directly instead.
        _searchPanel = SearchPanel.Install(SqlEditor);
        _searchPanel.TemplateApplied += (_, e) =>
        {
            WireSearchPanelButton(e, "PART_FindPreviousButton", p => p.FindPrevious());
            WireSearchPanelButton(e, "PART_FindNextButton", p => p.FindNext());
            WireSearchPanelButton(e, "PART_CloseButton", p => p.Close());
            WireSearchPanelButton(e, "PART_ReplaceNextButton", p => p.ReplaceNext());
            WireSearchPanelButton(e, "PART_ReplaceAllButton", p => p.ReplaceAll());
        };

        ActualThemeVariantChanged += (_, _) => ApplySqlHighlightingTheme();
        DataContextChanged += OnDataContextChanged;
    }

    // ActualThemeVariant isn't final at construction time; re-resolve the
    // palette once the panel is in a live visual tree, and close any open
    // completion popup when the host window loses focus (a native always-on-top
    // popup would otherwise float over whatever the user switched to).
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplySqlHighlightingTheme();
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.Deactivated += OnHostDeactivated;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.Deactivated -= OnHostDeactivated;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnHostDeactivated(object? sender, EventArgs e) => _completionWindow?.Close();

    // --- Host-driven interactions ----------------------------------------
    // The two things only the window can decide: F6 focus hand-off between the
    // editor and the results grid, and opening find/replace from OnKeyDown.

    /// <summary>True when keyboard focus is inside the editor — the host's F6 uses this to hop focus.</summary>
    public bool IsEditorFocused => SqlEditor.IsKeyboardFocusWithin;

    /// <summary>Moves keyboard focus into the editor's text area.</summary>
    public void FocusEditor() => SqlEditor.TextArea.Focus();

    /// <summary>
    /// Opens the editor's find (or find &amp; replace) panel, seeding the
    /// search box with the current single-line selection the way most editors
    /// do. Reactivate focuses and selects the search box, so a second Ctrl+F
    /// while the panel is already open just puts the cursor back in it.
    /// </summary>
    public void OpenSearch(bool replaceMode)
    {
        if (_searchPanel is null)
        {
            return;
        }

        _searchPanel.IsReplaceMode = replaceMode;

        var selection = SqlEditor.SelectedText;
        if (!string.IsNullOrEmpty(selection) && !selection.Contains('\n'))
        {
            _searchPanel.SearchPattern = selection;
        }

        _searchPanel.Open();
        // Focus after the open has been laid out — Reactivate needs the panel's
        // TextBox realized, which isn't guaranteed synchronously on first open.
        Dispatcher.UIThread.Post(() => _searchPanel.Reactivate());
    }

    // Attaches a click handler to one of the compact search-panel template's
    // named buttons (see the SearchPanel ControlTheme in Theme.axaml).
    // TemplateApplied can rerun (it instantiates fresh buttons each time), so
    // attaching here never double-subscribes.
    private void WireSearchPanelButton(TemplateAppliedEventArgs e, string name, Action<SearchPanel> action)
    {
        if (e.NameScope.Find<Button>(name) is { } button)
        {
            button.Click += (_, _) =>
            {
                if (_searchPanel is { } panel)
                {
                    action(panel);
                }
            };
        }
    }

    // --- ViewModel wiring / active-tab tracking --------------------------

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnMainViewModelPropertyChanged;
            _model.FormatSqlRequested -= FormatCurrentStatement;
            _model.ExpandStarRequested -= ExpandSelectStar;
            _model.FindRequested -= OpenSearch;
        }

        _model = DataContext as MainViewModel;

        if (_model is not null)
        {
            _model.PropertyChanged += OnMainViewModelPropertyChanged;
            _model.FormatSqlRequested += FormatCurrentStatement;
            _model.ExpandStarRequested += ExpandSelectStar;
            _model.FindRequested += OpenSearch;
            AttachQuery(_model.ActiveTab);
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ActiveTab is transiently null while the tab ListBox reacts to the
        // removal of its selected item (see MainViewModel.CloseTab).
        if (e.PropertyName == nameof(MainViewModel.ActiveTab) && _model is { ActiveTab: not null })
        {
            AttachQuery(_model.ActiveTab);
        }
    }

    // Switching the active tab swaps which QueryViewModel the shared editor
    // reflects - each tab keeps its own Sql, but there's only one on-screen
    // editor, so this re-points it at the new tab.
    private void AttachQuery(QueryViewModel? query)
    {
        if (_activeQuery is not null)
        {
            _activeQuery.PropertyChanged -= OnActiveQueryPropertyChanged;
        }

        _activeQuery = query;
        if (_activeQuery is null)
        {
            return;
        }

        _activeQuery.PropertyChanged += OnActiveQueryPropertyChanged;

        _suppressEditorSync = true;
        SqlEditor.Text = _activeQuery.Sql;
        _suppressEditorSync = false;

        // Seed the newly-attached tab's caret copy from where the editor actually sits,
        // so Explain doesn't target a statement based on the previous tab's offset.
        _activeQuery.CaretOffset = SqlEditor.CaretOffset;
    }

    // ViewModel → editor half of the manual two-way sync: an external Sql change
    // (a fix suggestion applied, a browse page composed, a saved query loaded
    // into this tab) pushes into the editor under the re-entrancy guard.
    private void OnActiveQueryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QueryViewModel.Sql) || _activeQuery is null)
        {
            return;
        }

        if (SqlEditor.Text == _activeQuery.Sql)
        {
            return;
        }

        _suppressEditorSync = true;
        SqlEditor.Text = _activeQuery.Sql;
        _suppressEditorSync = false;
    }

    // --- Syntax highlighting ---------------------------------------------

    private void LoadSqlHighlighting()
    {
        using var stream = AssetLoader.Open(new Uri("avares://PgNimbus.App/Assets/PostgreSql.xshd"));
        using var reader = XmlReader.Create(stream);
        _sqlHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        ApplySqlHighlightingTheme();
    }

    // The XSHD bakes in the dark palette; the highlighter has no theme
    // awareness of its own, so the named colors are rewritten whenever the
    // actual theme variant resolves or changes.
    private void ApplySqlHighlightingTheme()
    {
        if (_sqlHighlighting is null)
        {
            return;
        }

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        SetHighlightColor("Comment", dark ? "#6A9955" : "#008000");
        SetHighlightColor("String", dark ? "#CE9178" : "#A31515");
        SetHighlightColor("Number", dark ? "#B5CEA8" : "#098658");
        SetHighlightColor("Keyword", dark ? "#569CD6" : "#0000E0");
        SetHighlightColor("Type", dark ? "#4EC9B0" : "#267F99");

        // Editor chrome that has to track the theme with the palette: a
        // barely-there wash on the caret's line (border suppressed - the
        // stock one draws a hard outline box) and a stronger accent-tinted
        // wash behind the matched bracket pair.
        var textView = SqlEditor.TextArea.TextView;
        textView.CurrentLineBackground = new SolidColorBrush(Color.Parse(dark ? "#0DFFFFFF" : "#0D000000"));
        textView.CurrentLineBorder = new Pen(Brushes.Transparent);
        _bracketRenderer.Brush = new SolidColorBrush(Color.Parse(dark ? "#40569CD6" : "#332B5FBF"));
        // Find-match highlight: same accent-tinted wash family as the bracket pair.
        _searchPanel?.SetSearchResultsBrush(new SolidColorBrush(Color.Parse(dark ? "#40569CD6" : "#332B5FBF")));

        // Reassigning is what makes the TextView drop its cached line
        // visuals and re-run the highlighter with the new brushes.
        SqlEditor.SyntaxHighlighting = null;
        SqlEditor.SyntaxHighlighting = _sqlHighlighting;
    }

    private void SetHighlightColor(string name, string hex)
    {
        if (_sqlHighlighting?.GetNamedColor(name) is { } color)
        {
            color.Foreground = new SimpleHighlightingBrush(Color.Parse(hex));
        }
    }

    // --- Drag-drop target (identifier from the schema tree) --------------

    private void OnEditorDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Formats.Contains(DataFormat.Text))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        // Live caret preview: the caret tracks the pointer so it's obvious
        // where the identifier will land.
        if (SqlEditor.GetPositionFromPoint(e.GetPosition(SqlEditor)) is { } position)
        {
            SqlEditor.TextArea.Caret.Position = position;
        }

        e.Handled = true;
    }

    private void OnEditorDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetText() is not { Length: > 0 } text)
        {
            return;
        }

        var offset = SqlEditor.GetPositionFromPoint(e.GetPosition(SqlEditor)) is { } position
            ? SqlEditor.Document.GetOffset(position.Location)
            : SqlEditor.CaretOffset;
        SqlEditor.Document.Insert(offset, text);
        SqlEditor.CaretOffset = offset + text.Length;
        SqlEditor.TextArea.Focus();
        e.Handled = true;
    }

    // --- Auto-close pairs -------------------------------------------------
    // OnSqlTextEntering runs before AvaloniaEdit inserts the typed character:
    // TypeOver must suppress the insertion entirely, and an InsertPair verdict
    // needs the pre-insert text (AutoClosePairs.Decide's contract). The closer
    // itself is written in OnSqlTextEntered, after the opener exists.
    private void OnSqlTextEntering(object? sender, TextInputEventArgs e)
    {
        _pendingAutoCloser = '\0';
        if (e.Text is not { Length: 1 } entered || entered[0] is not ('(' or ')' or '\'' or '"'))
        {
            return;
        }

        var typed = entered[0];
        var textArea = SqlEditor.TextArea;

        // Typing an opener over a selection wraps it instead of replacing it.
        if (typed is not ')' && !textArea.Selection.IsEmpty)
        {
            textArea.Selection.ReplaceSelectionWithText(
                typed + textArea.Selection.GetText() + AutoClosePairs.CloserFor(typed));
            e.Handled = true;
            return;
        }

        var text = SqlEditor.Text;
        var caret = SqlEditor.CaretOffset;
        var inStringOrComment = SqlCompletionContext.GetCaretContext(text, caret).InStringOrComment;
        switch (AutoClosePairs.Decide(text, caret, typed, inStringOrComment))
        {
            case AutoClosePairs.Verdict.TypeOver:
                SqlEditor.CaretOffset = caret + 1;
                e.Handled = true;
                break;
            case AutoClosePairs.Verdict.InsertPair:
                _pendingAutoCloser = AutoClosePairs.CloserFor(typed);
                break;
        }
    }

    private void OnSqlTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        var c = e.Text[0];

        // The closer OnSqlTextEntering promised: write it after the caret so
        // the pair hugs it — "(|)" — and typing continues between them.
        if (_pendingAutoCloser != '\0')
        {
            var closer = _pendingAutoCloser;
            _pendingAutoCloser = '\0';
            var openerEnd = SqlEditor.CaretOffset;
            SqlEditor.Document.Insert(openerEnd, closer.ToString());
            SqlEditor.CaretOffset = openerEnd;
            return;
        }

        // A dot starts member access (alias./table./schema.). Re-trigger even
        // when a bare-identifier list is already open, so it switches to the
        // qualifier's columns instead of staying on the catalog-wide list.
        if (c == '.')
        {
            _completionWindow?.Close();
            ShowCompletion(includeTypedChar: false);
            return;
        }

        if (_completionWindow is not null)
        {
            return;
        }

        if (char.IsLetter(c) || c == '_')
        {
            ShowCompletion(includeTypedChar: true);
            return;
        }

        // A comma continuing a list (SELECT list, FROM list, GROUP/ORDER BY …)
        // reopens the list on the spot — the next item is as predictable as the
        // first one was right after the clause keyword.
        if (c == ',' && CaretIsInKnownClause())
        {
            ShowCompletion(includeTypedChar: false);
            return;
        }

        if (c != ' ')
        {
            return;
        }

        // The space right after a clause keyword (FROM/WHERE/SELECT/AND …)
        // opens the list unprompted — the spots where what comes next is most
        // predictable (ON is where the FK join-condition suggestion shows up,
        // when there is one). A space right after a comma re-opens the list the
        // comma itself opened (the space closed it by matching nothing).
        var caret = SqlEditor.CaretOffset;
        var text = SqlEditor.Text;
        var beforeSpace = caret >= 2 && caret <= text.Length ? text[caret - 2] : '\0';
        if (beforeSpace == ',' ? CaretIsInKnownClause() : WordBeforeCaretTriggersAutoOpen())
        {
            ShowCompletion(includeTypedChar: false);
        }
    }

    // True when the caret sits in a recognized clause (table position, select
    // list, predicate…) outside strings/comments — the contexts where the
    // popup's contents are scoped enough to be worth opening unasked.
    private bool CaretIsInKnownClause()
    {
        var context = SqlCompletionContext.GetCaretContext(SqlEditor.Text, SqlEditor.CaretOffset);
        return !context.InStringOrComment && context.Clause != SqlClause.None;
    }

    // The keywords whose trailing space auto-opens the popup: the ones after
    // which the very next token is predictable — a table (FROM/JOIN/INTO/
    // UPDATE), a scoped column (WHERE/ON/AND/OR), or a select-list expression.
    private static readonly string[] AutoOpenKeywords =
        ["from", "join", "into", "update", "on", "where", "and", "or", "select"];

    // True when the word just left of the caret (which sits right after the
    // freshly typed space) is a keyword after which the popup should open itself.
    private bool WordBeforeCaretTriggersAutoOpen()
    {
        var text = SqlEditor.Text;
        var end = Math.Min(SqlEditor.CaretOffset, text.Length) - 1; // skip the space
        if (end <= 0)
        {
            return false;
        }

        var start = end;
        while (start > 0 && (char.IsLetter(text[start - 1]) || text[start - 1] == '_'))
        {
            start--;
        }

        var word = text.AsSpan(start, Math.Max(end - start, 0));
        foreach (var keyword in AutoOpenKeywords)
        {
            if (word.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // --- Key handling / font zoom / bracket match ------------------------

    private void OnSqlEditorKeyDown(object? sender, KeyEventArgs e)
    {
        // The find/replace panel lives inside the TextArea, so this tunneled
        // handler also sees keys typed into its text boxes - without this
        // guard, Shift+Enter there runs the query instead of find-previous.
        if (e.Source is Visual source && source.FindAncestorOfType<SearchPanel>() is not null)
        {
            return;
        }

        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            ShowCompletion(includeTypedChar: false);
            e.Handled = true;
            return;
        }

        // Smart execution: runs just the statement the caret sits in (between
        // ;s) rather than the whole tab, so trying one statement out of a
        // multi-statement script doesn't require selecting it by hand first.
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Shift)
        {
            if (_activeQuery is { } query
                && SqlScriptSplitter.StatementAt(SqlEditor.Text, SqlEditor.CaretOffset) is { } statement)
            {
                _ = query.RunStatementAsync(statement);
            }

            e.Handled = true;
            return;
        }

        // Format the statement under the caret. All three shift-F combos fire it,
        // regardless of the active hotkey scheme: Ctrl+Shift+F and Cmd+Shift+F
        // (the two platform combos, as before) plus Alt+Shift+F (the
        // IntelliJ/VS Code convention). A deliberate exception to the
        // Hotkeys.Command routing — accepting every modifier is harmless here
        // (nothing else binds them) and means the muscle-memory combo works
        // whatever platform the user came from.
        if (e.Key == Key.F
            && (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)
                || e.KeyModifiers == (KeyModifiers.Meta | KeyModifiers.Shift)
                || e.KeyModifiers == (KeyModifiers.Alt | KeyModifiers.Shift)))
        {
            FormatCurrentStatement();
            e.Handled = true;
            return;
        }

        // Font-size zoom: Ctrl+= / Ctrl+- step, Ctrl+0 resets (numpad
        // variants included; Cmd on the mac scheme). Ctrl+wheel does the same
        // via the tunneled pointer handler. Shift is tolerated because
        // "Ctrl and +" is physically Ctrl+Shift+= on most layouts.
        if (e.KeyModifiers.HasFlag(Hotkeys.Command) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    AdjustEditorFontSize(+1);
                    e.Handled = true;
                    break;
                case Key.OemMinus or Key.Subtract:
                    AdjustEditorFontSize(-1);
                    e.Handled = true;
                    break;
                case Key.D0 or Key.NumPad0:
                    SqlEditor.FontSize = DefaultEditorFontSize;
                    e.Handled = true;
                    break;
            }
        }
    }

    private void OnSqlEditorPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(Hotkeys.Command))
        {
            return;
        }

        AdjustEditorFontSize(e.Delta.Y >= 0 ? +1 : -1);
        e.Handled = true;
    }

    private void AdjustEditorFontSize(int delta) =>
        SqlEditor.FontSize = Math.Clamp(SqlEditor.FontSize + delta, MinEditorFontSize, MaxEditorFontSize);

    private void UpdateBracketHighlight() =>
        // Pass the live document, not SqlEditor.Text — the latter allocates a
        // full-document string on every caret move, this reads a few chars.
        _bracketRenderer.Update(SqlEditor.Document, SqlEditor.CaretOffset);

    // --- Completion popup -------------------------------------------------

    private void ShowCompletion(bool includeTypedChar)
    {
        var data = _model?.CompletionProvider.GetCompletionData(SqlEditor.Text, SqlEditor.CaretOffset);
        if (data is not { Count: > 0 })
        {
            return;
        }

        var completionWindow = new CompletionWindow(SqlEditor.TextArea);
        // The stock filter is prefix/substring-only and can't be swapped out
        // (SelectItem isn't virtual, the list isn't replaceable), so it's turned
        // off and ApplyFuzzyFilter below owns filtering + ranking instead. What
        // remains of the stock path (SelectItemWithStart on every caret move) only
        // touches the selection, and the re-rank that runs right after overrides it.
        completionWindow.CompletionList.IsFiltering = false;
        if (includeTypedChar)
        {
            completionWindow.StartOffset -= 1;
        }

        if (!ApplyFuzzyFilter(completionWindow, data))
        {
            return; // nothing matches the already-typed character — never show
        }

        // Stock AvaloniaEdit only moves the *selection* as the user keeps typing;
        // re-filtering the visible items is on us, from the same caret event it uses.
        // Registered after the window's own handler, so this runs second and wins.
        EventHandler caretMoved = (_, _) =>
        {
            if (_completionWindow != completionWindow)
            {
                return; // already closed by the stock handler in this same event
            }

            if (!ApplyFuzzyFilter(completionWindow, data))
            {
                completionWindow.Hide(); // fuzzy-matches nothing — done, not "show all"
            }
        };
        SqlEditor.TextArea.Caret.PositionChanged += caretMoved;

        // On accept: feed the "picked it recently" ranking tie-breaker, and
        // append the auto-alias when a table just landed after FROM/JOIN. The
        // alias insert is posted, not run inline: this handler's order relative
        // to the window's own (which writes the completion text) isn't
        // guaranteed — text inserted before Complete() runs sits inside the
        // completion segment and gets replaced away with the filter word.
        completionWindow.CompletionList.InsertionRequested += (_, _) =>
        {
            if (completionWindow.CompletionList.SelectedItem is SqlCompletionData accepted)
            {
                _completionRecency.Record(accepted.Text);
                Dispatcher.UIThread.Post(() => MaybeInsertTableAlias(accepted));
            }
        };

        completionWindow.Closed += (_, _) =>
        {
            SqlEditor.TextArea.Caret.PositionChanged -= caretMoved;
            _completionWindow = null;
        };
        completionWindow.Show();
        _completionWindow = completionWindow;
    }

    // Appends the short auto-alias after a table accepted in FROM/JOIN position
    // ("FROM public.orders" → "FROM public.orders o") so the "o." member-access
    // flow works immediately — deduped against every name the statement already
    // uses (aliases, table names, CTEs). Gated by the persisted "AS" toggle and
    // re-checked against the clause at the caret, because the same table item
    // can be accepted in places where an alias is wrong (SELECT list) or
    // illegal (INSERT INTO / TRUNCATE targets).
    private void MaybeInsertTableAlias(SqlCompletionData accepted)
    {
        if (accepted.AliasTable is null || _model is not { AutoAliasTables: true })
        {
            return;
        }

        var text = SqlEditor.Text;
        var caret = SqlEditor.CaretOffset;
        var context = SqlCompletionContext.GetCaretContext(text, caret);
        if (context.Clause is not (SqlClause.FromTableRef or SqlClause.JoinTableRef))
        {
            return;
        }

        var taken = new List<string>();
        foreach (var table in SqlCompletionContext.ExtractTables(text))
        {
            taken.Add(table.Table);
            if (table.Alias is not null)
            {
                taken.Add(table.Alias);
            }
        }

        taken.AddRange(SqlCompletionContext.ExtractCteNames(text));

        var alias = TableAliaser.Derive(accepted.AliasTable, taken);
        SqlEditor.Document.Insert(caret, " " + alias);
        SqlEditor.CaretOffset = caret + alias.Length + 1;
    }

    // Re-ranks the candidate set against the segment typed since the popup opened
    // and pushes the result into the list. False when nothing matches (caller
    // hides the window). Clamp defensively: StartOffset -= 1 above (or
    // AvaloniaEdit's own offset bookkeeping) must never be allowed to slice out
    // of document bounds and crash the app.
    private bool ApplyFuzzyFilter(CompletionWindow completionWindow, IReadOnlyList<SqlCompletionData> data)
    {
        var document = SqlEditor.Document;
        var start = Math.Max(0, completionWindow.StartOffset);
        var caret = Math.Clamp(SqlEditor.CaretOffset, start, document.TextLength);
        var query = document.GetText(start, caret - start);

        var ranked = CompletionRanker.Rank(
            data, query, static d => d.Text, static d => d.Priority, d => _completionRecency.RankOf(d.Text));
        if (ranked.Items.Count == 0)
        {
            return false;
        }

        // CompletionData is a plain list the ListBox binds once at template time —
        // mutating it alone changes nothing on screen. Keep it in sync (the stock
        // selection-only pass indexes into it) and rebind ItemsSource for the
        // visible refresh, exactly like the stock filtering path does.
        var list = completionWindow.CompletionList;
        list.CompletionData.Clear();
        foreach (var item in ranked.Items)
        {
            list.CompletionData.Add(item);
        }

        list.ListBox.ItemsSource = ranked.Items;
        var selected = ranked.Items[ranked.SelectedIndex];
        list.SelectedItem = selected;
        list.ScrollIntoView(selected);
        return true;
    }

    // --- Format / expand-star (palette + Shift-F) ------------------------

    // Pretty-prints the statement under the caret and replaces just that span, so
    // formatting one statement in a multi-statement script leaves the others alone.
    // Puts the caret at the end of the reformatted text. A no-op when the caret
    // isn't in a statement or the formatter left the text unchanged.
    private void FormatCurrentStatement()
    {
        var text = SqlEditor.Text;
        if (SqlScriptSplitter.StatementSpanAt(text, SqlEditor.CaretOffset) is not { } span)
        {
            return;
        }

        var (start, end) = span;
        var formatted = SqlFormatter.Format(text[start..end]);
        if (formatted == text[start..end])
        {
            return;
        }

        SqlEditor.Document.Replace(start, end - start, formatted);
        SqlEditor.CaretOffset = start + formatted.Length;
    }

    // Palette "Expand SELECT *": replace the star(s) in the statement under
    // the caret with the explicit column list — CTEs and catalog tables both
    // resolve (see SqlCompletionProvider.ExpandSelectStar). A no-op when
    // there's no star or a table is unknown: better nothing than a wrong list.
    private void ExpandSelectStar()
    {
        if (_model?.CompletionProvider.ExpandSelectStar(SqlEditor.Text, SqlEditor.CaretOffset) is not { } expansion)
        {
            return;
        }

        SqlEditor.Document.Replace(expansion.Start, expansion.Length, expansion.Replacement);
        SqlEditor.CaretOffset = expansion.Start + expansion.Replacement.Length;
    }
}
