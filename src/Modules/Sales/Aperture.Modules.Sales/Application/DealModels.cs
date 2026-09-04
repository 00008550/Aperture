using Aperture.SharedKernel.Authorization;

namespace Aperture.Modules.Sales.Application;

/// <summary>
/// What a caller supplies to create a deal. Note what is <em>absent</em>: none of the five scope columns
/// (tenant, owner, team, region) — those are inherited from the parent account by the service, never named
/// by the caller. The account is named by <see cref="AccountId"/> and validated to be in the caller's
/// scope; the stage is always <c>new</c> on create, so it is not the caller's to state either.
/// </summary>
public sealed record CreateDealRequest(
    Guid AccountId,
    string Name,
    decimal Amount,
    decimal DiscountPct);

/// <summary>What a caller supplies to add a line to a deal: a product, a unit price, a quantity, and the
/// price-list version it was priced against.</summary>
public sealed record AddDealLineRequest(
    string ProductRef,
    decimal UnitPrice,
    int Quantity,
    string? PriceListVersion);

/// <summary>The read model for one deal line.</summary>
public sealed record DealLineView(
    Guid Id,
    Guid DealId,
    string ProductRef,
    decimal UnitPrice,
    int Quantity,
    string? PriceListVersion);

/// <summary>The read model for one deal — the shape the console and the assistant read. The grid returns
/// it without <see cref="Lines"/> (an empty list); a single-deal read includes them.</summary>
public sealed record DealView(
    Guid Id,
    Guid TenantId,
    Guid AccountId,
    Guid OwnerUserId,
    Guid? TeamId,
    Guid? RegionId,
    string Name,
    string Stage,
    decimal Amount,
    decimal DiscountPct,
    string? FrozenPriceListVersion,
    bool PendingApproval,
    string? LostReasonCode,
    DateTimeOffset CreatedAt,
    uint Version,
    IReadOnlyList<DealLineView> Lines);

public enum DealCreateStatus
{
    Created = 1,

    /// <summary>No account with that id is visible to the caller's tenant and scope. A deal cannot be
    /// opened against an account the caller cannot see, and a cross-tenant account reference fails here.</summary>
    AccountNotFound = 2,
}

public sealed record DealCreateResult(DealCreateStatus Status, DealView? Deal);

public enum DealLineAddStatus
{
    Added = 1,

    /// <summary>No deal with that id is visible to the caller's tenant and scope.</summary>
    DealNotFound = 2,
}

public sealed record DealLineAddResult(DealLineAddStatus Status, DealView? Deal);

/// <summary>One page of the deals grid, plus the cursor that fetches the next page (null at the end).</summary>
public sealed record DealsPage(IReadOnlyList<DealView> Items, string? NextCursor);

/// <summary>
/// The Sales module's public surface for deals. The implementation is internal; the endpoint host reaches
/// it only through this interface (ARCHITECTURE.md §1). Every method takes the caller's resolved scopes
/// explicitly — the service never invents them. P4 exposes creation, single-deal read, add-line and the
/// grid; the lifecycle transitions are P5.
/// </summary>
public interface IDealService
{
    /// <summary>
    /// Opens a deal (in <c>new</c>) under the account named by <see cref="CreateDealRequest.AccountId"/>,
    /// which is validated to exist and to be within the caller's scope before anything is written. The
    /// deal inherits the account's tenant and five scope facts.
    /// </summary>
    Task<DealCreateResult> CreateAsync(
        DataScopeSet scopes,
        CreateDealRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>One deal by id, with its lines, if the caller's tenant and data scope admit it; else null.</summary>
    Task<DealView?> GetAsync(
        DataScopeSet scopes,
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a line to the deal named by <paramref name="dealId"/>, if the caller's scope admits it.</summary>
    Task<DealLineAddResult> AddLineAsync(
        DataScopeSet scopes,
        Guid dealId,
        AddDealLineRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The deals grid, scoped through the reader role and its row-security policy: keyset-paginated by
    /// <c>(created_at, id)</c>, returning only rows the caller's scope admits.
    /// </summary>
    Task<DealsPage> ListAsync(
        DataScopeSet scopes,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);
}
