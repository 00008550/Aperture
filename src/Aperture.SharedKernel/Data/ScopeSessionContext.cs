using Aperture.SharedKernel.Authorization;

namespace Aperture.SharedKernel.Data;

/// <summary>
/// The SQL that establishes a principal's scope as PostgreSQL session context, for the raw-SQL read
/// path (009-P3). A <see cref="DataScopeSet"/> becomes six <c>set_config(..., is_local =&gt; true)</c>
/// settings that the row-security policy (<see cref="RowLevelSecurity.ScopeRlsPolicy"/>) reads back —
/// the DBMS, not the caller's <c>WHERE</c>, is what filters the rows.
/// <para>
/// This is the counterpart of <see cref="ScopeSql.ToSqlFragment"/> (the in-app first belt) and of
/// <see cref="ScopeQuerying.ToPredicate{T}"/> (the EF path): three encodings of one rule that the
/// differential test pins to the same result set. Here the rule is carried as GUC values that the
/// policy's <c>USING</c> predicate turns back into the same union.
/// </para>
/// <para>
/// <b>Every scope value is a bound parameter, never interpolated.</b> Only the setting <em>names</em>
/// appear inline, and those are compile-time constants declared on this type — never caller input.
/// An injection through this path would be exactly the failure the raw-SQL path exists to prevent.
/// </para>
/// <para>
/// <b>Fail-closed by default.</b> A connection that never runs this SQL leaves every setting unset;
/// <c>current_setting(name, true)</c> is then <c>NULL</c>, the policy's tenant equality is unknown,
/// and it returns zero rows — not everything. The empty set is the same: it sets the tenant but
/// leaves every grant setting empty, so the policy's grant union is false for every row.
/// </para>
/// </summary>
public static class ScopeSessionContext
{
    /// <summary>The tenant boundary. Conjoined outside the grant union in the policy; unreachable past.</summary>
    public const string TenantIdSetting = "app.tenant_id";

    /// <summary>The principal's own id, for the <see cref="DataScope.Self"/> grant.</summary>
    public const string UserIdSetting = "app.user_id";

    /// <summary>Comma-separated team ids granted, for the <see cref="DataScope.Team"/> grants.</summary>
    public const string TeamsSetting = "app.teams";

    /// <summary>Comma-separated region ids granted, for the <see cref="DataScope.Region"/> grants.</summary>
    public const string RegionsSetting = "app.regions";

    /// <summary>Comma-separated account ids granted, for the <see cref="DataScope.Account"/> grants.</summary>
    public const string AccountsSetting = "app.accounts";

    /// <summary><c>true</c> when an <see cref="DataScope.AllTenant"/> grant is present.</summary>
    public const string AllTenantSetting = "app.all_tenant";

    /// <summary>
    /// Builds the <c>set_config</c> statement and its parameters for <paramref name="scopes"/>.
    /// Run it as the first statement of a transaction (the settings are transaction-local, so they
    /// cannot leak across pooled reuse) and before any scoped read on that connection.
    /// </summary>
    public static ScopeSession Build(DataScopeSet scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        // Self is the principal themselves, so a set holds at most one distinct Self grant. The
        // singular app.user_id setting cannot represent two, and silently dropping one would widen
        // the deny — so it fails loud rather than fails open.
        var selfIds = scopes.Scopes
            .OfType<DataScope.Self>()
            .Select(s => s.UserId.Value)
            .Distinct()
            .ToArray();

        if (selfIds.Length > 1)
        {
            throw new ArgumentException(
                "A scope set with more than one distinct Self grant cannot be expressed through the " +
                "singular app.user_id session setting.",
                nameof(scopes));
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tenant"] = scopes.TenantId.Value.ToString(),
            ["user"] = selfIds.Length == 1 ? selfIds[0].ToString() : string.Empty,
            ["teams"] = Csv(scopes.Scopes.OfType<DataScope.Team>().Select(s => s.TeamId)),
            ["regions"] = Csv(scopes.Scopes.OfType<DataScope.Region>().Select(s => s.RegionId)),
            ["accounts"] = Csv(scopes.Scopes.OfType<DataScope.Account>().Select(s => s.AccountId)),
            ["all_tenant"] = scopes.Scopes.OfType<DataScope.AllTenant>().Any() ? "true" : "false",
        };

        // Setting names are constants on this type; only @-prefixed parameters carry values.
        var sql =
            $"""
             SELECT
               set_config('{TenantIdSetting}', @tenant, true),
               set_config('{UserIdSetting}', @user, true),
               set_config('{TeamsSetting}', @teams, true),
               set_config('{RegionsSetting}', @regions, true),
               set_config('{AccountsSetting}', @accounts, true),
               set_config('{AllTenantSetting}', @all_tenant, true);
             """;

        return new ScopeSession(sql, parameters);
    }

    private static string Csv(IEnumerable<Guid> ids) =>
        string.Join(",", ids.Select(id => id.ToString()));
}

/// <summary>
/// The <c>set_config</c> SQL from <see cref="ScopeSessionContext.Build"/> and the parameter values it
/// references, keyed by name without the leading <c>@</c>. Values are strings — <c>set_config</c>
/// takes text — and the policy casts them back (<c>::uuid</c>, <c>::uuid[]</c>, <c>::bool</c>).
/// </summary>
public sealed record ScopeSession(string Sql, IReadOnlyDictionary<string, object?> Parameters);
