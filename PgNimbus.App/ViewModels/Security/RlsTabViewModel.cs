using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels.Security;

/// <summary>Row-level security: which tables have it, which policies apply, and who bypasses it.</summary>
public sealed partial class RlsTabViewModel : ObservableObject, ISecuritySection
{
    private readonly PrivilegeService _privileges;
    private readonly SecurityViewModel _host;

    public RlsTabViewModel(PrivilegeService privileges, SecurityViewModel host)
    {
        _privileges = privileges;
        _host = host;
    }

    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}
