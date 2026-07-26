using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using PgNimbus.Core.Commands;

namespace PgNimbus.App;

/// <summary>
/// Renders a control's tooltip as "what it does (its shortcut)", with the
/// shortcut read from <see cref="CommandCatalog"/> instead of typed into the
/// markup. Same contract as the key bindings and the F1 sheet: a gesture is
/// stated once in Core and every surface is a projection of it — a tooltip
/// that hardcodes "(Ctrl+Shift+R)" goes stale the moment the chord moves or
/// the user picks the Cmd scheme.
///
/// <code>
///   &lt;Button cmd:CommandTip.Text="Refresh the schema" cmd:CommandTip.Command="RefreshSchema" /&gt;
/// </code>
///
/// <c>Text</c> alone is a plain tooltip; <c>Command</c> alone falls back to
/// the catalog's own title. The label re-renders live when the Ctrl/Cmd scheme
/// preference changes, so open windows never show the other platform's chord.
/// </summary>
public static class CommandTip
{
    /// <summary>The human sentence — what the control does, no gesture in it.</summary>
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Text", typeof(CommandTip));

    /// <summary>
    /// The catalog entry whose chord is appended in parentheses. Nullable on
    /// purpose: the enum's zero value is a real command (<c>Run</c>), so a
    /// non-nullable property would treat setting it as "no change" and never
    /// raise the handler that composes the tip.
    /// </summary>
    public static readonly AttachedProperty<CommandId?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, CommandId?>("Command", typeof(CommandTip));

    // Guards the one-time Hotkeys.Changed wiring: both properties are usually
    // set on the same control, and each set runs through Apply.
    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Hooked", typeof(CommandTip));

    static CommandTip()
    {
        TextProperty.Changed.AddClassHandler<Control>((control, _) => Apply(control));
        CommandProperty.Changed.AddClassHandler<Control>((control, _) => Apply(control));
    }

    public static void SetText(Control control, string? value) => control.SetValue(TextProperty, value);

    public static string? GetText(Control control) => control.GetValue(TextProperty);

    public static void SetCommand(Control control, CommandId? value) => control.SetValue(CommandProperty, value);

    public static CommandId? GetCommand(Control control) => control.GetValue(CommandProperty);

    private static void Apply(Control control)
    {
        ToolTip.SetTip(control, Compose(control));
        Hook(control);
    }

    private static string? Compose(Control control)
    {
        var text = control.GetValue(TextProperty);

        if (control.GetValue(CommandProperty) is not { } id)
        {
            return text;
        }

        var descriptor = CommandCatalog.Get(id);
        text ??= descriptor.Title;

        return descriptor.Chord is { } chord
            ? $"{text} ({chord.Label(Hotkeys.CommandLabel)})"
            : text;
    }

    // Subscribed only while the control is on screen, so a closed window's
    // buttons don't stay rooted by the static event.
    private static void Hook(Control control)
    {
        if (control.GetValue(HookedProperty))
        {
            return;
        }

        control.SetValue(HookedProperty, true);

        void OnSchemeChanged() => ToolTip.SetTip(control, Compose(control));

        control.AttachedToVisualTree += (_, _) => Hotkeys.Changed += OnSchemeChanged;
        control.DetachedFromVisualTree += (_, _) => Hotkeys.Changed -= OnSchemeChanged;

        if (control.IsAttachedToVisualTree())
        {
            Hotkeys.Changed += OnSchemeChanged;
        }
    }
}
