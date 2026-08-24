using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels.Security;

/// <summary>Pick an object, see which role can do what to it -- and which grant explains that.</summary>
public sealed partial class PermissionsTabViewModel : ObservableObject, ISecuritySection
{
    private readonly PrivilegeService _privileges;
    private readonly SecurityViewModel _host;

    public PermissionsTabViewModel(PrivilegeService privileges, SecurityViewModel host)
    {
        _privileges = privileges;
        _host = host;
    }

    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}
