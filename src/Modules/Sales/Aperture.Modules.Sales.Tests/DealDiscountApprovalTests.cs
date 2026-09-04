using Aperture.Modules.Sales.Application;
using Aperture.Modules.Sales.Domain;
using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aperture.Modules.Sales.Tests;

/// <summary>
/// Plan 002-P6's discount-approval rule and state logic (DOMAIN.md §2 rule 3), by name, against a real
/// PostgreSQL: edge 13 — a discount above the tenant threshold cannot advance to won on the agent's own
/// authority; the move holds in <c>negotiation</c> with a pending approval recorded, and re-attempting it
/// still holds (the agent alone cannot clear it). Edge 14 — once a lead clears the approval the deal may
/// advance; below-threshold discounts never hold; approving a deal with nothing pending, out of scope, or
/// under an empty scope set is denied fail-closed. The permission boundary itself (who may approve → 403)
/// lives at the API layer, where the policy is enforced (DealDiscountApprovalEndpointTests).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DealDiscountApprovalTests(PostgresFixture postgres)
{
    // The threshold every test in this file is written against: a discount over 20% needs a lead's approval.
    private const decimal ThresholdPct = 20m;

    private DealService DealsFor(TenantId tenant, out SalesDbContext db, decimal thresholdPct = ThresholdPct)
    {
        db = postgres.CreateContext(tenant);
        var reader = NpgsqlDataSource.Create(postgres.ReaderConnectionString);
        return new DealService(
            db,
            new ScopedConnection(reader, NullLogger<ScopedConnection>.Instance),
            new ConfiguredDiscountThresholdProvider(thresholdPct));
    }

    private AccountService AccountsFor(TenantId tenant)
    {
        var db = postgres.CreateContext(tenant);
        var reader = NpgsqlDataSource.Create(postgres.ReaderConnectionString);
        return new AccountService(db, new ScopedConnection(reader, NullLogger<ScopedConnection>.Instance));
    }

    /// <summary>An account, plus a deal under it in <c>negotiation</c> at <paramref name="discountPct"/> with
    /// one priced line — the starting point where rule 3 bites (the last edge before won).</summary>
    private async Task<(Guid DealId, DataScopeSet Scopes)> DealInNegotiationAsync(
        TenantId tenant, UserId owner, string taxId, decimal discountPct)
    {
        var accountResult = await AccountsFor(tenant).CreateAsync(
            tenant, owner, new CreateAccountRequest($"Acme {taxId}", taxId, 1000m, 30, null, null));
        Assert.Equal(AccountCreateStatus.Created, accountResult.Status);

        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));
        var deals = DealsFor(tenant, out _);
        var created = await deals.CreateAsync(
            scopes, new CreateDealRequest(accountResult.Account!.Id, "discount deal", 5000m, discountPct));
        Assert.Equal(DealCreateStatus.Created, created.Status);
        var dealId = created.Deal!.Id;

        await DealsFor(tenant, out _).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-1", 100m, 2, "v1"));

        foreach (var stage in new[] { Deal.Stages.Qualified, Deal.Stages.Quoted, Deal.Stages.Negotiation })
        {
            var version = stage == Deal.Stages.Quoted ? "v1" : null;
            var step = await DealsFor(tenant, out _).TransitionAsync(
                scopes, dealId, new TransitionDealRequest(stage, PriceListVersion: version));
            Assert.Equal(DealTransitionOutcome.Transitioned, step.Outcome);
        }

        return (dealId, scopes);
    }

    // ---- Edge 13: over-threshold discount holds in negotiation ---------------------------------

    [Fact]
    public async Task Won_with_a_discount_over_threshold_holds_in_negotiation_with_a_pending_approval()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await DealInNegotiationAsync(tenant, owner, "TX-P6-HOLD", discountPct: 30m);

        var result = await DealsFor(tenant, out _).TransitionAsync(
            scopes, dealId, new TransitionDealRequest(Deal.Stages.Won));

        Assert.Equal(DealTransitionOutcome.PendingApproval, result.Outcome);

        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal(Deal.Stages.Negotiation, deal!.Stage);
        Assert.True(deal.PendingApproval);
    }

    [Fact]
    public async Task The_agent_re_attempting_the_move_still_holds_it_cannot_clear_its_own_approval()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await DealInNegotiationAsync(tenant, owner, "TX-P6-AGENT", discountPct: 30m);

        // First attempt holds.
        Assert.Equal(
            DealTransitionOutcome.PendingApproval,
            (await DealsFor(tenant, out _).TransitionAsync(
                scopes, dealId, new TransitionDealRequest(Deal.Stages.Won))).Outcome);

        // The agent tries again — there is no path through TransitionAsync that clears the pending approval;
        // it still holds. Only ApproveDiscount (gated by deals.discount.approve at the endpoint) clears it.
        var again = await DealsFor(tenant, out _).TransitionAsync(
            scopes, dealId, new TransitionDealRequest(Deal.Stages.Won));

        Assert.Equal(DealTransitionOutcome.PendingApproval, again.Outcome);
        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal(Deal.Stages.Negotiation, deal!.Stage);
        Assert.True(deal.PendingApproval);
    }

    [Fact]
    public async Task A_discount_at_or_below_threshold_advances_to_won_without_any_approval()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        // Exactly at the threshold is NOT above it — no approval required.
        var (dealId, scopes) = await DealInNegotiationAsync(tenant, owner, "TX-P6-UNDER", discountPct: 20m);

        var result = await DealsFor(tenant, out _).TransitionAsync(
            scopes, dealId, new TransitionDealRequest(Deal.Stages.Won));

        Assert.Equal(DealTransitionOutcome.Transitioned, result.Outcome);
        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal(Deal.Stages.Won, deal!.Stage);
        Assert.False(deal.PendingApproval);
    }

    // ---- Edge 14: a lead clears the approval and the deal advances -----------------------------

    [Fact]
    public async Task Approving_the_discount_clears_the_hold_and_the_deal_may_then_be_won()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await DealInNegotiationAsync(tenant, owner, "TX-P6-APPROVE", discountPct: 30m);

        Assert.Equal(
            DealTransitionOutcome.PendingApproval,
            (await DealsFor(tenant, out _).TransitionAsync(
                scopes, dealId, new TransitionDealRequest(Deal.Stages.Won))).Outcome);

        var approval = await DealsFor(tenant, out _).ApproveDiscountAsync(
            scopes, dealId, new ApproveDiscountRequest("lead signed off"));

        Assert.Equal(DealDiscountApprovalOutcome.Approved, approval.Outcome);
        Assert.False(approval.Deal!.PendingApproval);

        // Now the same over-threshold move succeeds, because the approval is on record.
        var won = await DealsFor(tenant, out _).TransitionAsync(
            scopes, dealId, new TransitionDealRequest(Deal.Stages.Won));
        Assert.Equal(DealTransitionOutcome.Transitioned, won.Outcome);
        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal(Deal.Stages.Won, deal!.Stage);
    }

    [Fact]
    public async Task Approving_a_deal_with_nothing_pending_is_reported_as_not_pending()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        // Below threshold, so no approval was ever raised.
        var (dealId, scopes) = await DealInNegotiationAsync(tenant, owner, "TX-P6-NOTPEND", discountPct: 5m);

        var approval = await DealsFor(tenant, out _).ApproveDiscountAsync(
            scopes, dealId, new ApproveDiscountRequest("nothing to sign"));

        Assert.Equal(DealDiscountApprovalOutcome.NotPending, approval.Outcome);
    }

    // ---- Fail-closed: the underlying read still denies -----------------------------------------

    [Fact]
    public async Task Approving_a_deal_outside_the_callers_scope_is_reported_as_not_found()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, _) = await DealInNegotiationAsync(tenant, owner, "TX-P6-SCOPE", discountPct: 30m);

        var stranger = DataScopeSet.Of(tenant, new DataScope.Self(UserId.New()));
        var approval = await DealsFor(tenant, out _).ApproveDiscountAsync(
            stranger, dealId, new ApproveDiscountRequest("intruder"));

        Assert.Equal(DealDiscountApprovalOutcome.DealNotFound, approval.Outcome);
    }

    [Fact]
    public async Task An_empty_scope_set_cannot_approve_a_discount()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, _) = await DealInNegotiationAsync(tenant, owner, "TX-P6-EMPTY", discountPct: 30m);

        var approval = await DealsFor(tenant, out _).ApproveDiscountAsync(
            DataScopeSet.None(tenant), dealId, new ApproveDiscountRequest("no grants"));

        Assert.Equal(DealDiscountApprovalOutcome.DealNotFound, approval.Outcome);
    }

    [Fact]
    public async Task A_stale_expected_version_on_approval_is_rejected_as_a_conflict()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await DealInNegotiationAsync(tenant, owner, "TX-P6-STALE", discountPct: 30m);

        Assert.Equal(
            DealTransitionOutcome.PendingApproval,
            (await DealsFor(tenant, out _).TransitionAsync(
                scopes, dealId, new TransitionDealRequest(Deal.Stages.Won))).Outcome);

        var approval = await DealsFor(tenant, out _).ApproveDiscountAsync(
            scopes, dealId, new ApproveDiscountRequest("lead", ExpectedVersion: 999999u));

        Assert.Equal(DealDiscountApprovalOutcome.Conflict, approval.Outcome);
        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.True(deal!.PendingApproval);
    }
}
