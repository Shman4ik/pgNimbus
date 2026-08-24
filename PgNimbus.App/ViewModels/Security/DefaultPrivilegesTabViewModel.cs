using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels.Security;

/// <summary>pg_default_acl: what a future object created by a given role will already be granted.</summary>
public sealed partial class DefaultPrivilegesTabViewModel : ObservableObject, ISecuritySection
{
    private readonly PrivilegeService _privileges;
    private readonly SecurityViewModel _host;

    public DefaultPrivilegesTabViewModel(PrivilegeService privileges, SecurityViewModel host)
    {
        _privileges = privileges;
        _host = host;
    }

    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}
