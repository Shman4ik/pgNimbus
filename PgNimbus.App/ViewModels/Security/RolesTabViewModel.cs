using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels.Security;

/// <summary>The roles list, one role's attributes, and its membership tree in both directions.</summary>
public sealed partial class RolesTabViewModel : ObservableObject, ISecuritySection
{
    private readonly RoleService _roleService;
    private readonly SecurityEditor _editor;
    private readonly SecurityViewModel _host;

    /// <summary>Every role the last refresh saw; <see cref="FilteredRoles"/> is the view onto it.</summary>
    private readonly List<RoleRowViewModel> _all = [];

    [ObservableProperty]
    private string _filter = "";

    /// <summary>
    /// Off by default: a stock server carries a dozen <c>pg_*</c> roles nobody
    /// created and nobody edits, and on a managed one they outnumber the real
    /// roles. They stay one click away rather than being hidden outright,
    /// because "why is pg_monitor not listed" is a fair question.
    /// </summary>
    [ObservableProperty]
    private bool _showPredefinedRoles;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditRoleCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropRoleCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCreateScriptCommand))]
    private RoleRowViewModel? _selectedRole;

    [ObservableProperty]
    private bool _hasSettings;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _hasFilteredRoles;

    [ObservableProperty]
    private bool _hasMemberOf;

    [ObservableProperty]
    private bool _hasMembers;

    public RolesTabViewModel(RoleService roleService, SecurityEditor editor, SecurityViewModel host)
    {
        _roleService = roleService;
        _editor = editor;
        _host = host;
    }

    /// <summary>One row per role on the server, predefined ones included.</summary>
    public ObservableCollection<RoleRowViewModel> Roles { get; } = [];

    /// <summary>What the list actually shows: <see cref="Roles"/> after the name filter and the predefined toggle.</summary>
    public ObservableCollection<RoleRowViewModel> FilteredRoles { get; } = [];

    /// <summary>The groups the selected role belongs to, walking upward.</summary>
    public ObservableCollection<RoleTreeNodeViewModel> MemberOf { get; } = [];

    /// <summary>The roles that belong to the selected one, walking downward.</summary>
    public ObservableCollection<RoleTreeNodeViewModel> Members { get; } = [];

    /// <summary>The selected role's <c>ALTER ROLE … SET</c> entries; the section hides itself when empty.</summary>
    public ObservableCollection<string> Settings { get; } = [];

    /// <summary>
    /// Shows the create/alter dialog and reports whether it applied anything.
    /// Set by the view: a view model that news up a <c>Window</c> would drag
    /// Avalonia into the layer that has to stay bindable and testable, so this
    /// follows the <c>AlterTableViewModelFactory</c> precedent — the view model
    /// builds the dialog's view model, the view opens the dialog.
    /// </summary>
    public Func<RoleEditorViewModel, Task<bool>>? ShowRoleDialog { get; set; }

    /// <inheritdoc cref="ShowRoleDialog" />
    public Func<DropRoleViewModel, Task<bool>>? ShowDropDialog { get; set; }

    /// <summary>
    /// Re-reads from the snapshot the host already built. Nothing here opens a
    /// connection: the graph is the shared read, and doing it again per section
    /// would cost four round trips for one answer.
    /// </summary>
    public Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            if (_host.Graph is not { } graph)
            {
                return Task.CompletedTask;
            }

            var previous = SelectedRole?.Name;

            _all.Clear();
            foreach (var role in graph.Roles)
            {
                _all.Add(new RoleRowViewModel(role, graph.MemberOf(role.Name).Count));
            }

            Roles.Clear();
            foreach (var row in _all)
            {
                Roles.Add(row);
            }

            ApplyFilter();

            // A refresh must not silently move the detail pane to another role,
            // so the selection is restored by name rather than by identity.
            SelectedRole = previous is null
                ? null
                : FilteredRoles.FirstOrDefault(r => string.Equals(r.Name, previous, StringComparison.Ordinal));

            // A role the schema tree asked for outranks the restored selection:
            // the user right-clicked that name to get here.
            if (_host.PendingRoleSelection is { } pending && SelectRole(pending))
            {
                _host.PendingRoleSelection = null;
            }
        }
        catch (Exception ex)
        {
            _host.ReportError("Roles", ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Selects a role by name, returning false when this snapshot has no such
    /// role. A <c>pg_*</c> name turns the built-in filter on first — being asked
    /// for a role and silently not showing it would read as the command doing
    /// nothing, which is worse than changing a toggle the user can flip back.
    /// </summary>
    public bool SelectRole(string name)
    {
        if (name.StartsWith("pg_", StringComparison.Ordinal) && !ShowPredefinedRoles)
        {
            ShowPredefinedRoles = true; // ApplyFilter runs on the change
        }

        if (FilteredRoles.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal)) is not { } match)
        {
            return false;
        }

        SelectedRole = match;
        return true;
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnShowPredefinedRolesChanged(bool value) => ApplyFilter();

    partial void OnSelectedRoleChanged(RoleRowViewModel? value)
    {
        MemberOf.Clear();
        Members.Clear();
        Settings.Clear();

        if (value is not null && _host.Graph is { } graph)
        {
            foreach (var node in RoleTreeNodeViewModel.Build(graph.MemberOf(value.Name)))
            {
                MemberOf.Add(node);
            }

            foreach (var node in RoleTreeNodeViewModel.Build(graph.Members(value.Name)))
            {
                Members.Add(node);
            }

            foreach (var setting in value.Attributes.Settings)
            {
                Settings.Add(setting);
            }
        }

        // Explicit "is this empty" flags rather than a `!Collection.Count`
        // binding: negating an int is not something a compiled binding converts
        // reliably, and each empty state gets its own sentence anyway.
        HasSelection = value is not null;
        HasSettings = Settings.Count > 0;
        HasMemberOf = MemberOf.Count > 0;
        HasMembers = Members.Count > 0;
    }

    private void ApplyFilter()
    {
        var needle = Filter.Trim();

        FilteredRoles.Clear();
        foreach (var row in _all)
        {
            if (!ShowPredefinedRoles && row.IsPredefined)
            {
                continue;
            }

            if (needle.Length > 0 && !row.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FilteredRoles.Add(row);
        }

        HasFilteredRoles = FilteredRoles.Count > 0;

        // Filtering the selected row away would leave the detail pane describing
        // a role the list no longer shows.
        if (SelectedRole is { } selected && !FilteredRoles.Contains(selected))
        {
            SelectedRole = null;
        }
    }

    /// <summary>Predefined roles are the server's, not the user's — nothing here may edit or drop one.</summary>
    private bool CanEditSelected() => SelectedRole is { IsPredefined: false };

    /// <summary>
    /// Also refuses the role you are connected as. Dropping it would succeed
    /// only by breaking the connection that ran the statement.
    /// </summary>
    private bool CanDropSelected() =>
        CanEditSelected() && !string.Equals(SelectedRole!.Name, _host.CurrentRole, StringComparison.Ordinal);

    private bool CanScriptSelected() => SelectedRole is not null;

    [RelayCommand]
    private async Task NewRoleAsync()
    {
        if (ShowRoleDialog is not { } show)
        {
            return;
        }

        var editor = RoleEditorViewModel.ForCreate(_editor, _host);
        if (await show(editor))
        {
            await _host.ReloadAfterRoleChangeAsync(CancellationToken.None);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task EditRoleAsync()
    {
        if (SelectedRole is not { } row || ShowRoleDialog is not { } show)
        {
            return;
        }

        var editor = RoleEditorViewModel.ForAlter(_editor, _host, row.Attributes, DirectGroupsOf(row.Name));
        if (await show(editor))
        {
            await _host.ReloadAfterRoleChangeAsync(CancellationToken.None);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDropSelected))]
    private async Task DropRoleAsync()
    {
        if (SelectedRole is not { } row || ShowDropDialog is not { } show)
        {
            return;
        }

        var drop = new DropRoleViewModel(
            _roleService,
            _editor,
            row.Name,
            _host.CurrentRole,
            _all.Where(r => !string.Equals(r.Name, row.Name, StringComparison.Ordinal) && !r.IsPredefined)
                .Select(r => r.Name)
                .ToList(),
            _host.OpenSqlInNewTab);

        if (await show(drop))
        {
            await _host.ReloadAfterRoleChangeAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Hands the selected role's <c>CREATE ROLE</c> statement to the editor.
    /// The password is passed as null — a role's password is not in
    /// <c>pg_roles</c> and this app deliberately never reads <c>pg_authid</c>,
    /// so the generated script is a recipe to re-create the role, not a copy of
    /// its credentials.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScriptSelected))]
    private void CopyCreateScript()
    {
        // Null in the screenshot harness, which has no main window to open a tab in.
        if (SelectedRole is not { } row || _host.OpenSqlInNewTab is not { } openSql)
        {
            return;
        }

        var definition = ToDefinition(row.Attributes, DirectGroupsOf(row.Name));
        openSql($"role · {row.Name}", RoleScriptBuilder.Create(definition, password: null));
    }

    /// <summary>
    /// The groups a role is a member of directly — the roots of the membership
    /// tree, not the whole transitive closure. Re-granting an indirect
    /// membership would flatten the group hierarchy the server already has.
    /// </summary>
    private IReadOnlyList<string> DirectGroupsOf(string role) =>
        _host.Graph is { } graph ? graph.MemberOf(role).Select(n => n.Role).ToList() : [];

    internal static RoleDefinition ToDefinition(RoleAttributes role, IReadOnlyList<string> memberOf) =>
        new(
            role.Name,
            role.CanLogin,
            role.IsSuperuser,
            role.Inherit,
            role.CanCreateDb,
            role.CanCreateRole,
            role.CanReplicate,
            role.BypassRls,
            role.ConnectionLimit < 0 ? null : role.ConnectionLimit,
            role.ValidUntil,
            memberOf,
            role.Comment);
}

/// <summary>One role in the list, and the detail rows the pane beside it shows.</summary>
public sealed class RoleRowViewModel
{
    public RoleRowViewModel(RoleAttributes attributes, int memberOfCount)
    {
        Attributes = attributes;
        MemberOfCount = memberOfCount;

        // Same shape and vocabulary as RoleNode in the schema tree, extended by
        // the attributes that tree does not carry — one role must not read as
        // two different things in two places in the same app.
        var flags = new List<string>();
        if (attributes.IsSuperuser)
        {
            flags.Add("superuser");
        }

        if (attributes.CanLogin)
        {
            flags.Add("login");
        }

        if (attributes.CanCreateDb)
        {
            flags.Add("createdb");
        }

        if (attributes.CanCreateRole)
        {
            flags.Add("createrole");
        }

        if (attributes.CanReplicate)
        {
            flags.Add("replication");
        }

        if (attributes.BypassRls)
        {
            flags.Add("bypassrls");
        }

        if (!attributes.Inherit)
        {
            flags.Add("noinherit");
        }

        AttributesLabel = string.Join(" · ", flags);

        ValidUntilText = attributes.ValidUntil is not { } until
            ? ""
            : attributes.IsExpired
                ? "expired"
                : until.LocalDateTime.ToString("yyyy-MM-dd");

        Details =
        [
            new RoleDetailRow("Login", YesNo(attributes.CanLogin)),
            new RoleDetailRow("Superuser", YesNo(attributes.IsSuperuser)),
            new RoleDetailRow("Inherit", YesNo(attributes.Inherit),
                IsWarning: !attributes.Inherit,
                Note: attributes.Inherit ? null : "group privileges need SET ROLE"),
            new RoleDetailRow("Create DB", YesNo(attributes.CanCreateDb)),
            new RoleDetailRow("Create role", YesNo(attributes.CanCreateRole)),
            new RoleDetailRow("Replication", YesNo(attributes.CanReplicate)),
            new RoleDetailRow("Bypass RLS", YesNo(attributes.BypassRls)),
            new RoleDetailRow("Connection limit",
                attributes.ConnectionLimit < 0 ? "no limit" : attributes.ConnectionLimit.ToString()),
            new RoleDetailRow("Valid until",
                ValidUntilText.Length == 0 ? "no expiry" : ValidUntilText,
                IsWarning: attributes.IsExpired),
            new RoleDetailRow("Comment", attributes.Comment is { Length: > 0 } c ? c : "—"),
        ];
    }

    public RoleAttributes Attributes { get; }

    public string Name => Attributes.Name;

    public string AttributesLabel { get; }

    public int MemberOfCount { get; }

    /// <summary>Empty when the role never expires, "expired" once it has — the state worth spotting in a list.</summary>
    public string ValidUntilText { get; }

    public bool IsExpired => Attributes.IsExpired;

    public bool IsPredefined => Attributes.IsPredefined;

    public IReadOnlyList<RoleDetailRow> Details { get; }

    private static string YesNo(bool value) => value ? "yes" : "no";
}

/// <summary>One label/value line of the detail pane.</summary>
public sealed record RoleDetailRow(string Label, string Value, bool IsWarning = false, string? Note = null)
{
    public bool HasNote => Note is { Length: > 0 };
}

/// <summary>
/// A node of the membership tree. The one thing this adds over
/// <see cref="RoleTreeNode"/> is making <c>NOINHERIT</c> visible: such a
/// membership looks granted in every other client and behaves as if it is not,
/// because the privileges only arrive after an explicit <c>SET ROLE</c>.
/// </summary>
public sealed class RoleTreeNodeViewModel
{
    private RoleTreeNodeViewModel(RoleTreeNode node)
    {
        Role = node.Role;
        Inherits = node.Inherits;
        Children = Build(node.Children);
    }

    public string Role { get; }

    public bool Inherits { get; }

    public IReadOnlyList<RoleTreeNodeViewModel> Children { get; }

    /// <summary>True for a membership whose privileges are dormant until <c>SET ROLE</c>.</summary>
    public bool IsDormant => !Inherits;

    /// <summary>Dimmed rather than hidden: the relationship is real, the privileges are not automatic.</summary>
    public double RowOpacity => Inherits ? 1.0 : 0.55;

    public static IReadOnlyList<RoleTreeNodeViewModel> Build(IReadOnlyList<RoleTreeNode> nodes) =>
        nodes.Select(n => new RoleTreeNodeViewModel(n)).ToList();
}
