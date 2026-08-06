using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Nimbus.Ui.Controls;

/// <summary>
/// A dismissable panel drawn over the window it belongs to: dimmed backdrop, a
/// centred <c>layer</c> card, a title row with a close button. The Nimbus answer to
/// "this needs its own surface" — the cheat sheet, the About box, preferences.
/// <para>
/// A secondary <see cref="Window"/> was the obvious alternative and is worse in this
/// family for three reasons. It arrives with an OS-painted caption the app does not
/// control (which is the entire reason <c>ThemedWindowChrome</c> has to pin the DWM
/// caption colour on Windows — a dialog opened from a Light app on a Dark desktop
/// gets a black title bar); it is a second entry in Alt+Tab and the taskbar for
/// something that is not a second place to be; and it renders in its own chrome
/// rather than inside the two-tone shell, so it never quite looks like the app that
/// opened it. An overlay has none of those problems and costs a backdrop.
/// </para>
/// <para>
/// It does cost one thing, and it is real: an overlay covers the window, so anything
/// you would want to *watch* while the panel is open has to be a window instead. That
/// is the line — a reference view you read beside your work stays a window; a panel
/// you open, use and dismiss becomes one of these.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsOpen"/> binds two-way and is the only way in or out: the backdrop, the
/// close button and Escape all just set it to false. Deliberately no close
/// <c>Command</c> alongside it — a control wired with both a two-way state property and
/// a command that inverts the same state flips it twice per gesture and lands where it
/// started (kubeNimbus's UI rule 8b, DESIGN.md rule 6; that bug shipped three times
/// there before it was written down).
/// </para>
/// <para>
/// Escape is handled on the <see cref="TopLevel"/> rather than here, because nothing
/// inside the panel need hold focus for it to work — a cheat sheet has nothing to focus.
/// It is a <see cref="RoutingStrategies.Bubble"/> handler on purpose: bubbling reaches
/// the top level *after* the focused control, so a search box inside another overlay
/// still gets first refusal on the key.
/// </para>
/// </remarks>
[TemplatePart("PART_Backdrop", typeof(Border))]
[TemplatePart("PART_CloseButton", typeof(Button))]
public class OverlayPanel : ContentControl
{
    /// <summary>Whether the panel is showing. Two-way by default: the panel closes itself.</summary>
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<OverlayPanel, bool>(
            nameof(IsOpen), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The heading in the panel's own title row.</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<OverlayPanel, string?>(nameof(Title));

    /// <summary>
    /// The card's width. Not <see cref="Layoutable.Width"/>: that one belongs to the
    /// backdrop, which fills the window.
    /// </summary>
    public static readonly StyledProperty<double> PanelWidthProperty =
        AvaloniaProperty.Register<OverlayPanel, double>(nameof(PanelWidth), 520);

    /// <summary>
    /// The card's height ceiling. The card sizes to its content up to this, so a short
    /// panel (the About box) is short and a long one (the cheat sheet) scrolls.
    /// </summary>
    public static readonly StyledProperty<double> PanelMaxHeightProperty =
        AvaloniaProperty.Register<OverlayPanel, double>(nameof(PanelMaxHeight), 640);

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public double PanelWidth
    {
        get => GetValue(PanelWidthProperty);
        set => SetValue(PanelWidthProperty, value);
    }

    public double PanelMaxHeight
    {
        get => GetValue(PanelMaxHeightProperty);
        set => SetValue(PanelMaxHeightProperty, value);
    }

    private Border? _backdrop;
    private Button? _closeButton;
    private TopLevel? _topLevel;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_backdrop is not null)
        {
            _backdrop.PointerPressed -= OnBackdropPressed;
        }

        if (_closeButton is not null)
        {
            _closeButton.Click -= OnCloseClick;
        }

        _backdrop = e.NameScope.Find<Border>("PART_Backdrop");
        _closeButton = e.NameScope.Find<Button>("PART_CloseButton");

        if (_backdrop is not null)
        {
            _backdrop.PointerPressed += OnBackdropPressed;
        }

        if (_closeButton is not null)
        {
            _closeButton.Click += OnCloseClick;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Bubble);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _topLevel = null;

        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsOpen || e.Key != Key.Escape)
        {
            return;
        }

        IsOpen = false;
        e.Handled = true;
    }

    // Only a press on the backdrop ITSELF dismisses. Anything inside the card
    // reports itself as the source, so a click on the panel's own content — a
    // scrollbar, a ComboBox, a drag across a text selection — must not close it.
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, _backdrop))
        {
            IsOpen = false;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => IsOpen = false;
}
