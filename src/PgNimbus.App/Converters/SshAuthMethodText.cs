using Avalonia.Data.Converters;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Connections;

namespace PgNimbus.App.Converters;

/// <summary>The connection form's label for an <see cref="SshAuthMethod"/> ("SSH agent", not "Agent").</summary>
public static class SshAuthMethodText
{
    public static readonly IValueConverter Instance =
        new FuncValueConverter<SshAuthMethod, string>(ConnectionDialogViewModel.DescribeSshAuthMethod);
}
