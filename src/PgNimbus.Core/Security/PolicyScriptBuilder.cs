using PgNimbus.Core.Query;

namespace PgNimbus.Core.Security;

/// <summary>
/// The <c>CREATE POLICY</c> a row-level security policy was read back from,
/// which is how a policy gets edited: Postgres has no way to change a policy's
/// command or its permissiveness in place, so re-creating it is the real
/// workflow.
///
/// Pure, like <see cref="GrantScriptBuilder"/>: identifiers go through
/// <see cref="SqlIdentifier.QuoteIfNeeded"/>, the roles through
/// <see cref="GrantScriptBuilder.GranteeSql"/> (so <c>null</c> is the
/// <c>PUBLIC</c> keyword and a role named PUBLIC is <c>"PUBLIC"</c>), and the
/// names in the leading comment through <see cref="SqlComment.Safe"/>.
/// </summary>
public static class PolicyScriptBuilder
{
    private const string Newline = "\n";

    /// <summary>
    /// The statement, preceded — when <paramref name="rowSecurityEnabled"/> is
    /// false — by a comment saying that the policy is inert until row security
    /// is switched on for the table, since re-creating it changes nothing
    /// until that is fixed.
    /// </summary>
    public static string Create(RlsPolicyInfo policy, bool rowSecurityEnabled)
    {
        var table = $"{SqlIdentifier.QuoteIfNeeded(policy.Schema)}.{SqlIdentifier.QuoteIfNeeded(policy.Table)}";
        var roles = policy.AppliesToEveryone
            ? GrantScriptBuilder.PublicGrantee
            : string.Join(", ", policy.Roles.Select(GrantScriptBuilder.GranteeSql));

        var lines = new List<string>();

        if (!rowSecurityEnabled)
        {
            // A relation name may contain a line break, and inside a comment
            // only that matters: it would end the comment and leave the rest
            // of the name as a statement of its own (finding 11).
            lines.Add($"-- {SqlComment.Safe(policy.Schema)}.{SqlComment.Safe(policy.Table)} does not have row-level security enabled, so this");
            lines.Add("-- policy is not applied to anyone. It takes effect only after:");
            lines.Add($"--   ALTER TABLE {SqlComment.Safe(table)} ENABLE ROW LEVEL SECURITY;");
        }

        lines.Add($"CREATE POLICY {SqlIdentifier.QuoteIfNeeded(policy.Name)} ON {table}");
        lines.Add($"    AS {(policy.Permissive ? "PERMISSIVE" : "RESTRICTIVE")}");
        lines.Add($"    FOR {policy.Command}");
        lines.Add($"    TO {roles}");

        if (!string.IsNullOrWhiteSpace(policy.Using))
        {
            lines.Add($"    USING ({policy.Using})");
        }

        if (!string.IsNullOrWhiteSpace(policy.WithCheck))
        {
            lines.Add($"    WITH CHECK ({policy.WithCheck})");
        }

        return string.Join(Newline, lines) + ";";
    }
}
