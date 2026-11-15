using Avalonia.Data.Converters;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.Converters;

/// <summary>The SSL mode picker's text for an <see cref="SslMode"/>, from <see cref="SslModes"/>.</summary>
public static class SslModeText
{
    /// <summary>"Verify full", not "VerifyFull".</summary>
    public static readonly IValueConverter Label =
        new FuncValueConverter<SslMode, StringKey>(mode => SslModes.Describe(mode).LabelKey);

    /// <summary>The one line saying what the mode checks, and what it does not.</summary>
    public static readonly IValueConverter Description =
        new FuncValueConverter<SslMode, StringKey>(mode => SslModes.Describe(mode).DescriptionKey);

    /// <summary>True for the mode the picker marks as recommended.</summary>
    public static readonly IValueConverter IsRecommended =
        new FuncValueConverter<SslMode, bool>(mode => SslModes.Describe(mode).Recommended);
}
