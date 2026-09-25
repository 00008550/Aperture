using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Contracts.Sales;

/// <summary>
/// The in-process read another module uses to build from a won deal (plan 003-P1): the Orders module creates
/// an order from this snapshot and never touches the <c>sales</c> schema (ARCHITECTURE.md §1). Implemented by
/// the Sales module. A pull, not an event, so it needs no outbox.
/// <para>
/// Scope-filtered with the caller's <see cref="DataScopeSet"/>: an unknown deal, a deal in another tenant,
/// a deal outside the caller's scope, and any lookup with an empty scope set are all
/// <see cref="WonDealLookupStatus.NotFound"/> — indistinguishable, so the caller can answer a non-leaking 404.
/// A visible deal that is not <c>won</c> is <see cref="WonDealLookupStatus.NotWon"/> (a 422 to the caller).
/// </para>
/// </summary>
public interface IWonDealSource
{
    Task<WonDealLookup> GetWonDealAsync(
        DataScopeSet scopes,
        Guid dealId,
        CancellationToken cancellationToken = default);
}

/// <summary>Why a won-deal lookup did or did not yield a snapshot.</summary>
public enum WonDealLookupStatus
{
    /// <summary>Unknown, cross-tenant, out of scope, or an empty scope set — never distinguished.</summary>
    NotFound,

    /// <summary>Visible to the caller, but not in the <c>won</c> stage.</summary>
    NotWon,

    /// <summary>Visible and won; <see cref="WonDealLookup.Deal"/> is set.</summary>
    Won,
}

/// <summary>The lookup verdict. <see cref="Deal"/> is non-null exactly when <see cref="Status"/> is
/// <see cref="WonDealLookupStatus.Won"/>.</summary>
public sealed record WonDealLookup(WonDealLookupStatus Status, WonDealSnapshot? Deal)
{
    public static WonDealLookup NotFound { get; } = new(WonDealLookupStatus.NotFound, null);

    public static WonDealLookup NotWon { get; } = new(WonDealLookupStatus.NotWon, null);

    public static WonDealLookup Won(WonDealSnapshot deal) =>
        new(WonDealLookupStatus.Won, deal ?? throw new ArgumentNullException(nameof(deal)));
}

/// <summary>
/// A won deal as another module may copy it: the ids, the tenant, the five scope facts (tenant, owner, team,
/// region, account) an order inherits, the parent account's name, and the priced lines. Plain values only —
/// no Sales type crosses this boundary.
/// <para>
/// <see cref="AccountName"/> is <c>null</c> when the parent account is not visible under the caller's scope
/// (its scope columns diverged from the deal's): the label fails closed, the deal does not (as the 011-P4
/// read models do).
/// </para>
/// </summary>
public sealed record WonDealSnapshot(
    Guid DealId,
    TenantId TenantId,
    Guid AccountId,
    UserId OwnerUserId,
    Guid? TeamId,
    Guid? RegionId,
    string? AccountName,
    string DealName,
    string? FrozenPriceListVersion,
    IReadOnlyList<WonDealLine> Lines);

/// <summary>One priced line of a won deal, as frozen at the quote.</summary>
public sealed record WonDealLine(string ProductRef, decimal UnitPrice, int Quantity);
