using Avalonia.Data.Converters;
using PgNimbus.Core.Connections;

namespace PgNimbus.App.Converters;

/// <summary>The SSL mode picker's text for an <see cref="SslMode"/>, from <see cref="SslModes"/>.</summary>
public static class SslModeText
{
    /// <summary>"Verify full", not "VerifyFull".</summary>
    public static readonly IValueConverter Label =
        new FuncValueConverter<SslMode, string>(mode => SslModes.Describe(mode).Label);

    /// <summary>The one line saying what the mode checks, and what it does not.</summary>
    public static readonly IValueConverter Description =
        new FuncValueConverter<SslMode, string>(mode => SslModes.Describe(mode).Description);

    /// <summary>True for the mode the picker marks as recommended.</summary>
    public static readonly IValueConverter IsRecommended =
        new FuncValueConverter<SslMode, bool>(mode => SslModes.Describe(mode).Recommended);
}
