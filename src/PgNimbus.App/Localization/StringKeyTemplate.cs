using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.Localization;

/// <summary>
/// Draws a <see cref="StringKey"/> as its text, bound to the language: registered in
/// the application's data templates, so any content that is a key rather than a
/// string follows a change of language by itself. That is what a string cannot do
/// wherever a control copies it: a <c>ComboBox</c> copies the selected item into
/// its closed box when the selection is made, and a converter runs once.
/// <para>
/// So a choice in a combo box is <c>&lt;ComboBoxItem Content="{x:Static lk:StringKey.Settings_ThemeSystem}" /&gt;</c>
/// (<c>xmlns:lk="using:PgNimbus.Core.Localization"</c>), an option record shows a
/// <see cref="StringKey"/> property through <c>ContentControl Content="{Binding LabelKey}"</c>,
/// and a converter that names a kind returns the key, not its text.
/// </para>
/// </summary>
public static class StringKeyTemplate
{
    public static IDataTemplate Instance { get; } = new FuncDataTemplate<StringKey>((key, _) =>
    {
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, Localizer.Observe(key).ToBinding());
        return text;
    });
}
