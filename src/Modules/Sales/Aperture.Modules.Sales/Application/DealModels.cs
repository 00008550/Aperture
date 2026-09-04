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
/// What a caller supplies to move a deal along its lifecycle: the target stage, an optional reason (required
/// only for <c>lost</c>, rule 4) and an optional price-list version (required only for <c>quoted</c>,
/// rule 2). <see cref="ExpectedVersion"/> is the deal's <c>xmin</c> as the caller last read it; when
/// supplied, a stale value is rejected up front, and the EF concurrency token then guards the window to
/// commit — either way a concurrent transition loses rather than clobbers (edge 15). None of the five scope
/// columns appears here: a transition never changes who owns a deal.
/// </summary>
public sealed record TransitionDealRequest(
    string TargetStage,
    string? Reason = null,
    string? PriceListVersion = null,
    uint? ExpectedVersion = null);

public enum DealTransitionOutcome
{
    /// <summary>The deal advanced and was saved.</summary>
    Transitioned = 1,

    /// <summary>No deal with that id is visible to the caller's tenant and scope.</summary>
    DealNotFound = 2,

    /// <summary>The deal moved on between the caller's read and this write (or a stale
    /// <see cref="TransitionDealRequest.ExpectedVersion"/> was supplied); the returned
    /// <see cref="DealTransitionResponse.Deal"/> carries the current state.</summary>
    Conflict = 3,

    /// <summary>The requested edge does not exist — an unknown stage, a non-adjacent jump, or a move out of a
    /// terminal state (edge 12).</summary>
    IllegalTransition = 4,

    /// <summary>Rule 1: <c>won</c> requested with no priced line.</summary>
    NoPricedLine = 5,

    /// <summary>Rule 4: <c>lost</c> requested with no reason code.</summary>
    ReasonRequired = 6,

    /// <summary>Rule 2: <c>quoted</c> requested with no price-list version to freeze.</summary>
    PriceListVersionRequired = 7,

    /// <summary>Rule 3: the move to <c>won</c> carried a discount above the tenant threshold and no approval
    /// on record. The deal did NOT advance — it stays in <c>negotiation</c> with a pending approval recorded
    /// (<see cref="DealTransitionResponse.Deal"/> carries <see cref="DealView.PendingApproval"/> set). A lead
    /// with <c>deals.discount.approve</c> must clear it before a retry can win.</summary>
    PendingApproval = 8,
}

/// <summary>
/// The outcome of a transition. On success the <see cref="Deal"/> is the advanced deal; on a conflict it is
/// the current persisted state the caller must re-apply against; on the other rejections it is null. The
/// stages moved between are always present so the endpoint can audit the attempt.
/// </summary>
public sealed record DealTransitionResponse(
    DealTransitionOutcome Outcome,
    DealView? Deal,
    string FromStage,
    string ToStage);

/// <summary>
/// What a lead supplies to clear a deal's pending discount approval (DOMAIN.md §2 rule 3): the
/// <see cref="Reason"/> is the <em>why</em> the endpoint audits alongside the approver's identity, and an
/// optional <see cref="ExpectedVersion"/> guards against approving a deal that has moved on since the lead
/// read it (the same <c>xmin</c> optimistic check the transition path uses).
/// </summary>
public sealed record ApproveDiscountRequest(string Reason, uint? ExpectedVersion = null);

public enum DealDiscountApprovalOutcome
{
    /// <summary>The pending approval was cleared; the deal may now advance to <c>won</c>.</summary>
    Approved = 1,

    /// <summary>No deal with that id is visible to the caller's tenant and scope (fail-closed: an empty scope
    /// set or an out-of-scope deal is indistinguishable from a missing one).</summary>
    DealNotFound = 2,

    /// <summary>The deal has no discount approval outstanding — there is nothing to clear.</summary>
    NotPending = 3,

    /// <summary>The deal moved on between the lead's read and this write (a stale
    /// <see cref="ApproveDiscountRequest.ExpectedVersion"/> or a concurrent <c>xmin</c> loss); the returned
    /// <see cref="DealDiscountApprovalResult.Deal"/> carries the current state.</summary>
    Conflict = 4,
}

/// <summary>The outcome of clearing a pending discount approval, with the deal's current state on success or
/// conflict (null when the deal was not found or was not pending returns its unchanged view).</summary>
public sealed record DealDiscountApprovalResult(DealDiscountApprovalOutcome Outcome, DealView? Deal);

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

    /// <summary>
    /// Moves the deal named by <paramref name="dealId"/> along its lifecycle through the one table-driven
    /// <see cref="Domain.DealStateMachine"/>: an illegal edge, a terminal state, or a failed rule guard is a
    /// domain outcome on the returned <see cref="DealTransitionResponse"/>, and a concurrent transition
    /// (<c>xmin</c>) is reported as <see cref="DealTransitionOutcome.Conflict"/> with the current state. The
    /// deal is loaded and saved through the caller's scope, so a deal the caller cannot see cannot be moved.
    /// </summary>
    Task<DealTransitionResponse> TransitionAsync(
        DataScopeSet scopes,
        Guid dealId,
        TransitionDealRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the pending discount approval on the deal named by <paramref name="dealId"/> (DOMAIN.md §2
    /// rule 3), if the caller's tenant and scope admit it and the deal actually has an approval outstanding.
    /// Authorization for <em>who may approve</em> is enforced above this by the <c>deals.discount.approve</c>
    /// policy on the endpoint; this method still loads the deal through the caller's scope, so a deal the
    /// caller cannot see cannot be approved (an empty scope set denies). The approver's identity and reason
    /// are audited by the composition root, not stored on the deal.
    /// </summary>
    Task<DealDiscountApprovalResult> ApproveDiscountAsync(
        DataScopeSet scopes,
        Guid dealId,
        ApproveDiscountRequest request,
        CancellationToken cancellationToken = default);
}
