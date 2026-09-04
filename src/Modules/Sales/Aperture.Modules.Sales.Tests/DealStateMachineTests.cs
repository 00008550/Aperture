using Aperture.Modules.Sales.Application;
using Aperture.Modules.Sales.Domain;
using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aperture.Modules.Sales.Tests;

/// <summary>
/// Plan 002-P5's deal state machine, by name, against a real PostgreSQL: the one table-driven definition
/// (<see cref="DealStateMachine"/>) enforces the linear lifecycle and its rule guards — every legal edge is
/// walkable, every illegal edge and every move out of a terminal state is rejected (edge 12), won needs a
/// priced line (edge 9, rule 1), quoted freezes the price-list version onto the lines (edge 10, rule 2),
/// lost needs a reason and persists it (edge 11, rule 4), and two writers racing the same deal produce one
/// commit and one conflict (edge 15). The audit-row half lives at the API layer (DealTransitionEndpointTests),
/// where the Access trail this writes into is composed alongside Sales.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DealStateMachineTests(PostgresFixture postgres)
{
    private DealService DealsFor(TenantId tenant, out SalesDbContext db)
    {
        db = postgres.CreateContext(tenant);
        var reader = NpgsqlDataSource.Create(postgres.ReaderConnectionString);
        // A threshold above the 5% discount these state-machine tests use, so won is never held for approval
        // here — the discount-approval path (rule 3) is exercised by DealDiscountApprovalTests.
        return new DealService(
            db,
            new ScopedConnection(reader, NullLogger<ScopedConnection>.Instance),
            new ConfiguredDiscountThresholdProvider(100m));
    }

    private AccountService AccountsFor(TenantId tenant)
    {
        var db = postgres.CreateContext(tenant);
        var reader = NpgsqlDataSource.Create(postgres.ReaderConnectionString);
        return new AccountService(db, new ScopedConnection(reader, NullLogger<ScopedConnection>.Instance));
    }

    /// <summary>An account, and a deal under it in <c>new</c>, owned by <paramref name="owner"/>. Returns the
    /// deal id and a Self scope set that admits it — the starting point for every transition test.</summary>
    private async Task<(Guid DealId, DataScopeSet Scopes)> NewDealAsync(
        TenantId tenant, UserId owner, string taxId)
    {
        var accountResult = await AccountsFor(tenant).CreateAsync(
            tenant, owner, new CreateAccountRequest($"Acme {taxId}", taxId, 1000m, 30, null, null));
        Assert.Equal(AccountCreateStatus.Created, accountResult.Status);

        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));
        var deals = DealsFor(tenant, out _);
        var created = await deals.CreateAsync(
            scopes, new CreateDealRequest(accountResult.Account!.Id, "state-machine deal", 5000m, 5m));
        Assert.Equal(DealCreateStatus.Created, created.Status);
        return (created.Deal!.Id, scopes);
    }

    private async Task<DealTransitionResponse> TransitionAsync(
        TenantId tenant, DataScopeSet scopes, Guid dealId, string to,
        string? reason = null, string? priceListVersion = null)
    {
        var deals = DealsFor(tenant, out _);
        return await deals.TransitionAsync(
            scopes, dealId, new TransitionDealRequest(to, reason, priceListVersion));
    }

    /// <summary>Walks the deal to <paramref name="target"/> along the legal pipeline, supplying the version
    /// quoted needs and (for lost) a reason, and asserting each step lands.</summary>
    private async Task DriveToAsync(
        TenantId tenant, DataScopeSet scopes, Guid dealId, string target,
        bool addPricedLine = true, string? lostReason = "budget")
    {
        if (addPricedLine)
        {
            var deals = DealsFor(tenant, out _);
            await deals.AddLineAsync(scopes, dealId, new AddDealLineRequest("SKU-1", 100m, 2, "v1"));
        }

        var path = new[]
        {
            Deal.Stages.Qualified, Deal.Stages.Quoted, Deal.Stages.Negotiation, target,
        };

        var current = Deal.Stages.New;
        foreach (var stage in path)
        {
            var reason = stage == Deal.Stages.Lost ? lostReason : null;
            var version = stage == Deal.Stages.Quoted ? "v1" : null;
            var result = await TransitionAsync(tenant, scopes, dealId, stage, reason, version);
            Assert.Equal(DealTransitionOutcome.Transitioned, result.Outcome);
            current = stage;
            if (stage == target)
            {
                break;
            }
        }

        Assert.Equal(target, current);
    }

    // ---- Every legal edge (the linear pipeline) -----------------------------------------------

    [Fact]
    public async Task Each_legal_edge_new_qualified_quoted_negotiation_won_is_walkable()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-WON");

        await DriveToAsync(tenant, scopes, dealId, Deal.Stages.Won);

        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal(Deal.Stages.Won, deal!.Stage);
    }

    [Fact]
    public async Task The_legal_edge_negotiation_lost_is_walkable_and_terminal()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-LOSTEDGE");

        await DriveToAsync(tenant, scopes, dealId, Deal.Stages.Lost, addPricedLine: false);

        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal(Deal.Stages.Lost, deal!.Stage);
    }

    // ---- Edge 9: won requires a priced line (rule 1) ------------------------------------------

    [Fact]
    public async Task Won_with_no_line_at_all_is_rejected_and_the_stage_does_not_change()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-WON-NOLINE");

        // Drive to negotiation WITHOUT adding a line.
        await DriveToAsync(tenant, scopes, dealId, Deal.Stages.Negotiation, addPricedLine: false);

        var result = await TransitionAsync(tenant, scopes, dealId, Deal.Stages.Won);

        Assert.Equal(DealTransitionOutcome.NoPricedLine, result.Outcome);
        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal(Deal.Stages.Negotiation, deal!.Stage);
    }

    [Fact]
    public async Task Won_with_a_line_missing_a_price_is_rejected()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-WON-NOPRICE");

        // A line priced at zero has a quantity but no price — rule 1 is not satisfied.
        await DealsFor(tenant, out _).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-FREE", 0m, 3, "v1"));
        await DriveToAsync(tenant, scopes, dealId, Deal.Stages.Negotiation, addPricedLine: false);

        var result = await TransitionAsync(tenant, scopes, dealId, Deal.Stages.Won);

        Assert.Equal(DealTransitionOutcome.NoPricedLine, result.Outcome);
    }

    // ---- Edge 10: quoted freezes the price-list version onto the lines (rule 2) ---------------

    [Fact]
    public async Task Quoted_freezes_the_price_list_version_onto_the_deal_and_its_lines()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-FREEZE");

        // A line originally priced against a different version — the quote must overwrite it with the frozen
        // one so the whole quote references a single snapshot.
        await DealsFor(tenant, out _).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-1", 100m, 2, "draft-0"));

        await TransitionAsync(tenant, scopes, dealId, Deal.Stages.Qualified);
        var quoted = await TransitionAsync(
            tenant, scopes, dealId, Deal.Stages.Quoted, priceListVersion: "v1");
        Assert.Equal(DealTransitionOutcome.Transitioned, quoted.Outcome);

        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal("v1", deal!.FrozenPriceListVersion);
        Assert.All(deal.Lines, l => Assert.Equal("v1", l.PriceListVersion));

        // A later price-list change (a new line added on a newer version after the freeze) does not touch the
        // frozen snapshot — the outstanding quote still references v1.
        await DealsFor(tenant, out _).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-2", 50m, 1, "v2"));
        var reread = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal("v1", reread!.FrozenPriceListVersion);
    }

    [Fact]
    public async Task Quoted_without_a_price_list_version_is_rejected()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-QUOTE-NOVER");

        await TransitionAsync(tenant, scopes, dealId, Deal.Stages.Qualified);
        var result = await TransitionAsync(tenant, scopes, dealId, Deal.Stages.Quoted);

        Assert.Equal(DealTransitionOutcome.PriceListVersionRequired, result.Outcome);
        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal(Deal.Stages.Qualified, deal!.Stage);
    }

    // ---- Edge 11: lost requires a reason, and persists it (rule 4) ----------------------------

    [Fact]
    public async Task Lost_without_a_reason_is_rejected_and_the_stage_does_not_change()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-LOST-NOREASON");

        await DriveToAsync(tenant, scopes, dealId, Deal.Stages.Negotiation, addPricedLine: false);

        var result = await TransitionAsync(tenant, scopes, dealId, Deal.Stages.Lost);

        Assert.Equal(DealTransitionOutcome.ReasonRequired, result.Outcome);
        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal(Deal.Stages.Negotiation, deal!.Stage);
        Assert.Null(deal.LostReasonCode);
    }

    [Fact]
    public async Task Lost_with_a_reason_is_accepted_terminal_and_the_reason_is_persisted()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-LOST-REASON");

        await DriveToAsync(tenant, scopes, dealId, Deal.Stages.Negotiation, addPricedLine: false);
        var lost = await TransitionAsync(
            tenant, scopes, dealId, Deal.Stages.Lost, reason: "competitor-cheaper");
        Assert.Equal(DealTransitionOutcome.Transitioned, lost.Outcome);

        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal(Deal.Stages.Lost, deal!.Stage);
        Assert.Equal("competitor-cheaper", deal.LostReasonCode);

        // Terminal: no edge leaves lost.
        var after = await TransitionAsync(tenant, scopes, dealId, Deal.Stages.Won);
        Assert.Equal(DealTransitionOutcome.IllegalTransition, after.Outcome);
    }

    // ---- Edge 12: illegal transitions and terminal states are rejected ------------------------

    [Fact]
    public async Task A_non_adjacent_jump_new_to_won_is_rejected_as_an_illegal_edge()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-JUMP");

        var result = await TransitionAsync(tenant, scopes, dealId, Deal.Stages.Won);

        Assert.Equal(DealTransitionOutcome.IllegalTransition, result.Outcome);
        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal(Deal.Stages.New, deal!.Stage);
    }

    [Fact]
    public async Task An_unknown_target_stage_is_rejected_as_an_illegal_edge()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-UNKNOWN");

        var result = await TransitionAsync(tenant, scopes, dealId, "archived");

        Assert.Equal(DealTransitionOutcome.IllegalTransition, result.Outcome);
    }

    [Fact]
    public async Task Any_transition_out_of_the_terminal_won_state_is_rejected()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-WONTERM");

        await DriveToAsync(tenant, scopes, dealId, Deal.Stages.Won);

        Assert.Equal(
            DealTransitionOutcome.IllegalTransition,
            (await TransitionAsync(tenant, scopes, dealId, Deal.Stages.Lost, reason: "x")).Outcome);
        Assert.Equal(
            DealTransitionOutcome.IllegalTransition,
            (await TransitionAsync(tenant, scopes, dealId, Deal.Stages.Negotiation)).Outcome);
    }

    [Fact]
    public void The_state_machine_defines_only_the_linear_pipeline_edges()
    {
        // The definition itself, independent of any deal: exactly the DOMAIN.md §2 edges are legal.
        Assert.True(DealStateMachine.IsLegal(Deal.Stages.New, Deal.Stages.Qualified));
        Assert.True(DealStateMachine.IsLegal(Deal.Stages.Qualified, Deal.Stages.Quoted));
        Assert.True(DealStateMachine.IsLegal(Deal.Stages.Quoted, Deal.Stages.Negotiation));
        Assert.True(DealStateMachine.IsLegal(Deal.Stages.Negotiation, Deal.Stages.Won));
        Assert.True(DealStateMachine.IsLegal(Deal.Stages.Negotiation, Deal.Stages.Lost));

        // A sample of the illegal ones, including both terminals as sources.
        Assert.False(DealStateMachine.IsLegal(Deal.Stages.New, Deal.Stages.Won));
        Assert.False(DealStateMachine.IsLegal(Deal.Stages.Qualified, Deal.Stages.Negotiation));
        Assert.False(DealStateMachine.IsLegal(Deal.Stages.Won, Deal.Stages.Lost));
        Assert.False(DealStateMachine.IsLegal(Deal.Stages.Lost, Deal.Stages.Won));
        Assert.False(DealStateMachine.IsLegal(Deal.Stages.New, "archived"));
    }

    // ---- Edge 15: concurrent transition — one commits, the other 409s -------------------------

    [Fact]
    public async Task Two_writers_transitioning_the_same_deal_produce_one_commit_and_one_conflict()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-RACE");

        // Both writers read the deal at the same version. (Mirrors the account xmin test: two callers hold
        // the same version they last read; the second's is stale once the first commits.)
        var version = (await DealsFor(tenant, out _).GetAsync(scopes, dealId))!.Version;

        var first = DealsFor(tenant, out _);
        var second = DealsFor(tenant, out _);
        var request = new TransitionDealRequest(Deal.Stages.Qualified, ExpectedVersion: version);

        var firstResult = await first.TransitionAsync(scopes, dealId, request);
        var secondResult = await second.TransitionAsync(scopes, dealId, request);

        Assert.Equal(DealTransitionOutcome.Transitioned, firstResult.Outcome);
        Assert.Equal(DealTransitionOutcome.Conflict, secondResult.Outcome);
        // The conflict carries the current state so the caller can re-apply against it.
        Assert.NotNull(secondResult.Deal);
        Assert.Equal(Deal.Stages.Qualified, secondResult.Deal!.Stage);
    }

    [Fact]
    public async Task The_xmin_token_catches_a_race_within_the_load_to_commit_window()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-XMIN");

        // No ExpectedVersion, so the early check is skipped: this exercises the DB-level xmin token, the
        // backstop for the window between a service's load and its commit. Two contexts both load the deal in
        // `new`, both mutate it in memory, then both save. EF's concurrency token lets exactly one land.
        using var dbA = postgres.CreateContext(tenant);
        using var dbB = postgres.CreateContext(tenant);

        var dealA = await dbA.Deals.Include(d => d.Lines)
            .SingleAsync(d => d.Id == dealId);
        var dealB = await dbB.Deals.Include(d => d.Lines)
            .SingleAsync(d => d.Id == dealId);

        Assert.True(dealA.Transition(Deal.Stages.Qualified, new DealTransitionInput()).Succeeded);
        Assert.True(dealB.Transition(Deal.Stages.Qualified, new DealTransitionInput()).Succeeded);

        await dbA.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task A_stale_expected_version_is_rejected_as_a_conflict_before_any_change()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, owner, "TX-P5-STALE");

        var deals = DealsFor(tenant, out _);
        var result = await deals.TransitionAsync(
            scopes, dealId, new TransitionDealRequest(Deal.Stages.Qualified, ExpectedVersion: 999999u));

        Assert.Equal(DealTransitionOutcome.Conflict, result.Outcome);
        var deal = await DealsFor(tenant, out _).GetAsync(scopes, dealId);
        Assert.Equal(Deal.Stages.New, deal!.Stage);
    }

    // ---- Scope: a deal the caller cannot see cannot be moved ----------------------------------

    [Fact]
    public async Task Transitioning_a_deal_outside_the_callers_scope_is_reported_as_not_found()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, _) = await NewDealAsync(tenant, owner, "TX-P5-SCOPE");

        var stranger = DataScopeSet.Of(tenant, new DataScope.Self(UserId.New()));
        var result = await TransitionAsync(tenant, stranger, dealId, Deal.Stages.Qualified);

        Assert.Equal(DealTransitionOutcome.DealNotFound, result.Outcome);
    }

    [Fact]
    public async Task An_empty_scope_set_cannot_transition_a_deal()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var (dealId, _) = await NewDealAsync(tenant, owner, "TX-P5-EMPTY");

        var result = await TransitionAsync(tenant, DataScopeSet.None(tenant), dealId, Deal.Stages.Qualified);

        Assert.Equal(DealTransitionOutcome.DealNotFound, result.Outcome);
    }
}
