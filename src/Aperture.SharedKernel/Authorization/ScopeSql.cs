namespace Aperture.SharedKernel.Authorization;

/// <summary>
/// Translates a <see cref="DataScopeSet"/> into a raw-SQL <see cref="ScopeFragment"/> for the
/// raw-SQL read path (009-P2) — the sibling of <see cref="ScopeQuerying"/>'s <c>IQueryable</c> path.
/// <para>
/// The two are one rule with two forms: each <see cref="DataScope"/> case owns both a
/// <c>ToPredicateBody</c> and a <c>ToSqlFragment</c>, side by side, so they cannot drift apart.
/// The structure here mirrors <see cref="ScopeQuerying.ToPredicate{T}"/> deliberately — the tenant
/// term is conjoined <em>outside</em> the union of grants and cannot be reached past, and the
/// empty set matches nothing before any grant is bound.
/// </para>
/// </summary>
public static class ScopeSql
{
    /// <summary>
    /// The fragment form of <paramref name="scopes"/>: <c>({alias}.tenant_id = @tenant) AND (…grant
    /// union…)</c>, with every scope value bound as a parameter and only the validated alias and
    /// column names appearing inline.
    /// <para>
    /// <b>An empty set yields a fragment that matches nothing</b> — the tenant term followed by
    /// <c>1 = 0</c>, decided before any grant is bound, so no widening branch can be added later.
    /// The result is never <c>null</c> and never empty.
    /// </para>
    /// </summary>
    public static ScopeFragment ToSqlFragment(this DataScopeSet scopes, ScopeColumns columns)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(columns);

        var parameters = new ScopeParameterBag(columns.Alias);

        // The tenant boundary, conjoined outside the union below. Emitted first and always — no
        // grant, including AllTenant, can be OR-ed past it.
        var tenantTerm = $"({columns.TenantId} = {parameters.AddTenant(scopes.TenantId.Value)})";

        // Nothing granted, nothing visible. 1 = 0 is chosen before any grant column is bound, so it
        // cannot accidentally acquire a widening branch — the SQL form of ScopeQuerying's early
        // Expression.Constant(false).
        var union = scopes.IsEmpty
            ? "1 = 0"
            : string.Join(" OR ", scopes.Scopes.Select(scope => scope.ToSqlFragment(columns, parameters)));

        return new ScopeFragment($"{tenantTerm} AND ({union})", parameters.Snapshot());
    }
}
