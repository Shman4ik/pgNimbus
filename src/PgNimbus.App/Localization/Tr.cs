using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.Localization;

/// <summary>
/// A translated string in XAML: <c>Text="{l:Tr About_Tagline}"</c>, with
/// <c>xmlns:l="using:PgNimbus.App.Localization"</c>. The key is a
/// <see cref="StringKey"/>, so the XAML compiler refuses one that
/// <c>Strings.resx</c> doesn't define. The value is a binding to
/// <see cref="Localizer.Observe(StringKey)"/>, so it follows a change of language
/// in Settings at once; no reflection is involved (NativeAOT, compiled bindings).
/// </summary>
public sealed class Tr(StringKey key) : MarkupExtension
{
    public StringKey Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Localizer.Observe(Key).ToBinding();
}
