using Avalonia.Data.Converters;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.Converters;

/// <summary>The environment picker's and the connection list's text for a <see cref="ConnectionEnvironment"/>.</summary>
public static class EnvironmentText
{
    /// <summary>"Production", for the picker.</summary>
    public static readonly IValueConverter Label =
        new FuncValueConverter<ConnectionEnvironment, StringKey>(environment => environment.LabelKey());

    /// <summary>"production", the list's tag; empty for none.</summary>
    public static readonly IValueConverter Tag =
        new FuncValueConverter<ConnectionEnvironment, StringKey?>(environment => environment.TagKey());

    public static readonly IValueConverter HasTag =
        new FuncValueConverter<ConnectionEnvironment, bool>(environment => environment.Tag() is not null);

    public static readonly IValueConverter IsProduction =
        new FuncValueConverter<ConnectionEnvironment, bool>(environment => environment == ConnectionEnvironment.Production);

    public static readonly IValueConverter IsStaging =
        new FuncValueConverter<ConnectionEnvironment, bool>(environment => environment == ConnectionEnvironment.Staging);
}
