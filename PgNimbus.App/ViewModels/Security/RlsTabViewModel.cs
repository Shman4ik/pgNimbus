using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Query;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels.Security;

/// <summary>
/// One policy, shaped for the detail pane. The <c>USING</c> and
/// <c>WITH CHECK</c> quals are SQL expressions, so they are carried whole and
/// rendered monospace and selectable — truncating them into a grid cell is how
/// the only part of a policy that says what it does becomes unreadable.
/// </summary>
public sealed record RlsPolicyRow(RlsPolicyInfo Policy, bool RowSecurityEnabled)
{
    public string Name => Policy.Name;

    /// <summary>
    /// PERMISSIVE policies are OR-ed together and RESTRICTIVE ones are AND-ed on
    /// top, so which one a policy is decides whether it widens or narrows access.
    /// </summary>
    public string Kind => Policy.Permissive ? "PERMISSIVE" : "RESTRICTIVE";

    public string Command => Policy.Command;

    /// <summary>A lone "public" entry is not a role — it is every role.</summary>
    public string RolesLabel => IsForEveryone
        ? "public (every role)"
        : string.Join(", ", Policy.Roles);

    public bool IsForEveryone =>
        Policy.Roles.Count == 0
        || Policy.Roles.Any(r => r.Equals("public", StringComparison.OrdinalIgnoreCase));

    public string? Using => Policy.Using;

    public bool HasUsing => !string.IsNullOrWhiteSpace(Policy.Using);

    public string? WithCheck => Policy.WithCheck;

    public bool HasWithCheck => !string.IsNullOrWhiteSpace(Policy.WithCheck);

    public string TabTitle => $"{Policy.Table} · {Policy.Name}";

    /// <summary>
    /// The <c>CREATE POLICY</c> this row was read back from, which is how a
    /// policy gets edited: Postgres has no way to change a policy's command or
    /// its permissiveness in place, so re-creating it is the real workflow.
    /// </summary>
    public string Sql
    {
        get
        {
            var table = $"{SqlIdentifier.QuoteIfNeeded(Policy.Schema)}.{SqlIdentifier.QuoteIfNeeded(Policy.Table)}";
            var roles = IsForEveryone
                ? GrantScriptBuilder.PublicGrantee
                : string.Join(", ", Policy.Roles.Select(SqlIdentifier.QuoteIfNeeded));

            var lines = new List<string>();

            // A policy on a table with row security switched off is inert, and
            // re-creating it changes nothing until that is fixed. Say so where
            // the statement is about to be edited.
            if (!RowSecurityEnabled)
            {
                lines.Add($"-- {Policy.Schema}.{Policy.Table} does not have row-level security enabled, so this");
                lines.Add("-- policy is not applied to anyone. It takes effect only after:");
                lines.Add($"--   ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
            }

            lines.Add($"CREATE POLICY {SqlIdentifier.QuoteIfNeeded(Policy.Name)} ON {table}");
            lines.Add($"    AS {Kind}");
            lines.Add($"    FOR {Policy.Command}");
            lines.Add($"    TO {roles}");

            if (HasUsing)
            {
                lines.Add($"    USING ({Policy.Using})");
            }

            if (HasWithCheck)
            {
                lines.Add($"    WITH CHECK ({Policy.WithCheck})");
            }

            return string.Join("\n", lines) + ";";
        }
    }
}

/// <summary>
/// One table's row-level security state, plus the sentence that explains a
/// bypass when there is one.
/// </summary>
/// <param name="BypassReason">
/// Why the connected role sees every row anyway, or null when it does not. Built
/// by the view model, which is the only place that knows the connected role's
/// attributes.
/// </param>
public sealed record RlsTableRow(RlsTableState State, string? BypassReason)
{
    public string Schema => State.Schema;

    public string Table => State.Table;

    public string Display => $"{State.Schema}.{State.Table}";

    /// <summary>
    /// What makes this the same table across a re-read. Record equality is no
    /// use here: the synthesized comparison reaches the policy list, which is a
    /// fresh <c>List</c> every refresh and compares by reference.
    /// </summary>
    public string Identity => Display;

    public string StatusLabel => State.RowSecurityEnabled
        ? State.ForceRowSecurity ? "Enabled · FORCE" : "Enabled"
        : "Not enabled";

    public string PolicyCountLabel =>
        State.Policies.Count == 1 ? "1 policy" : $"{State.Policies.Count} policies";

    /// <summary>Policies exist and none of them does anything — protection that only looks like protection.</summary>
    public bool IsInert => State.HasInertPolicies;

    /// <summary>
    /// RLS on with no policies at all: the default-deny state, which hides every
    /// row from every role that does not bypass. Usually deliberate, occasionally
    /// a half-finished migration, and invisible either way without saying it.
    /// </summary>
    public bool DeniesEverything => State.RowSecurityEnabled && State.Policies.Count == 0;

    public bool IsBypassed => State.BypassedByCurrentRole;

    public bool IsForced => State.ForceRowSecurity;

    public IReadOnlyList<RlsPolicyRow> Policies { get; } =
        State.Policies.Select(p => new RlsPolicyRow(p, State.RowSecurityEnabled)).ToList();

    public bool HasPolicies => Policies.Count > 0;
}

/// <summary>
/// Row-level security: which tables have it, which policies apply, and who
/// bypasses it.
///
/// <para>Read-only, like the rest of v1. The value is in the two states a plain
/// policy listing cannot show: policies that exist on a table where row security
/// was never switched on (they look like protection and are not), and a
/// connected role that bypasses row security entirely (the policies are right
/// and the person testing them cannot tell).</para>
/// </summary>
public sealed partial class RlsTabViewModel : ObservableObject, ISecuritySection
{
    /// <summary>The schema-filter entry that means "do not filter". Not a schema name.</summary>
    public const string AllSchemas = "(all schemas)";

    private readonly PrivilegeService _privileges;
    private readonly SecurityViewModel _host;

    /// <summary>
    /// Every table read, before the schema filter. Kept so the filter is a local
    /// re-slice rather than a round trip — and so the schema list stays whole
    /// once a schema is picked, instead of collapsing to the one selected.
    /// </summary>
    private IReadOnlyList<RlsTableRow> _allTables = [];

    /// <summary>
    /// Nullable because a <c>ComboBox</c> whose items are being rebuilt pushes a
    /// null selection back through the binding before the new list settles; the
    /// filter reads null as "no filter" rather than as a schema named nothing.
    /// </summary>
    [ObservableProperty]
    private string? _selectedSchema = AllSchemas;

    [ObservableProperty]
    private RlsTableRow? _selectedTable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyPolicyAsSqlCommand))]
    private RlsPolicyRow? _selectedPolicy;

    [ObservableProperty]
    private bool _hasTables;

    public RlsTabViewModel(PrivilegeService privileges, SecurityViewModel host)
    {
        _privileges = privileges;
        _host = host;
        Schemas.Add(AllSchemas);
    }

    /// <summary>The schema filter's entries: <see cref="AllSchemas"/> first, then every schema that has RLS.</summary>
    public ObservableCollection<string> Schemas { get; } = [];

    public ObservableCollection<RlsTableRow> Tables { get; } = [];

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            // Read every schema, always. The filter below is a view over this
            // one snapshot: re-reading per schema would cost a round trip per
            // selection and leave nothing to build the schema list from.
            var states = await _privileges.GetRlsAsync(null, ct);

            _allTables = states.Select(s => new RlsTableRow(s, BypassReasonFor(s))).ToList();

            var schemas = _allTables
                .Select(t => t.Schema)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            var previous = SelectedSchema;

            // Rebuild the combo only when the set of schemas actually moved.
            // Clearing it makes the ComboBox push a null selection back through
            // the binding, so doing it on every refresh would churn the filter
            // for nothing.
            if (!Schemas.Skip(1).SequenceEqual(schemas, StringComparer.Ordinal))
            {
                Schemas.Clear();
                Schemas.Add(AllSchemas);
                foreach (var schema in schemas)
                {
                    Schemas.Add(schema);
                }
            }

            // Setting the property re-filters; when it is unchanged, filter by
            // hand so a refresh still picks up new tables.
            var kept = previous is not null && schemas.Contains(previous, StringComparer.Ordinal)
                ? previous
                : AllSchemas;

            if (SelectedSchema == kept)
            {
                ApplyFilter();
            }
            else
            {
                SelectedSchema = kept;
            }
        }
        catch (OperationCanceledException)
        {
            // A superseded snapshot — not an error worth surfacing.
        }
        catch (Exception ex)
        {
            _host.ReportError("Row-level security", ex);
        }
    }

    partial void OnSelectedSchemaChanged(string? value) => ApplyFilter();

    /// <summary>
    /// Re-points the policy selection whenever the table changes — never left
    /// pointing at a policy belonging to a table that is no longer on screen,
    /// and landing on the new table's first policy so the Copy action has a
    /// target the moment a table is picked.
    /// </summary>
    partial void OnSelectedTableChanged(RlsTableRow? value) =>
        SelectedPolicy = value?.Policies.FirstOrDefault();

    private void ApplyFilter()
    {
        var selected = SelectedTable;
        var schema = SelectedSchema;
        var all = schema is null || schema == AllSchemas;

        Tables.Clear();
        foreach (var table in _allTables)
        {
            if (all || string.Equals(table.Schema, schema, StringComparison.Ordinal))
            {
                Tables.Add(table);
            }
        }

        HasTables = Tables.Count > 0;

        // Keep the user where they were across a refresh, falling back to the
        // first table so the detail pane is never blank next to a populated list.
        SelectedTable = (selected is null
            ? null
            : Tables.FirstOrDefault(t => t.Identity == selected.Identity))
            ?? Tables.FirstOrDefault();
    }

    /// <summary>
    /// Why the connected role sees every row of <paramref name="state"/>
    /// regardless of its policies, or null when it does not.
    ///
    /// <para>The two routes are not the same thing and the wording must not blur
    /// them: a superuser or a <c>BYPASSRLS</c> role skips row security on every
    /// table, always, and <c>FORCE ROW LEVEL SECURITY</c> does not touch that;
    /// the table's owner skips it only while the table is not FORCE, so FORCE is
    /// the fix in that case and no fix at all in the other.</para>
    /// </summary>
    private string? BypassReasonFor(RlsTableState state)
    {
        if (!state.BypassedByCurrentRole)
        {
            return null;
        }

        var role = _host.CurrentRole;
        var attributes = _host.Graph?.Find(role);

        if (attributes is null)
        {
            return $"{role} sees every row in this table, whatever the policies below say. "
                + "Its role attributes could not be read, so this is either a superuser or BYPASSRLS role "
                + "(which bypasses every table, always) or ownership of a table that is not FORCE ROW LEVEL SECURITY.";
        }

        if (attributes.IsSuperuser)
        {
            return $"{role} is a superuser, so it bypasses row-level security on every table in the cluster. "
                + "That is unconditional: FORCE ROW LEVEL SECURITY does not apply the policies to it. "
                + "Test these policies as the role your application connects as.";
        }

        if (attributes.BypassRls)
        {
            return $"{role} has the BYPASSRLS attribute, so it bypasses row-level security on every table, always. "
                + "FORCE ROW LEVEL SECURITY does not apply the policies to it. "
                + "Test these policies as the role your application connects as.";
        }

        return $"{role} owns this table, or is a member of the role that does, and the table is not "
            + "FORCE ROW LEVEL SECURITY — so the policies below are not applied to it, though they are "
            + "applied to every other role. ALTER TABLE … FORCE ROW LEVEL SECURITY makes them apply to the owner too.";
    }

    /// <summary>
    /// Drops the selected policy's <c>CREATE POLICY</c> into a new editor tab —
    /// how a policy is edited, since Postgres cannot alter a policy's command or
    /// permissiveness in place. Null-checked: the screenshot harness has no main
    /// window to open a tab in.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyPolicyAsSql))]
    private void CopyPolicyAsSql()
    {
        if (SelectedPolicy is not { } policy)
        {
            return;
        }

        _host.OpenSqlInNewTab?.Invoke(policy.TabTitle, policy.Sql);
    }

    private bool CanCopyPolicyAsSql() => SelectedPolicy is not null;
}
