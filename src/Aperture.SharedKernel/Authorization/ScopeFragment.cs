namespace Aperture.SharedKernel.Authorization;

/// <summary>
/// A scoped <c>WHERE</c> fragment and the parameters it references (009-P2): the raw-SQL sibling
/// of <see cref="ScopeQuerying.ToPredicate{T}"/>.
/// <para>
/// <see cref="Sql"/> is a boolean expression the caller <c>AND</c>s into its own query. It is a
/// fragment, never a whole statement — no <c>SELECT</c>, no <c>ORDER BY</c>, no <c>LIMIT</c>, no
/// trailing semicolon — because ordering and paging belong to the caller's query, not to the
/// scope translator. It is never <c>null</c> and never empty: the empty scope set yields a
/// fragment that matches nothing, so a caller writing <c>WHERE 1=1 AND ({Sql})</c> cannot turn an
/// absent fragment into an unfiltered scan.
/// </para>
/// <para>
/// <see cref="Parameters"/> is keyed by parameter name without the leading <c>@</c>, ready to hand
/// to the raw-SQL query runner (009-P3).
/// </para>
/// </summary>
public sealed record ScopeFragment(string Sql, IReadOnlyDictionary<string, object?> Parameters);
