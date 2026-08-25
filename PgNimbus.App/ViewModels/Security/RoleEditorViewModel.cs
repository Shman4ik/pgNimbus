using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels.Security;

/// <summary>
/// Creates a role, or alters one. Both modes are the same form over the same
/// <see cref="RoleDefinition"/>; what differs is what the generated script says,
/// and that difference is the whole point of showing the script at all — create
/// emits the full statement, alter emits only what actually changed.
///
/// <para><b>The preview is masked and the execution is not.</b> Postgres has no
/// parameter form for <c>PASSWORD</c>, so a new password has to be interpolated
/// into statement text. <see cref="LivePreview"/> is built with
/// <c>maskPassword: true</c> and <see cref="ApplyAsync"/> with false — the real
/// literal exists only inside that one call and the connection it goes down. It
/// is never bound to a <c>TextBlock</c>, never handed to the editor tab (which
/// would file it in the on-disk query history), and never logged. This is a
/// security property, not a formatting choice: change it and the password ends
/// up in a screenshot and in <c>queries.json</c>.</para>
/// </summary>
public sealed partial class RoleEditorViewModel : ObservableObject
{
    /// <summary>
    /// The predefined roles that answer "make me a read-only user" on their own.
    /// PG14 added the data pair; <c>pg_monitor</c> is older but belongs in the
    /// same one-click group because it solves the same shape of problem.
    /// </summary>
    private static readonly (string Name, string Hint)[] PredefinedGrantable =
    [
        ("pg_read_all_data", "SELECT on every table, view and sequence, in every schema"),
        ("pg_write_all_data", "INSERT, UPDATE and DELETE on every table"),
        ("pg_monitor", "read the statistics and monitoring views without superuser"),
    ];

    private readonly SecurityEditor _editor;

    /// <summary>Null in create mode; the catalog's current state in alter mode.</summary>
    private readonly RoleAttributes? _current;

    /// <summary>
    /// The groups the role belongs to today. Empty in create mode; in alter mode
    /// it is what <see cref="RoleScriptBuilder.Alter"/> diffs against, and passing
    /// it is what makes a membership removable at all.
    /// </summary>
    private readonly IReadOnlyList<string> _currentMemberOf;

    private bool _loaded;

    /// <summary>
    /// Re-entrancy guard. <see cref="Recompute"/> raises change notifications of
    /// its own, and the blanket <see cref="OnPropertyChanged"/> hook below would
    /// otherwise call it back into itself forever.
    /// </summary>
    private bool _recomputing;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private bool _canLogin = true;

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _passwordConfirm = "";

    [ObservableProperty]
    private bool _isSuperuser;

    [ObservableProperty]
    private bool _inherit = true;

    [ObservableProperty]
    private bool _canCreateDb;

    [ObservableProperty]
    private bool _canCreateRole;

    [ObservableProperty]
    private bool _canReplicate;

    [ObservableProperty]
    private bool _bypassRls;

    /// <summary>Blank means no limit, which Postgres stores as -1.</summary>
    [ObservableProperty]
    private int? _connectionLimit;

    [ObservableProperty]
    private DateTimeOffset? _validUntil;

    [ObservableProperty]
    private string _comment = "";

    /// <summary>The masked script. Never the executed one — see the type remarks.</summary>
    [ObservableProperty]
    private string _livePreview = "";

    /// <summary>Why Apply is refused, or empty when it is not.</summary>
    [ObservableProperty]
    private string _validationMessage = "";

    /// <summary>Legal but probably not what you meant. Shown, never blocking.</summary>
    [ObservableProperty]
    private string _warningMessage = "";

    /// <summary>The server's own refusal, kept in the dialog because its wording is the useful part.</summary>
    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _isBusy;

    private RoleEditorViewModel(
        SecurityEditor editor,
        SecurityViewModel host,
        RoleAttributes? current,
        IReadOnlyList<string> currentMemberOf)
    {
        _editor = editor;
        _current = current;
        _currentMemberOf = currentMemberOf;

        var existing = new HashSet<string>(currentMemberOf, StringComparer.Ordinal);
        var graph = host.Graph;

        // Only offer a predefined role the server actually has. On PG13 the data
        // pair does not exist, and a checkbox whose GRANT is guaranteed to fail
        // is worse than no checkbox.
        if (PgFeatures.SupportsPredefinedDataRoles(host.ServerVersion))
        {
            foreach (var (name, hint) in PredefinedGrantable)
            {
                if (graph is not null && graph.Find(name) is null)
                {
                    continue;
                }

                PredefinedMemberships.Add(new RoleMembershipOption(name, hint, existing.Contains(name), Recompute));
            }
        }

        if (graph is not null)
        {
            foreach (var role in graph.Roles)
            {
                if (role.IsPredefined || string.Equals(role.Name, current?.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                GroupMemberships.Add(new RoleMembershipOption(
                    role.Name,
                    role.CanLogin ? "login role" : "group role",
                    existing.Contains(role.Name),
                    Recompute));
            }
        }

        if (current is not null)
        {
            Name = current.Name;
            CanLogin = current.CanLogin;
            IsSuperuser = current.IsSuperuser;
            Inherit = current.Inherit;
            CanCreateDb = current.CanCreateDb;
            CanCreateRole = current.CanCreateRole;
            CanReplicate = current.CanReplicate;
            BypassRls = current.BypassRls;
            ConnectionLimit = current.ConnectionLimit < 0 ? null : current.ConnectionLimit;
            ValidUntil = current.ValidUntil;
            Comment = current.Comment ?? "";
        }

        _loaded = true;
        Recompute();
    }

    /// <summary>Closes the dialog. True means a script ran and the caller should re-read the catalog.</summary>
    public Action<bool>? CloseRequested { get; set; }

    public bool IsCreate => _current is null;

    public string Title => IsCreate ? "New role" : $"Alter role “{_current!.Name}”";

    /// <summary>A role's name is its identity for every grant; renaming is <c>ALTER ROLE … RENAME</c>, not this form.</summary>
    public bool CanEditName => IsCreate;

    /// <summary>Hidden entirely on a server too old to have the predefined data roles.</summary>
    public bool HasPredefinedMemberships => PredefinedMemberships.Count > 0;

    public ObservableCollection<RoleMembershipOption> PredefinedMemberships { get; } = [];

    public ObservableCollection<RoleMembershipOption> GroupMemberships { get; } = [];

    public bool HasValidationMessage => ValidationMessage.Length > 0;

    public bool HasWarningMessage => WarningMessage.Length > 0;

    public bool HasErrorMessage => ErrorMessage.Length > 0;

    /// <summary>
    /// <c>NumericUpDown</c> speaks <c>decimal?</c>; blank is "no limit". The
    /// equality guard keeps the control's own re-write of the value it was just
    /// given from registering as a change the diff would then emit.
    /// </summary>
    public decimal? ConnectionLimitValue
    {
        get => ConnectionLimit;
        set
        {
            if (value == ConnectionLimitValue)
            {
                return;
            }

            ConnectionLimit = value is { } limit ? (int)Math.Clamp(limit, 0m, int.MaxValue) : null;
        }
    }

    /// <summary>
    /// <c>CalendarDatePicker</c> speaks <c>DateTime?</c> and clears to null when
    /// its text is emptied, which is how an expiry is removed. Normalized to the
    /// date so the picker's write-back of what it was given is a no-op.
    /// </summary>
    public DateTime? ValidUntilDate
    {
        get => ValidUntil?.LocalDateTime.Date;
        set
        {
            if (value == ValidUntilDate)
            {
                return;
            }

            ValidUntil = value is { } date
                ? new DateTimeOffset(date.Date, TimeZoneInfo.Local.GetUtcOffset(date.Date))
                : null;
        }
    }

    public static RoleEditorViewModel ForCreate(SecurityEditor editor, SecurityViewModel host) =>
        new(editor, host, current: null, currentMemberOf: []);

    public static RoleEditorViewModel ForAlter(
        SecurityEditor editor,
        SecurityViewModel host,
        RoleAttributes current,
        IReadOnlyList<string> currentMemberOf) =>
        new(editor, host, current, currentMemberOf);

    /// <summary>
    /// Any field change re-derives the preview and the validation state. Done
    /// here rather than with a NotifyPropertyChangedFor attribute per field
    /// because there are fifteen of them and one forgotten attribute is a
    /// preview that quietly stops matching the form.
    /// </summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is nameof(LivePreview) or nameof(ValidationMessage) or nameof(WarningMessage)
            or nameof(ErrorMessage) or nameof(IsBusy))
        {
            return;
        }

        Recompute();
    }

    private void Recompute()
    {
        if (!_loaded || _recomputing)
        {
            return;
        }

        _recomputing = true;
        try
        {
            RecomputeCore();
        }
        finally
        {
            _recomputing = false;
        }
    }

    private void RecomputeCore()
    {
        LivePreview = BuildScript(maskPassword: true);

        ValidationMessage =
            Name.Trim().Length == 0 ? "A role needs a name."
            : Password != PasswordConfirm ? "The two passwords do not match."
            : LivePreview.Length == 0 ? "Nothing has changed yet."
            : "";

        // Legal, and sometimes right: pg_hba.conf may authenticate this role by
        // peer, trust or an external method. So it is said out loud, not refused.
        WarningMessage = IsCreate && CanLogin && Password.Length == 0
            ? "This role can log in but has no password. That works only if pg_hba.conf authenticates it another way."
            : "";

        OnPropertyChanged(nameof(HasValidationMessage));
        OnPropertyChanged(nameof(HasWarningMessage));
        OnPropertyChanged(nameof(ConnectionLimitValue));
        OnPropertyChanged(nameof(ValidUntilDate));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The script, in the one shape both callers need.
    /// <paramref name="maskPassword"/> false is only ever passed by
    /// <see cref="ApplyAsync"/>.
    /// </summary>
    private string BuildScript(bool maskPassword)
    {
        var trimmed = Name.Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }

        var definition = new RoleDefinition(
            trimmed,
            CanLogin,
            IsSuperuser,
            Inherit,
            CanCreateDb,
            CanCreateRole,
            CanReplicate,
            BypassRls,
            ConnectionLimit,
            ValidUntil,
            SelectedMemberships(),
            Comment.Trim().Length == 0 ? null : Comment.Trim());

        if (_current is null)
        {
            return RoleScriptBuilder.Create(definition, Password.Length == 0 ? null : Password, maskPassword);
        }

        // Alter returns "" when nothing changed, which is what disables Apply —
        // running an empty script would report success for a no-op.
        var statements = new List<string>();
        var diff = RoleScriptBuilder.Alter(_current, definition, _currentMemberOf);
        if (diff.Length > 0)
        {
            statements.Add(diff);
        }

        // A password is not part of RoleAttributes and cannot be diffed, so it is
        // a change exactly when the field was typed into.
        if (Password.Length > 0)
        {
            statements.Add(RoleScriptBuilder.SetPassword(_current.Name, Password, maskPassword));
        }

        return string.Join("\n", statements);
    }

    private IReadOnlyList<string> SelectedMemberships() =>
        PredefinedMemberships.Concat(GroupMemberships)
            .Where(o => o.IsMember)
            .Select(o => o.Name)
            .ToList();

    private bool CanApply() => !IsBusy && ValidationMessage.Length == 0;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        ErrorMessage = "";
        IsBusy = true;
        ApplyCommand.NotifyCanExecuteChanged();

        try
        {
            // The only place maskPassword is false. The result is not stored,
            // shown or logged - it goes straight down the connection.
            await _editor.ExecuteScriptAsync(BuildScript(maskPassword: false), CancellationToken.None);
            CloseRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            // Staying open with the server's own words: on a managed server this
            // is "permission denied to create role", which is the answer.
            ErrorMessage = ex.Message;
            OnPropertyChanged(nameof(HasErrorMessage));
        }
        finally
        {
            IsBusy = false;
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>One membership checkbox: a role this one may belong to.</summary>
public sealed partial class RoleMembershipOption : ObservableObject
{
    private readonly Action _changed;

    [ObservableProperty]
    private bool _isMember;

    public RoleMembershipOption(string name, string hint, bool isMember, Action changed)
    {
        Name = name;
        Hint = hint;
        _isMember = isMember;
        _changed = changed;
    }

    public string Name { get; }

    public string Hint { get; }

    partial void OnIsMemberChanged(bool value) => _changed();
}

/// <summary>
/// The answer to Postgres's 2BP01. <c>DROP ROLE</c> fails the moment the role
/// owns anything or holds a grant, and the server's error names neither the
/// objects nor the fix — so this reads both lists up front and generates the
/// <c>REASSIGN OWNED</c> / <c>DROP OWNED</c> / <c>DROP ROLE</c> recipe that
/// actually works.
/// </summary>
public sealed partial class DropRoleViewModel : ObservableObject
{
    private readonly RoleService _roleService;
    private readonly SecurityEditor _editor;
    private readonly Action<string, string>? _openSqlInNewTab;

    [ObservableProperty]
    private ReassignOption? _reassignTo;

    [ObservableProperty]
    private string _script = "";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _ownedHeader = "Objects it owns";

    [ObservableProperty]
    private string _grantsHeader = "Privileges it holds";

    public DropRoleViewModel(
        RoleService roleService,
        SecurityEditor editor,
        string roleName,
        string currentRole,
        IReadOnlyList<string> otherRoles,
        Action<string, string>? openSqlInNewTab)
    {
        _roleService = roleService;
        _editor = editor;
        _openSqlInNewTab = openSqlInNewTab;
        RoleName = roleName;

        // "Nobody" first, so the destructive choice is a deliberate pick rather
        // than the one you land on by leaving the combo alone.
        ReassignTargets.Add(ReassignOption.Nobody);
        foreach (var role in otherRoles)
        {
            ReassignTargets.Add(new ReassignOption(role));
        }

        _reassignTo = ReassignTargets.FirstOrDefault(o => string.Equals(o.Role, currentRole, StringComparison.Ordinal))
                      ?? ReassignTargets.Skip(1).FirstOrDefault()
                      ?? ReassignOption.Nobody;

        Rebuild();
    }

    public Action<bool>? CloseRequested { get; set; }

    public string RoleName { get; }

    public ObservableCollection<ReassignOption> ReassignTargets { get; } = [];

    public ObservableCollection<RoleDependency> Owned { get; } = [];

    public ObservableCollection<RoleDependency> GrantsHeld { get; } = [];

    public bool HasOwned => Owned.Count > 0;

    public bool HasGrantsHeld => GrantsHeld.Count > 0;

    /// <summary>Nothing was named to take the objects over, so DROP OWNED deletes them.</summary>
    public bool WillDeleteOwnedObjects => ReassignTo is null || ReassignTo.Role is null;

    public bool HasErrorMessage => ErrorMessage.Length > 0;

    public bool CanRun => !IsBusy && !IsLoading;

    /// <summary>
    /// Reads what would make the drop fail. Both queries cover the
    /// <em>current database only</em>, which is why the dialog says so: a role
    /// with objects in three databases needs the recipe run in each.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var ownedTask = _roleService.GetOwnedObjectsAsync(RoleName, ct);
            var grantsTask = _roleService.GetGrantsHeldAsync(RoleName, ct);
            await Task.WhenAll(ownedTask, grantsTask);

            foreach (var dependency in await ownedTask)
            {
                Owned.Add(dependency);
            }

            foreach (var dependency in await grantsTask)
            {
                GrantsHeld.Add(dependency);
            }

            OwnedHeader = $"Objects it owns ({Owned.Count})";

            // A full page means the query hit its cap, not that this is the whole
            // list - saying "200" there would be a number the user could act on
            // and be wrong about.
            GrantsHeader = GrantsHeld.Count >= RoleService.GrantsHeldLimit
                ? $"Privileges it holds ({RoleService.GrantsHeldLimit}+, list truncated)"
                : $"Privileges it holds ({GrantsHeld.Count})";
        }
        catch (OperationCanceledException)
        {
            // The dialog closed while the read was in flight.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            OnPropertyChanged(nameof(HasErrorMessage));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasOwned));
            OnPropertyChanged(nameof(HasGrantsHeld));
            OnPropertyChanged(nameof(CanRun));
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnReassignToChanged(ReassignOption? value) => Rebuild();

    private void Rebuild()
    {
        Script = RoleScriptBuilder.Drop(RoleName, ReassignTo?.Role);
        OnPropertyChanged(nameof(WillDeleteOwnedObjects));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        ErrorMessage = "";
        IsBusy = true;
        RunCommand.NotifyCanExecuteChanged();

        try
        {
            await _editor.ExecuteScriptAsync(Script, CancellationToken.None);
            CloseRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            OnPropertyChanged(nameof(HasErrorMessage));
        }
        finally
        {
            IsBusy = false;
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Hands the recipe to the editor instead of running it here. REASSIGN and
    /// DROP OWNED act on the current database only, so a role with objects in
    /// several databases needs the same script run against each connection —
    /// which is the editor's job, not a modal dialog's.
    /// </summary>
    [RelayCommand]
    private void OpenInEditor()
    {
        // Null in the screenshot harness, which has no main window.
        _openSqlInNewTab?.Invoke($"drop role · {RoleName}", Script);
        CloseRequested?.Invoke(false);
    }
}

/// <summary>A choice in the "reassign owned objects to" combo; <see cref="Role"/> null is the destructive one.</summary>
public sealed record ReassignOption(string? Role)
{
    public static ReassignOption Nobody { get; } = new((string?)null);

    public string Label => Role ?? "Nobody — delete the owned objects";
}
