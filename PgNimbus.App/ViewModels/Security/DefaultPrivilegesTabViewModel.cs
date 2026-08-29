using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Query;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels.Security;

/// <summary>
/// One row of the default-privileges grid: a single grantee's share of one
/// <c>pg_default_acl</c> entry, which is exactly one
/// <c>ALTER DEFAULT PRIVILEGES</c> statement.
///
/// <para>The catalog groups by (creator, schema, object class) and can carry
/// several grantees under one key, so the rows are flattened per grantee — and
/// per grant-option, because <c>WITH GRANT OPTION</c> is a property of the
/// statement, not a footnote on a privilege. Two rows that differ only in it
/// really are two statements.</para>
/// </summary>
public sealed record DefaultPrivilegeRow(
    string OwnerRole,
    string? Schema,
    SecurableKind AppliesTo,
    string? Grantee,
    bool WithGrantOption,
    IReadOnlyList<PrivilegeKind> GrantedPrivileges)
{
    /// <summary>
    /// Null means <c>defaclnamespace = 0</c>: the database-wide default, which
    /// applies wherever the creating role puts the object. Said in words rather
    /// than left as an empty cell, since a blank there reads as "unknown".
    /// </summary>
    public string SchemaLabel => Schema ?? "(all schemas)";

    public string AppliesToLabel => AppliesTo switch
    {
        SecurableKind.Table => "Tables",
        SecurableKind.Sequence => "Sequences",
        SecurableKind.Function => "Functions",
        SecurableKind.Type => "Types",
        SecurableKind.Schema => "Schemas",
        _ => AppliesTo.ToString(),
    };

    /// <summary>The <c>ON …</c> keyword of the statement — plural, unlike <c>GRANT</c>'s.</summary>
    public string ObjectClassKeyword => AppliesTo switch
    {
        SecurableKind.Table => "TABLES",
        SecurableKind.Sequence => "SEQUENCES",
        SecurableKind.Function => "FUNCTIONS",
        SecurableKind.Type => "TYPES",
        SecurableKind.Schema => "SCHEMAS",
        _ => AppliesTo.ToString().ToUpperInvariant(),
    };

    public string GranteeLabel => Grantee ?? GrantScriptBuilder.PublicGrantee;

    /// <summary>
    /// What makes this the same row across a re-read. Record equality is no use
    /// here: the synthesized comparison reaches the privilege list, which is a
    /// fresh <c>List</c> every refresh and compares by reference.
    /// </summary>
    public string Identity =>
        $"{OwnerRole}{Schema}{AppliesTo}{Grantee}{WithGrantOption}";

    public string PrivilegesText => string.Join(", ", GrantedPrivileges.Select(Privileges.Sql));

    /// <summary>Shown beside the privilege list rather than folded into it — it changes the statement.</summary>
    public string GrantOptionNote => WithGrantOption ? "WITH GRANT OPTION" : "";

    /// <summary>The tab title for <see cref="Sql"/>, in the app's "subject · what" shape.</summary>
    public string TabTitle => $"{OwnerRole} · default privileges";

    /// <summary>
    /// The statement this row represents, ready to be edited into a REVOKE or
    /// re-pointed at another creating role. The two comment lines are the two
    /// things people get wrong about default privileges, kept with the SQL
    /// because that is where they will be read.
    /// </summary>
    public string Sql
    {
        get
        {
            var owner = SqlIdentifier.QuoteIfNeeded(OwnerRole);
            var scope = Schema is null ? "" : $" IN SCHEMA {SqlIdentifier.QuoteIfNeeded(Schema)}";
            var grantee = Grantee is null
                ? GrantScriptBuilder.PublicGrantee
                : SqlIdentifier.QuoteIfNeeded(Grantee);
            var option = WithGrantOption ? " WITH GRANT OPTION" : "";

            var where = Schema is null ? "in any schema" : $"in schema {Schema}";
            return $"""
                -- Applies to {ObjectClassKeyword.ToLowerInvariant()} created from now on by {OwnerRole} {where}.
                -- Objects that already exist are untouched: those need
                -- GRANT … ON ALL {ObjectClassKeyword} IN SCHEMA …, which this does not replace.
                --
                -- The key is the CREATING role, not the schema. Pointed at the wrong creator
                -- this statement runs fine and does nothing.
                ALTER DEFAULT PRIVILEGES FOR ROLE {owner}{scope}
                    GRANT {PrivilegesText} ON {ObjectClassKeyword} TO {grantee}{option};
                """;
        }
    }
}

/// <summary>
/// pg_default_acl: what a future object created by a given role will already be
/// granted.
///
/// <para>Nearly a pure explanation tab, and deliberately so. Almost no client
/// surfaces <c>pg_default_acl</c> at all, and a grid on its own would not say
/// either of the things people actually get wrong — that these do not touch
/// objects that already exist, and that they key off the role that *creates* an
/// object rather than the schema it lands in. Both live under the grid, in the
/// note idiom the Database Overview uses for its unused-index caveat.</para>
/// </summary>
public sealed partial class DefaultPrivilegesTabViewModel(PrivilegeService privileges, SecurityViewModel host) : ObservableObject, ISecuritySection
{
    private readonly PrivilegeService _privileges = privileges;
    private readonly SecurityViewModel _host = host;

    /// <summary>
    /// False when nothing is configured, which is a state worth spelling out
    /// rather than rendering as an empty grid — the built-in defaults still
    /// apply, and they are not "nothing".
    /// </summary>
    [ObservableProperty]
    private bool _hasRows;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedAsSqlCommand))]
    private DefaultPrivilegeRow? _selectedRow;

    public ObservableCollection<DefaultPrivilegeRow> Rows { get; } = [];

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var defaults = await _privileges.GetDefaultPrivilegesAsync(ct);

            var selected = SelectedRow;
            Rows.Clear();
            foreach (var row in Flatten(defaults))
            {
                Rows.Add(row);
            }

            HasRows = Rows.Count > 0;

            // The selection survives a Refresh when the row still exists.
            SelectedRow = selected is null
                ? null
                : Rows.FirstOrDefault(r => r.Identity == selected.Identity);
        }
        catch (OperationCanceledException)
        {
            // A superseded snapshot — not an error worth surfacing.
        }
        catch (Exception ex)
        {
            _host.ReportError("Default privileges", ex);
        }
    }

    /// <summary>
    /// One row per (creator, schema, object class, grantee, grant option),
    /// ordered creator-then-schema so the shape of a role's setup reads down the
    /// grid instead of being scattered through it. The database-wide default
    /// sorts above the per-schema ones because that is the order they apply in:
    /// per-schema defaults *add* to it and cannot subtract from it.
    /// </summary>
    private static IEnumerable<DefaultPrivilegeRow> Flatten(IReadOnlyList<DefaultPrivilege> defaults) =>
        defaults
            .SelectMany(d => d.Entries
                .GroupBy(e => (e.Grantee, e.WithGrantOption))
                .Select(g => new DefaultPrivilegeRow(
                    d.OwnerRole,
                    d.Schema,
                    d.AppliesTo,
                    g.Key.Grantee,
                    g.Key.WithGrantOption,
                    SortPrivileges(g.Select(e => e.Privilege), d.AppliesTo))))
            .OrderBy(r => r.OwnerRole, StringComparer.Ordinal)
            .ThenBy(r => r.Schema is not null)
            .ThenBy(r => r.Schema ?? "", StringComparer.Ordinal)
            .ThenBy(r => r.AppliesToLabel, StringComparer.Ordinal)
            .ThenBy(r => r.GranteeLabel, StringComparer.Ordinal)
            .ThenBy(r => r.WithGrantOption);

    /// <summary>
    /// Privileges in <see cref="Privileges.For"/> order — the order the
    /// permissions matrix shows its columns in — so the generated statement and
    /// the grid agree with the rest of the window. Anything outside that list
    /// sorts last rather than disappearing.
    /// </summary>
    private static IReadOnlyList<PrivilegeKind> SortPrivileges(
        IEnumerable<PrivilegeKind> privileges,
        SecurableKind kind)
    {
        var order = Privileges.For(kind);
        return privileges
            .Distinct()
            .OrderBy(p => IndexOf(order, p) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(p => (int)p)
            .ToList();
    }

    /// <summary>Position of <paramref name="privilege"/> in <paramref name="order"/>, or -1.</summary>
    private static int IndexOf(IReadOnlyList<PrivilegeKind> order, PrivilegeKind privilege)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == privilege)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Hands the selected row's statement to the editor, in a new tab (never the
    /// active one), where it can be turned into a REVOKE or aimed at a different
    /// creating role. Null-checked because the screenshot harness has no main
    /// window to open a tab in.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopySelectedAsSql))]
    private void CopySelectedAsSql()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        _host.OpenSqlInNewTab?.Invoke(row.TabTitle, row.Sql);
    }

    private bool CanCopySelectedAsSql() => SelectedRow is not null;
}
