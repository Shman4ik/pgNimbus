using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Localization;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels.Security;

/// <summary>One column of the matrix: a privilege, and the header text for it.</summary>
public sealed record PrivilegeColumn(PrivilegeKind Privilege, string Header);

/// <summary>
/// One choice of the object-kind picker: the kind, and the key of the word for it.
/// A key, not text, because a combo box copies the selected item and a string
/// would not follow a change of language.
/// </summary>
public sealed record SecurableKindOption(SecurableKind Kind, StringKey LabelKey);

/// <summary>
/// One cell: whether the role holds the privilege, and — the part no other
/// client shows — which grant explains that.
/// </summary>
public sealed partial class PrivilegeCellViewModel(EffectivePrivilege effective, bool editable, Action<PrivilegeCellViewModel> toggle) : ObservableObject
{
    private readonly Action<PrivilegeCellViewModel> _toggle = toggle;

    [ObservableProperty]
    private bool _isPending;

    public EffectivePrivilege Effective { get; } = effective;

    public PrivilegeKind Privilege => Effective.Privilege;

    public string Role => Effective.Role;

    /// <summary>
    /// False where a GRANT/REVOKE would not change the answer: ownership and
    /// superuser are not privileges you can revoke, so offering a toggle there
    /// would promise something the statement cannot deliver.
    /// </summary>
    public bool IsEditable { get; } = editable;

    public bool Granted => Effective.Granted;

    /// <summary>A filled mark for granted, a quiet dash for not — never a checkbox that only looks editable.</summary>
    public string Glyph => IsPending
        ? Granted ? "✕" : "✚"
        : Granted ? "●" : "–";

    /// <summary>
    /// A grant reached through inheritance, PUBLIC, ownership or superuser is
    /// real but derived, and reads dimmer than one that names the role.
    /// </summary>
    public double Weight => Effective.Source switch
    {
        PrivilegeSource.Direct => 1.0,
        PrivilegeSource.None => 0.35,
        _ => 0.7,
    };

    /// <summary>
    /// Unknown means the server said yes and the catalog could not explain why.
    /// It has to look different from a direct grant — dressing it up as one is
    /// exactly the dishonesty this feature exists to avoid.
    /// </summary>
    public bool IsUnexplained => Effective.Source == PrivilegeSource.Unknown;

    public string Tooltip
    {
        get
        {
            var head = Strings.Security_CellTip(Privileges.Sql(Privilege), Effective.Explanation);
            if (IsPending)
            {
                return head + "\n" + (Granted ? Strings.Security_CellTipPendingRevoke : Strings.Security_CellTipPendingGrant);
            }

            return IsEditable
                ? head
                : head + "\n" + Strings.Security_CellTipNotRevocable;
        }
    }

    [RelayCommand]
    private void Toggle()
    {
        if (IsEditable)
        {
            _toggle(this);
        }
    }

    /// <summary>The tooltip says why in words, so it is composed again.</summary>
    public void NotifyLanguageChanged() => OnPropertyChanged(nameof(Tooltip));

    partial void OnIsPendingChanged(bool value)
    {
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(Tooltip));
    }
}

/// <summary>One role's row of the matrix, plus the one-word answer to "how?".</summary>
public sealed class PermissionRowViewModel : ObservableObject
{
    public PermissionRowViewModel(string role, IReadOnlyList<PrivilegeCellViewModel> cells)
    {
        Role = role;
        Cells = cells;
        SourceSummary = Summarize();
    }

    /// <summary>Re-words the summary and every cell's tooltip, for a new language.</summary>
    public void NotifyLanguageChanged()
    {
        SourceSummary = Summarize();
        OnPropertyChanged(nameof(SourceSummary));
        foreach (var cell in Cells)
        {
            cell.NotifyLanguageChanged();
        }
    }

    private string Summarize()
    {
        var cells = Cells;

        // The dominant source, so the answer is readable without hovering every
        // cell. Superuser and owner win outright; otherwise the most common
        // explanation among the privileges actually held.
        var held = cells.Where(c => c.Granted).ToList();
        return held.Count == 0
            ? "—"
            : held.Any(c => c.Effective.Source == PrivilegeSource.Superuser) ? Strings.Security_SourceSuperuser
            : held.Any(c => c.Effective.Source == PrivilegeSource.Owner) ? Strings.Security_SourceOwner
            : held.GroupBy(c => c.Effective.Source == PrivilegeSource.Inherited
                    ? Strings.Security_SourceVia(c.Effective.Via)
                    : c.Effective.Source switch
                    {
                        PrivilegeSource.Direct => Strings.Security_SourceDirect,
                        PrivilegeSource.Public => Strings.Security_SourcePublic,
                        PrivilegeSource.Unknown => Strings.Security_SourceUnknown,
                        _ => "—",
                    })
                .OrderByDescending(g => g.Count())
                .First().Key;
    }

    public string Role { get; }

    public IReadOnlyList<PrivilegeCellViewModel> Cells { get; }

    public string SourceSummary { get; private set; }
}

/// <summary>A column that carries grants of its own, on top of the table's.</summary>
public sealed record ColumnGrantRow(string Column, string Grants);

/// <summary>
/// The permissions matrix — the reason this feature exists.
///
/// Every other Postgres client renders an object's stored ACL, and a stored ACL
/// omits everything a role reaches through group membership, ownership or
/// PUBLIC, so a working permission looks missing. This asks the server directly
/// (<c>has_*_privilege</c>, which expands all of that server-side) and then uses
/// <see cref="EffectivePrivilegeResolver"/> to attribute each answer to a source.
///
/// Nothing here writes. Toggling cells accumulates a change set and produces a
/// GRANT/REVOKE script that opens in the editor.
/// </summary>
public sealed partial class PermissionsTabViewModel(PrivilegeService privileges, SecurityViewModel host) : ObservableObject, ISecuritySection
{
    private readonly PrivilegeService _privileges = privileges;
    private readonly SecurityViewModel _host = host;
    private readonly List<SecurableRef> _allObjects = [];
    private readonly List<PrivilegeChange> _pending = [];

    // Set while this view model is driving its own loads - by SeedForHarness,
    // and by RefreshAsync - so the property setters do not start a second,
    // overlapping read of what is already being fetched.
    private bool _suppressLoads;

    [ObservableProperty]
    private SecurableKind _selectedKind = SecurableKind.Table;

    [ObservableProperty]
    private string? _selectedSchema;

    [ObservableProperty]
    private string _objectFilter = "";

    [ObservableProperty]
    private SecurableRef? _selectedObject;

    [ObservableProperty]
    private string _roleFilter = "";

    [ObservableProperty]
    private bool _showPredefinedRoles;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isDefaultAcl;

    [ObservableProperty]
    private string _ownerLabel = "";

    [ObservableProperty]
    private string _accessSentence = "";

    [ObservableProperty]
    private PermissionRowViewModel? _selectedRow;

    public IReadOnlyList<SecurableKind> Kinds { get; } =
    [
        SecurableKind.Table, SecurableKind.Sequence, SecurableKind.Schema,
        SecurableKind.Database, SecurableKind.Function, SecurableKind.Type,
    ];

    /// <summary>The kinds the picker offers, each with the key of its word.</summary>
    public IReadOnlyList<SecurableKindOption> KindOptions { get; } =
    [
        new(SecurableKind.Table, StringKey.Security_KindOptionTable),
        new(SecurableKind.Sequence, StringKey.Security_KindOptionSequence),
        new(SecurableKind.Schema, StringKey.Security_KindOptionSchema),
        new(SecurableKind.Database, StringKey.Security_KindOptionDatabase),
        new(SecurableKind.Function, StringKey.Security_KindOptionFunction),
        new(SecurableKind.Type, StringKey.Security_KindOptionType),
    ];

    /// <summary><see cref="SelectedKind"/> as the picker's item.</summary>
    public SecurableKindOption SelectedKindOption
    {
        get => KindOptions.First(o => o.Kind == SelectedKind);
        set
        {
            if (value is not null)
            {
                SelectedKind = value.Kind;
            }
        }
    }

    public ObservableCollection<string> Schemas { get; } = [];

    public RangeObservableCollection<SecurableRef> Objects { get; } = [];

    public ObservableCollection<PrivilegeColumn> PrivilegeColumns { get; } = [];

    public RangeObservableCollection<PermissionRowViewModel> Rows { get; } = [];

    public ObservableCollection<ColumnGrantRow> ColumnGrants { get; } = [];

    /// <summary>Set by the view, which owns the dialog. Null in the screenshot harness.</summary>
    public Func<BulkGrantViewModel, Task<bool>>? ShowBulkGrantDialog { get; set; }

    /// <summary>True where the object class has a schema to pick — a database or a schema has none.</summary>
    public bool KindHasSchema => SelectedKind is not (SecurableKind.Database or SecurableKind.Schema);

    public bool HasColumnGrants => ColumnGrants.Count > 0;

    public bool HasObject => SelectedObject is not null;

    public bool HasAccessSentence => AccessSentence.Length > 0;

    public bool HasPending => _pending.Count > 0;

    public string PendingLabel => _pending.Count == 0
        ? Strings.Security_NoPendingChanges
        : Strings.Security_PendingChanges(_pending.Count);

    /// <summary>
    /// Shown instead of an empty grid when the catalog ACL column is NULL. In
    /// Postgres that means nobody has ever run GRANT or REVOKE here — the owner
    /// holds everything and the built-in defaults apply — and rendering it as
    /// "no privileges" is how a permissions UI teaches the wrong thing.
    /// </summary>
    public string DefaultAclNote =>
        Strings.Security_DefaultAclNote(OwnerLabel);

    /// <summary>
    /// Re-reads schemas, objects and the matrix. The matrix reload is driven
    /// explicitly rather than left to the property setters, because the setters
    /// only fire on a *change*: a refresh rebuilds the object list into equal
    /// <see cref="SecurableRef"/> records, so re-selecting the same object is a
    /// no-op and nothing would reload. That is what left a role created in this
    /// window missing from the matrix until Refresh was pressed a second time.
    /// The loads the setters would have started are suppressed for the same
    /// reason, so this is one reload rather than two.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var schemas = await _privileges.GetSecurablesAsync(SecurableKind.Schema, null, ct);
            var previous = SelectedSchema;

            _suppressLoads = true;
            try
            {
                Schemas.Clear();
                foreach (var schema in schemas)
                {
                    Schemas.Add(schema.Name);
                }

                SelectedSchema = previous is not null && Schemas.Contains(previous)
                    ? previous
                    : Schemas.FirstOrDefault();

                await LoadObjectsAsync(ct);
            }
            finally
            {
                _suppressLoads = false;
            }

            await LoadMatrixAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _host.ReportError(Strings.Security_TabPermissions, ex);
        }
    }

    partial void OnSelectedKindChanged(SecurableKind value)
    {
        OnPropertyChanged(nameof(KindHasSchema));
        OnPropertyChanged(nameof(SelectedKindOption));
        if (!_suppressLoads)
        {
            _ = LoadObjectsAsync(CancellationToken.None);
        }
    }

    partial void OnSelectedSchemaChanged(string? value)
    {
        if (!_suppressLoads)
        {
            _ = LoadObjectsAsync(CancellationToken.None);
        }
    }

    partial void OnObjectFilterChanged(string value) => ApplyObjectFilter();

    partial void OnSelectedObjectChanged(SecurableRef? value)
    {
        OnPropertyChanged(nameof(HasObject));
        _pending.Clear();
        NotifyPending();
        if (!_suppressLoads)
        {
            _ = LoadMatrixAsync(CancellationToken.None);
        }
    }

    partial void OnRoleFilterChanged(string value)
    {
        if (!_suppressLoads)
        {
            _ = LoadMatrixAsync(CancellationToken.None);
        }
    }

    partial void OnShowPredefinedRolesChanged(bool value)
    {
        if (!_suppressLoads)
        {
            _ = LoadMatrixAsync(CancellationToken.None);
        }
    }

    partial void OnSelectedRowChanged(PermissionRowViewModel? value)
    {
        if (!_suppressLoads)
        {
            _ = UpdateSentenceAsync(CancellationToken.None);
        }
    }

    partial void OnAccessSentenceChanged(string value) => OnPropertyChanged(nameof(HasAccessSentence));

    /// <summary>
    /// What makes <see cref="AccessSentence"/>, kept so the sentence can be worded
    /// again when the language changes, without asking the server anything.
    /// </summary>
    private Func<string>? _accessSentenceSource;

    private void SetAccessSentence(Func<string>? source)
    {
        _accessSentenceSource = source;
        AccessSentence = source?.Invoke() ?? "";
    }

    /// <summary>The rows, their tooltips, the pending label and the sentences say themselves again.</summary>
    internal void OnLanguageChanged()
    {
        foreach (var row in Rows)
        {
            row.NotifyLanguageChanged();
        }

        OnPropertyChanged(nameof(PendingLabel));
        OnPropertyChanged(nameof(DefaultAclNote));
        if (_accessSentenceSource is { } source)
        {
            AccessSentence = source();
        }
    }

    /// <summary>
    /// Harness-only: points the matrix at a snapshot that was never read from a
    /// server, so the screenshot scenarios and the headless UI tests can render
    /// a populated tab with no database behind them. Production always goes
    /// through <see cref="RefreshAsync"/>; this suppresses the catalog reads the
    /// property setters would otherwise start.
    /// </summary>
    public void SeedForHarness(
        IEnumerable<string> schemas,
        IReadOnlyList<SecurableRef> objects,
        IReadOnlyList<PrivilegeColumn> columns,
        IReadOnlyList<PermissionRowViewModel> rows,
        Func<string> accessSentence,
        IEnumerable<ColumnGrantRow> columnGrants)
    {
        _suppressLoads = true;
        try
        {
            Schemas.Clear();
            foreach (var schema in schemas)
            {
                Schemas.Add(schema);
            }

            SelectedSchema = Schemas.FirstOrDefault();

            _allObjects.Clear();
            _allObjects.AddRange(objects);
            Objects.ReplaceAll(objects);

            SelectedObject = Objects.FirstOrDefault();

            PrivilegeColumns.Clear();
            foreach (var column in columns)
            {
                PrivilegeColumns.Add(column);
            }

            Rows.ReplaceAll(rows);

            SelectedRow = Rows.FirstOrDefault();
            SetAccessSentence(accessSentence);

            ColumnGrants.Clear();
            foreach (var grant in columnGrants)
            {
                ColumnGrants.Add(grant);
            }

            OnPropertyChanged(nameof(HasColumnGrants));
        }
        finally
        {
            _suppressLoads = false;
        }
    }

    private async Task LoadObjectsAsync(CancellationToken ct)
    {
        try
        {
            var schema = KindHasSchema ? SelectedSchema : null;
            var objects = await _privileges.GetSecurablesAsync(SelectedKind, schema, ct);

            _allObjects.Clear();
            _allObjects.AddRange(objects);
            ApplyObjectFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _host.ReportError(Strings.Security_AreaObjects, ex);
        }
    }

    private void ApplyObjectFilter()
    {
        var previous = SelectedObject;
        // One Reset per keystroke: an Add per matching object re-added every
        // table of the schema on each key.
        Objects.ReplaceAll(_allObjects.Where(o =>
            ObjectFilter.Length == 0 || o.Name.Contains(ObjectFilter, StringComparison.OrdinalIgnoreCase)));

        SelectedObject = previous is not null && Objects.Contains(previous) ? previous : Objects.FirstOrDefault();
    }

    /// <summary>
    /// Two reads and one pure resolve. The privilege list must come from the
    /// real server version — asking a pre-PG17 server about MAINTAIN raises
    /// "unrecognized privilege type" and takes the whole matrix with it.
    /// </summary>
    private async Task LoadMatrixAsync(CancellationToken ct)
    {
        Rows.Clear();
        ColumnGrants.Clear();
        OnPropertyChanged(nameof(HasColumnGrants));
        SetAccessSentence(null);

        if (SelectedObject is not { } obj || _host.Graph is not { } graph)
        {
            return;
        }

        try
        {
            var kinds = Privileges.For(obj.Kind, _host.ServerVersion);
            PrivilegeColumns.Clear();
            foreach (var kind in kinds)
            {
                PrivilegeColumns.Add(new PrivilegeColumn(kind, Privileges.Sql(kind)));
            }

            var roles = graph.Roles
                .Where(r => ShowPredefinedRoles || !r.IsPredefined)
                .Where(r => RoleFilter.Length == 0 || r.Name.Contains(RoleFilter, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Name)
                .ToList();

            var acl = await _privileges.GetAclAsync(obj, ct);
            IsDefaultAcl = acl.IsDefaultAcl;
            OwnerLabel = acl.Owner;
            OnPropertyChanged(nameof(DefaultAclNote));

            var answers = await _privileges.GetServerAnswersAsync(obj, roles, kinds, ct);
            var effective = EffectivePrivilegeResolver.Resolve(acl, roles, kinds, graph, answers);
            var byRole = effective.ToLookup(e => e.Role);

            Rows.ReplaceAll(roles.Select(role => new PermissionRowViewModel(
                role,
                kinds
                    .Select(kind => byRole[role].First(e => e.Privilege == kind))
                    .Select(e => new PrivilegeCellViewModel(
                        e,
                        e.Source is not (PrivilegeSource.Owner or PrivilegeSource.Superuser),
                        OnCellToggled))
                    .ToList())));

            SelectedRow = Rows.FirstOrDefault();

            if (obj.Kind == SecurableKind.Table)
            {
                foreach (var column in await _privileges.GetColumnAclsAsync(obj, ct))
                {
                    var grants = string.Join(", ", column.Entries
                        .GroupBy(e => e.GranteeLabel)
                        .Select(g => $"{g.Key}: {string.Join('/', g.Select(e => Privileges.Sql(e.Privilege)))}"));
                    ColumnGrants.Add(new ColumnGrantRow(column.Column, grants));
                }

                OnPropertyChanged(nameof(HasColumnGrants));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _host.ReportError(Strings.Security_TabPermissions, ex);
        }
    }

    /// <summary>
    /// The plain-English answer, for the selected role. It also carries the trap
    /// where every table privilege is granted and the role still cannot reach
    /// the table for want of USAGE on its schema.
    /// </summary>
    private async Task UpdateSentenceAsync(CancellationToken ct)
    {
        if (SelectedObject is not { } obj || SelectedRow is not { } row)
        {
            SetAccessSentence(null);
            return;
        }

        try
        {
            var usage = obj.Schema is null || await _privileges.HasSchemaUsageAsync(row.Role, obj.Schema, ct);
            var effective = row.Cells.Select(c => c.Effective).ToList();
            SetAccessSentence(() => EffectivePrivilegeResolver.ExplainSentence(row.Role, obj, effective, usage));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _host.ReportError(Strings.Security_AreaAccess, ex);
        }
    }

    private void OnCellToggled(PrivilegeCellViewModel cell)
    {
        if (SelectedObject is not { } obj)
        {
            return;
        }

        var existing = _pending.FindIndex(c =>
            c.Privilege == cell.Privilege && string.Equals(c.Grantee, cell.Role, StringComparison.Ordinal));

        if (existing >= 0)
        {
            _pending.RemoveAt(existing);
            cell.IsPending = false;
        }
        else
        {
            // Granted means the pending change is a REVOKE, and vice versa.
            _pending.Add(new PrivilegeChange(obj, cell.Role, cell.Privilege, Grant: !cell.Granted));
            cell.IsPending = true;
        }

        NotifyPending();
    }

    private void NotifyPending()
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingLabel));
        ReviewChangesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasPending))]
    private void ReviewChanges()
    {
        // Null in the screenshot harness, which has no main window to open a tab in.
        if (SelectedObject is not { } obj || _host.OpenSqlInNewTab is not { } openSql)
        {
            return;
        }

        openSql(Strings.Security_GrantsTabTitle(obj.Display), GrantScriptBuilder.Build(_pending));
    }

    [RelayCommand]
    private void ClearPending()
    {
        _pending.Clear();
        foreach (var cell in Rows.SelectMany(r => r.Cells))
        {
            cell.IsPending = false;
        }

        NotifyPending();
    }

    [RelayCommand]
    private async Task BulkGrantAsync()
    {
        if (ShowBulkGrantDialog is not { } show || _host.Graph is not { } graph)
        {
            return;
        }

        var model = new BulkGrantViewModel(
            Schemas,
            graph.Roles.Where(r => !r.IsPredefined).Select(r => r.Name),
            SelectedSchema,
            _host.CurrentRole);

        if (await show(model) && _host.OpenSqlInNewTab is { } openSql)
        {
            openSql(model.ScriptTitle, model.Script);
        }
    }
}
