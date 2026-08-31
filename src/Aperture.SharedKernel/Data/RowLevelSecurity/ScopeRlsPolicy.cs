using System.Text.RegularExpressions;

namespace Aperture.SharedKernel.Data.RowLevelSecurity;

/// <summary>
/// The row-level-security convention a scoped table adopts (009-P3): the SQL that binds the
/// <see cref="ScopeSessionContext"/> settings to a table so the DBMS re-asserts tenant + scope on
/// every read, regardless of how the query was written. This is the enforcement point the in-app
/// fragment could not be — a policy applied below the SQL string cannot be <c>OR</c>-ed away,
/// commented out, or escaped by an unbalanced paren in caller SQL.
/// <para>
/// The <c>USING</c> predicate is the third encoding of the scope rule, and it is built here from the
/// same setting names <see cref="ScopeSessionContext"/> writes, so the two cannot drift on the GUC
/// names. That it encodes the same <em>union</em> as <see cref="ScopeSql"/> and
/// <see cref="ScopeQuerying"/> is proven at the DBMS by the differential test, not by inspection.
/// </para>
/// <para>
/// <b>Least privilege, not forced.</b> The policy is <c>FOR SELECT TO</c> the dedicated reader role
/// only, and RLS is left <c>NO FORCE</c> deliberately: a table's owner (the role EF and migrations
/// use) bypasses RLS, so enabling a policy never changes EF behaviour. The blast radius is the reader
/// role and nothing else.
/// </para>
/// </summary>
public static class ScopeRlsPolicy
{
    /// <summary>The least-privilege login role the policy binds. Provisioned by an Access migration.</summary>
    public const string ReaderRole = "aperture_reader";

    /// <summary>The policy name given to every scoped table, so the convention is greppable.</summary>
    public const string PolicyName = "aperture_scope";

    private static readonly Regex Identifier =
        new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The SQL that makes <paramref name="table"/> in <paramref name="schema"/> scope-enforced for the
    /// reader role, using the snake_case default column names. Idempotent — safe to run in a migration
    /// that may re-apply, and in a test fixture shared across a collection.
    /// </summary>
    public static string Enable(string schema, string table) =>
        Enable(schema, table, "tenant_id", "owner_user_id", "team_id", "region_id", "account_id");

    /// <summary>
    /// As <see cref="Enable(string,string)"/>, for a table whose scope columns are not the snake_case
    /// defaults. Every identifier is validated — these are emitted inline, so a non-identifier is an
    /// <see cref="ArgumentException"/>, never a sanitised-and-continued string.
    /// </summary>
    public static string Enable(
        string schema,
        string table,
        string tenantId,
        string ownerUserId,
        string teamId,
        string regionId,
        string accountId)
    {
        var s = Ident(schema, nameof(schema));
        var t = Ident(table, nameof(table));
        var qualified = $"{s}.{t}";

        var predicate = UsingPredicate(
            Ident(tenantId, nameof(tenantId)),
            Ident(ownerUserId, nameof(ownerUserId)),
            Ident(teamId, nameof(teamId)),
            Ident(regionId, nameof(regionId)),
            Ident(accountId, nameof(accountId)));

        return
            $"""
             ALTER TABLE {qualified} ENABLE ROW LEVEL SECURITY;
             ALTER TABLE {qualified} NO FORCE ROW LEVEL SECURITY;
             DROP POLICY IF EXISTS {PolicyName} ON {qualified};
             CREATE POLICY {PolicyName} ON {qualified}
                 FOR SELECT TO {ReaderRole}
                 USING (
             {predicate}
                 );
             GRANT USAGE ON SCHEMA {s} TO {ReaderRole};
             GRANT SELECT ON {qualified} TO {ReaderRole};
             """;
    }

    /// <summary>
    /// The boolean the policy evaluates per row. Tenant equality is conjoined <em>outside</em> the
    /// grant union and cannot be reached past — the SQL form of the same rule
    /// <see cref="ScopeSql.ToSqlFragment"/> emits and <see cref="ScopeQuerying"/> builds.
    /// <list type="bullet">
    /// <item>Unset context → <c>current_setting(..., true)</c> is <c>NULL</c> → tenant equality is
    /// unknown → zero rows. Fail-closed by default.</item>
    /// <item>Empty grant settings → every disjunct is false (an empty array via <c>nullif</c> makes
    /// <c>= ANY(NULL)</c> unknown, <c>all_tenant</c> is <c>false</c>) → nothing admitted.</item>
    /// <item><c>NULL</c> team/region/account column → <c>NULL = ANY(...)</c> is unknown, not a match →
    /// absent data narrows, never widens.</item>
    /// </list>
    /// </summary>
    public static string UsingPredicate(
        string tenantId,
        string ownerUserId,
        string teamId,
        string regionId,
        string accountId) =>
        $"""
                 {tenantId} = nullif(current_setting('{ScopeSessionContext.TenantIdSetting}', true), '')::uuid
                 AND (
                     current_setting('{ScopeSessionContext.AllTenantSetting}', true)::bool
                     OR {ownerUserId} = nullif(current_setting('{ScopeSessionContext.UserIdSetting}', true), '')::uuid
                     OR {AnyOf(teamId, ScopeSessionContext.TeamsSetting)}
                     OR {AnyOf(regionId, ScopeSessionContext.RegionsSetting)}
                     OR {AnyOf(accountId, ScopeSessionContext.AccountsSetting)}
                 )
         """;

    // column = ANY(<the setting parsed as a uuid[]>). nullif(...,'') makes an unset or empty setting
    // NULL, so string_to_array is never handed '' (which would yield {''} and fail the ::uuid[] cast),
    // and = ANY(NULL) is unknown — the empty grant admits nothing.
    private static string AnyOf(string column, string setting) =>
        $"{column} = ANY(string_to_array(nullif(current_setting('{setting}', true), ''), ',')::uuid[])";

    private static string Ident(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);

        return Identifier.IsMatch(value)
            ? value
            : throw new ArgumentException(
                $"'{value}' is not a plain SQL identifier. Schema, table and column names are emitted " +
                "inline in the policy, so only [A-Za-z_][A-Za-z0-9_]* is accepted.",
                paramName);
    }
}
