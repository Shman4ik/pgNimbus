using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels.Security;

/// <summary>The roles list, one role's attributes, and its membership tree in both directions.</summary>
public sealed partial class RolesTabViewModel : ObservableObject, ISecuritySection
{
    private readonly RoleService _roleService;
    private readonly SecurityEditor _editor;
    private readonly SecurityViewModel _host;

    public RolesTabViewModel(RoleService roleService, SecurityEditor editor, SecurityViewModel host)
    {
        _roleService = roleService;
        _editor = editor;
        _host = host;
    }

    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}
