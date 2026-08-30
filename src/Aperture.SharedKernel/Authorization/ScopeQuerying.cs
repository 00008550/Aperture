using System.Linq.Expressions;

namespace Aperture.SharedKernel.Authorization;

/// <summary>
/// Translates a <see cref="DataScopeSet"/> into a query predicate (001-P4).
/// <para>
/// The in-memory <see cref="DataScopeSet.Admits"/> from 001-P1 answers "may this principal see
/// this row I already loaded". That is the wrong question for a list endpoint: loading the rows
/// and then discarding them is both a performance problem and, once paging is involved, a
/// correctness one. This turns the same semantics into a <c>WHERE</c> clause, so the database
/// never hands over rows the principal may not see.
/// </para>
/// <para>
/// The two must stay the same rule. Each <see cref="DataScope"/> case owns both forms, side by
/// side, so they cannot drift apart in separate files.
/// </para>
/// </summary>
public static class ScopeQuerying
{
    /// <summary>
    /// The predicate form of <paramref name="scopes"/>: the tenant boundary, and then the union
    /// of the individual grants.
    /// <para>
    /// <b>An empty set yields a predicate that matches nothing</b> — the DOMAIN.md §5.1 incident
    /// in its SQL form. There is no branch here that returns an unfiltered query, because the
    /// only way that bug gets written is when such a branch exists.
    /// </para>
    /// </summary>
    public static Expression<Func<T, bool>> ToPredicate<T>(this DataScopeSet scopes)
        where T : IScopedResource
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var parameter = Expression.Parameter(typeof(T), "row");

        // Nothing granted, nothing visible. Written before the row members are even bound, so
        // it cannot accidentally acquire a widening branch later.
        if (scopes.IsEmpty)
        {
            return Expression.Lambda<Func<T, bool>>(Expression.Constant(false), parameter);
        }

        var row = ScopeRowExpressions.For<T>(parameter);

        Expression? union = null;
        foreach (var scope in scopes.Scopes)
        {
            var body = scope.ToPredicateBody(row);
            union = union is null ? body : Expression.OrElse(union, body);
        }

        // union is non-null: the set is not empty, and every scope contributes a body.
        var predicate = Expression.AndAlso(
            ScopeRowExpressions.TenantEquals(row, scopes.TenantId),
            union!);

        return Expression.Lambda<Func<T, bool>>(predicate, parameter);
    }

    /// <summary>
    /// Composes <see cref="ToPredicate{T}"/> into <paramref name="source"/>. Composed rather
    /// than enumerated: the caller keeps an <see cref="IQueryable{T}"/>, so ordering, paging and
    /// projection still run in the database on top of the scoped set.
    /// </summary>
    public static IQueryable<T> WhereInScope<T>(this IQueryable<T> source, DataScopeSet scopes)
        where T : IScopedResource
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Where(scopes.ToPredicate<T>());
    }
}
