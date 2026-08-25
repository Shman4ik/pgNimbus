using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels.Security;

/// <summary>
/// One tab of the Roles &amp; Permissions window. The root view model owns the
/// shared snapshot (roles, the membership graph, the server version) and calls
/// each section to re-read whatever it shows; a section never opens the window's
/// connection on its own schedule.
/// </summary>
public interface ISecuritySection
{
    /// <summary>Re-read this section from the catalog. Never throws — failures land in <see cref="SecurityViewModel.Status"/>.</summary>
    Task RefreshAsync(CancellationToken ct);
}

/// <summary>
/// Drives the Roles &amp; Permissions window — the 0.11 headline feature, and the
/// answer to a question the incumbent clients do not answer: not "what does the
/// catalog store for this object" but "what can this role actually do, and which
/// grant explains that".
///
/// Shaped like <see cref="DatabaseOverviewViewModel"/>: a snapshot taken on open
/// and re-taken on Refresh, no timer, everything read-only against pg_catalog.
/// What it does *not* do is apply changes behind the user's back — every
/// privilege change leaves through <see cref="OpenSqlInNewTab"/> as a script in
/// the editor, following the <c>DdlTemplates</c> precedent. The single exception
/// is a statement carrying a password, which runs through
/// <see cref="SecurityEditor"/> and is never shown or persisted.
/// </summary>
public sealed partial class SecurityViewModel : ObservableObject
{
    private readonly RoleService _roleService;

    [ObservableProperty]
    private string _status = "";

    /// <summary>The connected database, for the window's context line.</summary>
    [ObservableProperty]
    private string _databaseName = "";

    /// <summary>
    /// <c>current_user</c>. Shown because half of "why can't I see this?" is
    /// that the *reader* lacks the privilege, not the role being inspected.
    /// </summary>
    [ObservableProperty]
    private string _currentRole = "";

    [ObservableProperty]
    private string _serverVersionText = "";

    /// <summary>
    /// The server's version, or null before the first refresh. Gates the
    /// privileges and catalog columns that don't exist everywhere — see
    /// <see cref="PgFeatures"/>.
    /// </summary>
    [ObservableProperty]
    private Version? _serverVersion;

    /// <summary>
    /// The shared role snapshot every section reads, rebuilt on each refresh.
    /// Null before the first one completes.
    /// </summary>
    [ObservableProperty]
    private RoleGraph? _graph;

    public SecurityViewModel(
        RoleService roleService,
        PrivilegeService privilegeService,
        SecurityEditor editor,
        string databaseName)
    {
        _roleService = roleService;
        DatabaseName = databaseName;

        Roles = new RolesTabViewModel(roleService, editor, this);
        Permissions = new PermissionsTabViewModel(privilegeService, this);
        DefaultPrivileges = new DefaultPrivilegesTabViewModel(privilegeService, this);
        Rls = new RlsTabViewModel(privilegeService, this);
    }

    public RolesTabViewModel Roles { get; }

    public PermissionsTabViewModel Permissions { get; }

    public DefaultPrivilegesTabViewModel DefaultPrivileges { get; }

    public RlsTabViewModel Rls { get; }

    /// <summary>
    /// Hands a generated script to the main window's editor, in a new tab. Set by
    /// the view when it opens the window; null in the screenshot harness, where
    /// there is no main window to open a tab in — so every caller must null-check
    /// rather than assume.
    /// </summary>
    public Action<string, string>? OpenSqlInNewTab { get; set; }

    /// <summary>
    /// A role the window should land on once it has a snapshot — set by the
    /// schema tree's "Roles and permissions…" before the window opens. Cleared
    /// by whichever path consumes it first.
    /// </summary>
    public string? PendingRoleSelection { get; set; }

    /// <summary>
    /// Applies <see cref="PendingRoleSelection"/> against the roles already
    /// listed. Called for the case where the window was already open, so no
    /// refresh is coming to pick the name up.
    /// </summary>
    public void ApplyPendingRoleSelection()
    {
        if (PendingRoleSelection is { } role && Roles.SelectRole(role))
        {
            PendingRoleSelection = null;
        }
    }

    /// <summary>
    /// Re-reads the shared role snapshot, then fans out to the four sections.
    /// Sections run in parallel because their reads are independent — one round
    /// trip's worth of latency instead of four, which is what makes this bearable
    /// over an SSH tunnel.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            // Only on the first read: a refresh that already has a snapshot behind
            // it should not blank the status line, and the screenshot harness
            // seeds a snapshot before the window's Opened refresh fires.
            if (Graph is null)
            {
                Status = "Reading roles…";
            }

            var rolesTask = _roleService.GetRolesAsync(includePredefined: true, ct);
            var membershipsTask = _roleService.GetMembershipsAsync(ct);
            var currentRoleTask = _roleService.GetCurrentRoleAsync(ct);
            var versionTask = _roleService.GetServerVersionAsync(ct);
            await Task.WhenAll(rolesTask, membershipsTask, currentRoleTask, versionTask);

            Graph = RoleGraph.Build(await rolesTask, await membershipsTask);
            CurrentRole = await currentRoleTask;
            ServerVersion = await versionTask;
            ServerVersionText = $"PostgreSQL {ServerVersion.Major}.{ServerVersion.Minor}";

            await Task.WhenAll(
                Roles.RefreshAsync(ct),
                Permissions.RefreshAsync(ct),
                DefaultPrivileges.RefreshAsync(ct),
                Rls.RefreshAsync(ct));

            var visible = Graph.Roles.Count(r => !r.IsPredefined);
            Status = $"{visible} role{(visible == 1 ? "" : "s")} · {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            // A superseded snapshot — not an error worth surfacing.
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    /// <summary>
    /// The one place a section reports a failure, so a single read going wrong
    /// (an extension type the server will not expand, a permission the reader
    /// lacks) costs a status line rather than the whole window.
    /// </summary>
    public void ReportError(string what, Exception ex) => Status = $"{what}: {ex.Message}";
}
