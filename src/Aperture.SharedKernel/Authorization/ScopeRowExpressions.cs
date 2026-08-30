using System.Linq.Expressions;
using System.Reflection;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.SharedKernel.Authorization;

/// <summary>
/// The <see cref="IScopedResource"/> members of one row, as expressions over a query parameter.
/// <para>
/// The properties are resolved on the <em>concrete</em> entity type rather than on the interface.
/// An interface member access is not something a query provider can map to a column, so binding
/// against <see cref="IScopedResource"/> would compile, run, and silently evaluate the filter on
/// the client — which is exactly the failure 001-P4 exists to rule out.
/// </para>
/// </summary>
public sealed class ScopeRowExpressions
{
    private ScopeRowExpressions(
        ParameterExpression row,
        MemberExpression tenantId,
        MemberExpression ownerUserId,
        MemberExpression teamId,
        MemberExpression regionId,
        MemberExpression accountId)
    {
        Row = row;
        TenantId = tenantId;
        OwnerUserId = ownerUserId;
        TeamId = teamId;
        RegionId = regionId;
        AccountId = accountId;
    }

    /// <summary>The lambda parameter every member expression is rooted at.</summary>
    public ParameterExpression Row { get; }

    public MemberExpression TenantId { get; }

    public MemberExpression OwnerUserId { get; }

    public MemberExpression TeamId { get; }

    public MemberExpression RegionId { get; }

    public MemberExpression AccountId { get; }

    internal static ScopeRowExpressions For<T>(ParameterExpression row)
        where T : IScopedResource =>
        new(
            row,
            Member<T>(row, nameof(IScopedResource.TenantId)),
            Member<T>(row, nameof(IScopedResource.OwnerUserId)),
            Member<T>(row, nameof(IScopedResource.TeamId)),
            Member<T>(row, nameof(IScopedResource.RegionId)),
            Member<T>(row, nameof(IScopedResource.AccountId)));

    private static MemberExpression Member<T>(ParameterExpression row, string name)
    {
        var property = typeof(T).GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

        // An explicit interface implementation is invisible here, and that is deliberate: it
        // would also be invisible to the query provider.
        return property is null
            ? throw new InvalidOperationException(
                $"'{typeof(T).FullName}' has no public instance property '{name}'. " +
                "A scoped entity must implement IScopedResource with public properties so the " +
                "predicate can be translated to SQL.")
            : Expression.Property(row, property);
    }

    /// <summary>
    /// A parameterised constant. Wrapping the value in a field access rather than inlining it
    /// with <see cref="Expression.Constant(object?)"/> makes the query provider emit a SQL
    /// parameter, so a filter applied to every query produces one cached plan instead of one
    /// plan per tenant.
    /// </summary>
    internal static MemberExpression Parameterised<TValue>(TValue value) =>
        Expression.Field(Expression.Constant(new Box<TValue>(value)), nameof(Box<TValue>.Value));

    private sealed class Box<TValue>(TValue value)
    {
        public readonly TValue Value = value;
    }

    internal static Expression TenantEquals(ScopeRowExpressions row, TenantId tenantId) =>
        Expression.Equal(row.TenantId, Parameterised(tenantId));
}
