using Aperture.SharedKernel.Authorization;

namespace Aperture.Contracts.Sales;

/// <summary>
/// The live credit limit of an account (plan 003-P1), read at order confirmation rather than snapshotted —
/// the limit lives in Sales and changes. Implemented by the Sales module.
/// <para>
/// Scope-filtered with the caller's <see cref="DataScopeSet"/>: an unknown, cross-tenant or out-of-scope
/// account, or an empty scope set, yields <c>null</c>. A caller must treat <c>null</c> as "cannot confirm",
/// never as "no limit" (fail closed).
/// </para>
/// </summary>
public interface IAccountCreditReader
{
    Task<decimal?> GetCreditLimitAsync(
        DataScopeSet scopes,
        Guid accountId,
        CancellationToken cancellationToken = default);
}
