using Aperture.Modules.Sales.Application;
using Aperture.Modules.Sales.Domain;
using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aperture.Modules.Sales.Tests;

/// <summary>
/// Plan 011-P3, edges 12–16, against a real PostgreSQL: a closed deal takes no new line, a line added after
/// the quote keeps the frozen price-list version (DOMAIN.md §2 rule 2), and add-line participates in the
/// deal's <c>xmin</c> — it moves the version, loses to a writer that committed first, and refuses a stale
/// <see cref="AddDealLineRequest.ExpectedVersion"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DealLineIntegrityTests(PostgresFixture postgres)
{
    private DealService DealsFor(TenantId tenant) => DealsOver(postgres.CreateContext(tenant));

    private DealService DealsOver(SalesDbContext db)
    {
        var reader = NpgsqlDataSource.Create(postgres.ReaderConnectionString);
        return new DealService(
            db,
            new ScopedConnection(reader, NullLogger<ScopedConnection>.Instance),
            new ConfiguredDiscountThresholdProvider(100m));
    }

    private async Task<(Guid DealId, DataScopeSet Scopes)> NewDealAsync(TenantId tenant, string taxId)
    {
        var owner = UserId.New();
        var reader = NpgsqlDataSource.Create(postgres.ReaderConnectionString);
        var accounts = new AccountService(
            postgres.CreateContext(tenant), new ScopedConnection(reader, NullLogger<ScopedConnection>.Instance));
        var account = await accounts.CreateAsync(
            tenant, owner, new CreateAccountRequest($"Acme {taxId}", taxId, 1000m, 30, null, null));
        Assert.Equal(AccountCreateStatus.Created, account.Status);

        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));
        var created = await DealsFor(tenant).CreateAsync(
            scopes, new CreateDealRequest(account.Account!.Id, "integrity deal", 5000m, 5m));
        Assert.Equal(DealCreateStatus.Created, created.Status);
        return (created.Deal!.Id, scopes);
    }

    private async Task MoveAsync(
        TenantId tenant, DataScopeSet scopes, Guid dealId, string to, string? reason = null, string? version = null)
    {
        var result = await DealsFor(tenant).TransitionAsync(
            scopes, dealId, new TransitionDealRequest(to, reason, version));
        Assert.Equal(DealTransitionOutcome.Transitioned, result.Outcome);
    }

    /// <summary>A deal with one priced line, walked to <c>quoted</c> at <c>v1</c>.</summary>
    private async Task<(Guid DealId, DataScopeSet Scopes)> QuotedDealAsync(TenantId tenant, string taxId)
    {
        var (dealId, scopes) = await NewDealAsync(tenant, taxId);
        var added = await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-1", 100m, 2, "v1"));
        Assert.Equal(DealLineAddStatus.Added, added.Status);
        await MoveAsync(tenant, scopes, dealId, Deal.Stages.Qualified);
        await MoveAsync(tenant, scopes, dealId, Deal.Stages.Quoted, version: "v1");
        return (dealId, scopes);
    }

    // ---- Edge 12: add-line on a terminal deal -------------------------------------------------

    [Theory]
    [InlineData(Deal.Stages.Won)]
    [InlineData(Deal.Stages.Lost)]
    public async Task Add_line_on_a_terminal_deal_is_rejected_and_the_line_count_is_unchanged(string terminal)
    {
        var tenant = TenantId.New();
        var (dealId, scopes) = await QuotedDealAsync(tenant, $"TX-011P3-CLOSED-{terminal}");
        await MoveAsync(tenant, scopes, dealId, Deal.Stages.Negotiation);
        await MoveAsync(tenant, scopes, dealId, terminal, reason: terminal == Deal.Stages.Lost ? "budget" : null);

        var result = await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-LATE", 10m, 1, null));

        Assert.Equal(DealLineAddStatus.DealClosed, result.Status);
        Assert.Null(result.Deal);
        var deal = await DealsFor(tenant).GetAsync(scopes, dealId);
        Assert.Single(deal!.Lines);
    }

    [Fact]
    public async Task Invalid_line_input_on_a_terminal_deal_is_still_a_validation_error()
    {
        // Precedence: input validity (400) before the stage refusal (422).
        var tenant = TenantId.New();
        var (dealId, scopes) = await QuotedDealAsync(tenant, "TX-011P3-CLOSED-INVALID");
        await MoveAsync(tenant, scopes, dealId, Deal.Stages.Negotiation);
        await MoveAsync(tenant, scopes, dealId, Deal.Stages.Lost, reason: "budget");

        await Assert.ThrowsAsync<Aperture.SharedKernel.Domain.DomainValidationException>(() =>
            DealsFor(tenant).AddLineAsync(scopes, dealId, new AddDealLineRequest(" ", 10m, 1, null)));
    }

    // ---- Edge 13: add-line after quote keeps the freeze ---------------------------------------

    [Fact]
    public async Task Add_line_after_quote_with_no_version_takes_the_frozen_version()
    {
        var tenant = TenantId.New();
        var (dealId, scopes) = await QuotedDealAsync(tenant, "TX-011P3-FREEZE-NONE");

        var result = await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-2", 50m, 1, null));

        Assert.Equal(DealLineAddStatus.Added, result.Status);
        var deal = await DealsFor(tenant).GetAsync(scopes, dealId);
        Assert.Equal(2, deal!.Lines.Count);
        Assert.All(deal.Lines, l => Assert.Equal("v1", l.PriceListVersion));
    }

    [Fact]
    public async Task Add_line_after_quote_with_a_different_version_is_rejected_and_adds_no_line()
    {
        var tenant = TenantId.New();
        var (dealId, scopes) = await QuotedDealAsync(tenant, "TX-011P3-FREEZE-V2");

        var result = await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-2", 50m, 1, "v2"));

        Assert.Equal(DealLineAddStatus.PriceListVersionMismatch, result.Status);
        var deal = await DealsFor(tenant).GetAsync(scopes, dealId);
        Assert.Single(deal!.Lines);
        Assert.Equal("v1", deal.FrozenPriceListVersion);
    }

    [Fact]
    public async Task Add_line_after_quote_with_the_frozen_version_is_accepted()
    {
        var tenant = TenantId.New();
        var (dealId, scopes) = await QuotedDealAsync(tenant, "TX-011P3-FREEZE-V1");

        var result = await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-2", 50m, 1, " v1 "));

        Assert.Equal(DealLineAddStatus.Added, result.Status);
        Assert.Equal(2, result.Deal!.Lines.Count);
    }

    [Fact]
    public async Task Add_line_in_negotiation_still_keeps_the_freeze()
    {
        var tenant = TenantId.New();
        var (dealId, scopes) = await QuotedDealAsync(tenant, "TX-011P3-FREEZE-NEG");
        await MoveAsync(tenant, scopes, dealId, Deal.Stages.Negotiation);

        var mismatch = await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-2", 50m, 1, "v2"));
        var stamped = await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-3", 50m, 1, null));

        Assert.Equal(DealLineAddStatus.PriceListVersionMismatch, mismatch.Status);
        Assert.Equal(DealLineAddStatus.Added, stamped.Status);
        Assert.All(stamped.Deal!.Lines, l => Assert.Equal("v1", l.PriceListVersion));
    }

    [Fact]
    public async Task Add_line_before_quote_keeps_the_callers_version()
    {
        var tenant = TenantId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, "TX-011P3-PREQUOTE");

        var result = await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-1", 10m, 1, "draft-7"));

        Assert.Equal(DealLineAddStatus.Added, result.Status);
        Assert.Equal("draft-7", Assert.Single(result.Deal!.Lines).PriceListVersion);
    }

    // ---- Edge 14: add-line bumps the version --------------------------------------------------

    [Fact]
    public async Task Add_line_bumps_the_deal_version_so_a_transition_at_the_old_version_conflicts()
    {
        var tenant = TenantId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, "TX-011P3-BUMP");
        var before = (await DealsFor(tenant).GetAsync(scopes, dealId))!.Version;

        var added = await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-1", 10m, 1, null));

        Assert.Equal(DealLineAddStatus.Added, added.Status);
        Assert.NotEqual(before, added.Deal!.Version);
        var persisted = (await DealsFor(tenant).GetAsync(scopes, dealId))!.Version;
        Assert.Equal(added.Deal.Version, persisted);

        var transition = await DealsFor(tenant).TransitionAsync(
            scopes, dealId, new TransitionDealRequest(Deal.Stages.Qualified, ExpectedVersion: before));
        Assert.Equal(DealTransitionOutcome.Conflict, transition.Outcome);
    }

    [Fact]
    public async Task Add_line_with_the_current_expected_version_is_accepted()
    {
        var tenant = TenantId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, "TX-011P3-CURRENT");
        var version = (await DealsFor(tenant).GetAsync(scopes, dealId))!.Version;

        var result = await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-1", 10m, 1, null, version));

        Assert.Equal(DealLineAddStatus.Added, result.Status);
    }

    // ---- Edge 15: add-line ∥ quote ------------------------------------------------------------

    [Fact]
    public async Task Add_line_racing_a_quote_conflicts_with_the_quoted_deal_and_leaves_no_unfrozen_line()
    {
        var tenant = TenantId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, "TX-011P3-RACE");
        Assert.Equal(DealLineAddStatus.Added, (await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-1", 100m, 2, "v1"))).Status);
        await MoveAsync(tenant, scopes, dealId, Deal.Stages.Qualified);

        // The add-line service has loaded the deal (version N, not yet quoted) and is about to commit; in that
        // window a second writer commits →quoted. No expectedVersion is sent, so only the xmin predicate on
        // the forced header UPDATE can catch it.
        var competitor = new CompetingWriter(async () =>
            await MoveAsync(tenant, scopes, dealId, Deal.Stages.Quoted, version: "v1"));
        var raced = DealsOver(postgres.CreateContext(tenant, competitor));

        var result = await raced.AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-2", 50m, 1, null));

        Assert.True(competitor.Fired);
        Assert.Equal(DealLineAddStatus.Conflict, result.Status);
        Assert.Equal(Deal.Stages.Quoted, result.Deal!.Stage);
        var deal = await DealsFor(tenant).GetAsync(scopes, dealId);
        Assert.Single(deal!.Lines);
        Assert.All(deal.Lines, l => Assert.Equal("v1", l.PriceListVersion));
    }

    [Fact]
    public async Task Two_concurrent_add_lines_produce_one_line_and_one_conflict()
    {
        var tenant = TenantId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, "TX-011P3-ADDADD");

        var competitor = new CompetingWriter(async () =>
            Assert.Equal(DealLineAddStatus.Added, (await DealsFor(tenant).AddLineAsync(
                scopes, dealId, new AddDealLineRequest("SKU-A", 10m, 1, null))).Status));
        var raced = DealsOver(postgres.CreateContext(tenant, competitor));

        var result = await raced.AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-B", 10m, 1, null));

        Assert.Equal(DealLineAddStatus.Conflict, result.Status);
        var deal = await DealsFor(tenant).GetAsync(scopes, dealId);
        Assert.Equal("SKU-A", Assert.Single(deal!.Lines).ProductRef);
    }

    // ---- Edge 16: stale add-line --------------------------------------------------------------

    [Fact]
    public async Task Stale_add_line_is_a_conflict_with_the_current_deal_and_adds_no_line()
    {
        var tenant = TenantId.New();
        var (dealId, scopes) = await NewDealAsync(tenant, "TX-011P3-STALE");
        var current = (await DealsFor(tenant).GetAsync(scopes, dealId))!;

        var result = await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-1", 10m, 1, null, current.Version + 1));

        Assert.Equal(DealLineAddStatus.Conflict, result.Status);
        Assert.Equal(current.Version, result.Deal!.Version);
        Assert.Empty((await DealsFor(tenant).GetAsync(scopes, dealId))!.Lines);
    }

    [Fact]
    public async Task Add_line_outside_the_callers_scope_is_not_found_even_with_a_stale_version()
    {
        var tenant = TenantId.New();
        var (dealId, _) = await NewDealAsync(tenant, "TX-011P3-SCOPE");
        var stranger = DataScopeSet.Of(tenant, new DataScope.Self(UserId.New()));

        var result = await DealsFor(tenant).AddLineAsync(
            stranger, dealId, new AddDealLineRequest("SKU-1", 10m, 1, null, 1u));

        Assert.Equal(DealLineAddStatus.DealNotFound, result.Status);
        Assert.Null(result.Deal);
    }

    /// <summary>Runs a competing write once, just before the intercepted context saves — landing it in the
    /// window between the service's load and its commit.</summary>
    private sealed class CompetingWriter(Func<Task> write) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                await write();
            }

            return result;
        }
    }
}
