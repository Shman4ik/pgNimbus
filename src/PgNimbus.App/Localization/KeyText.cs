using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.Localization;

/// <summary>
/// A <see cref="TextBlock"/> showing a <see cref="StringKey"/> that comes from a
/// binding (a converter, an option record), in the language in use, while keeping
/// the block's own classes and layout: <c>&lt;TextBlock Classes="secondary"
/// l:KeyText.Key="{Binding LabelKey}" /&gt;</c>. A <c>ContentControl</c> whose content
/// is the key (<see cref="StringKeyTemplate"/>) does the same where the styling
/// doesn't matter.
/// </summary>
public sealed class KeyText
{
    public static readonly AttachedProperty<StringKey?> KeyProperty =
        AvaloniaProperty.RegisterAttached<KeyText, TextBlock, StringKey?>("Key");

    static KeyText()
    {
        KeyProperty.Changed.AddClassHandler<TextBlock>((block, e) =>
        {
            if (e.NewValue is StringKey key)
            {
                block.Bind(TextBlock.TextProperty, Localizer.Observe(key).ToBinding());
            }
            else
            {
                block.ClearValue(TextBlock.TextProperty);
            }
        });
    }

    public static StringKey? GetKey(TextBlock block) => block.GetValue(KeyProperty);

    public static void SetKey(TextBlock block, StringKey? value) => block.SetValue(KeyProperty, value);
}
