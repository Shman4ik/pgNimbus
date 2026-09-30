using Avalonia.Controls;

namespace PgNimbus.App.Views.Security;

/// <summary>
/// The Roles &amp; Permissions window's default-privileges tab. No code-behind
/// beyond loading the XAML: the grid, the empty state and the one action are all
/// bindings onto <see cref="ViewModels.Security.DefaultPrivilegesTabViewModel"/>,
/// and there is no interaction here that needs to touch a <c>Control</c>.
/// </summary>
public partial class DefaultPrivilegesTabView : UserControl
{
    public DefaultPrivilegesTabView()
    {
        InitializeComponent();
    }
}
