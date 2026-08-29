using System.Collections.Immutable;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.SharedKernel.Authorization;

/// <summary>
/// Every data scope a principal holds, in one tenant. Scopes compose as a <em>union</em>: a lead
/// with <c>Team(A)</c> and <c>Region(North)</c> sees rows in either.
/// <para>
/// A scope set is meaningless without the tenant it was resolved for, so it carries one. The
/// tenant check runs first and cannot be reached past — not even by
/// <see cref="DataScope.AllTenant"/>, which means "everything in this tenant" and never
/// "everything".
/// </para>
/// <para>
/// <b>The empty set admits nothing.</b> This is the failure recorded in DOMAIN.md §5.1 — a
/// filter that read "no regions selected" as "all regions" — encoded so it cannot recur:
/// <see cref="None"/> is a value with the same type as any other set, and callers ask it
/// <see cref="Admits"/> rather than inspecting a count and deciding for themselves.
/// </para>
/// </summary>
public sealed class DataScopeSet : IEquatable<DataScopeSet>
{
    private readonly ImmutableHashSet<DataScope> _scopes;

    private DataScopeSet(TenantId tenantId, ImmutableHashSet<DataScope> scopes)
    {
        TenantId = tenantId;
        _scopes = scopes;
    }

    /// <summary>The tenant these scopes were resolved for.</summary>
    public TenantId TenantId { get; }

    public bool IsEmpty => _scopes.Count == 0;

    public int Count => _scopes.Count;

    public IReadOnlyCollection<DataScope> Scopes => _scopes;

    /// <summary>A set that admits nothing. The correct default for an unresolved principal.</summary>
    public static DataScopeSet None(TenantId tenantId) =>
        new(tenantId, ImmutableHashSet<DataScope>.Empty);

    public static DataScopeSet Of(TenantId tenantId, params DataScope[] scopes) =>
        Of(tenantId, (IEnumerable<DataScope>)scopes);

    public static DataScopeSet Of(TenantId tenantId, IEnumerable<DataScope> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        // Records give value equality, so duplicate grants collapse and two sets built in a
        // different order compare equal — which is what makes caching them safe.
        return new DataScopeSet(tenantId, [.. scopes]);
    }

    /// <summary>
    /// Whether this principal may see <paramref name="resource"/>. Wrong tenant, or no scopes,
    /// means no.
    /// </summary>
    public bool Admits(IScopedResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (resource.TenantId != TenantId)
        {
            return false;
        }

        foreach (var scope in _scopes)
        {
            if (scope.Admits(resource))
            {
                return true;
            }
        }

        return false;
    }

    public bool Equals(DataScopeSet? other) =>
        other is not null && TenantId == other.TenantId && _scopes.SetEquals(other._scopes);

    public override bool Equals(object? obj) => Equals(obj as DataScopeSet);

    public override int GetHashCode()
    {
        // Order-independent: the set is unordered, so the hash must be too.
        var accumulated = 0;
        foreach (var scope in _scopes)
        {
            accumulated ^= scope.GetHashCode();
        }

        return HashCode.Combine(TenantId, accumulated);
    }
}
