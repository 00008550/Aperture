using Aperture.Modules.Sales.Application;
using Aperture.Modules.Sales.Domain;
using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Multitenancy;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aperture.Modules.Sales.Tests;

/// <summary>
/// Plan 002-P4's deal test list, by name, against a real PostgreSQL: creation (a deal requires a valid,
/// in-scope account; a cross-tenant/out-of-scope account fails closed), add-line (the aggregate owns its
/// lines), and scope inheritance — a deal is visible under exactly the grants that see its parent account —
/// proven on <b>both</b> the EF read (<see cref="ScopeQuerying.WhereInScope{T}"/>) and the reader-role grid
/// (<see cref="ScopedConnection"/> + RLS), which must agree, plus tenant isolation and empty-scope deny.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DealServiceTests(PostgresFixture postgres)
{
    static DealServiceTests() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private DealService DealsFor(TenantId tenant, out SalesDbContext db)
    {
        db = postgres.CreateContext(tenant);
        var reader = NpgsqlDataSource.Create(postgres.ReaderConnectionString);
        // A threshold above any discount these tests use (they never touch the rule-3 path): no create/read/
        // add-line test should trip discount approval.
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

    private async Task<AccountView> NewAccountAsync(
        TenantId tenant, UserId owner, string taxId, Guid? region = null, Guid? team = null)
    {
        var result = await AccountsFor(tenant).CreateAsync(
            tenant, owner, new CreateAccountRequest($"Acme {taxId}", taxId, 1000m, 30, region, team));
        Assert.Equal(AccountCreateStatus.Created, result.Status);
        return result.Account!;
    }

    private static CreateDealRequest DealFor(Guid accountId, string name) =>
        new(accountId, name, Amount: 5000m, DiscountPct: 5m);

    private static async Task<IReadOnlyList<Guid>> AllGridIdsAsync(DealService service, DataScopeSet scopes)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await service.ListAsync(scopes, limit: 100, cursor);
            ids.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return ids;
    }

    // ---- Create + inheritance ------------------------------------------------------------------

    [Fact]
    public async Task Create_opens_in_new_and_inherits_tenant_owner_team_region_and_account()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var team = Guid.NewGuid();
        var region = Guid.NewGuid();
        var account = await NewAccountAsync(tenant, owner, "TX-D-INHERIT", region, team);

        var deals = DealsFor(tenant, out _);
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));

        var result = await deals.CreateAsync(scopes, DealFor(account.Id, "Big deal"));

        Assert.Equal(DealCreateStatus.Created, result.Status);
        var deal = result.Deal!;
        Assert.Equal(Deal.Stages.New, deal.Stage);
        Assert.Equal(tenant.Value, deal.TenantId);
        Assert.Equal(account.Id, deal.AccountId);
        Assert.Equal(owner.Value, deal.OwnerUserId);
        Assert.Equal(team, deal.TeamId);
        Assert.Equal(region, deal.RegionId);
        Assert.Empty(deal.Lines);
    }

    [Fact]
    public async Task A_deal_is_visible_under_a_region_grant_that_sees_its_parent_account_through_both_paths()
    {
        var tenant = TenantId.New();
        var region = Guid.NewGuid();
        var account = await NewAccountAsync(tenant, UserId.New(), "TX-D-REGION", region: region);

        var deals = DealsFor(tenant, out var db);
        var creatorScopes = DataScopeSet.Of(tenant, new DataScope.Account(account.Id));
        var created = (await deals.CreateAsync(creatorScopes, DealFor(account.Id, "r-deal"))).Deal!;

        var regionScopes = DataScopeSet.Of(tenant, new DataScope.Region(region));

        var efIds = await db.Deals.WhereInScope(regionScopes).Select(d => d.Id).ToListAsync();
        Assert.Equal(new[] { created.Id }, efIds);

        var gridIds = await AllGridIdsAsync(deals, regionScopes);
        Assert.Equal(new[] { created.Id }, gridIds);
    }

    [Fact]
    public async Task A_deal_is_visible_under_an_account_grant_that_sees_its_parent_account_through_both_paths()
    {
        var tenant = TenantId.New();
        var account = await NewAccountAsync(tenant, UserId.New(), "TX-D-ACCGRANT");

        var deals = DealsFor(tenant, out var db);
        var accountScopes = DataScopeSet.Of(tenant, new DataScope.Account(account.Id));
        var created = (await deals.CreateAsync(accountScopes, DealFor(account.Id, "a-deal"))).Deal!;

        var efIds = await db.Deals.WhereInScope(accountScopes).Select(d => d.Id).ToListAsync();
        Assert.Equal(new[] { created.Id }, efIds);

        var gridIds = await AllGridIdsAsync(deals, accountScopes);
        Assert.Equal(new[] { created.Id }, gridIds);
    }

    // ---- One-account rule: valid + in-scope account required -----------------------------------

    [Fact]
    public async Task Create_against_an_unknown_account_is_account_not_found()
    {
        var tenant = TenantId.New();
        var deals = DealsFor(tenant, out _);
        var scopes = DataScopeSet.Of(tenant, new DataScope.AllTenant());

        var result = await deals.CreateAsync(scopes, DealFor(Guid.NewGuid(), "nowhere"));

        Assert.Equal(DealCreateStatus.AccountNotFound, result.Status);
        Assert.Null(result.Deal);
    }

    [Fact]
    public async Task Create_against_an_account_outside_the_callers_scope_fails_closed()
    {
        var tenant = TenantId.New();
        var account = await NewAccountAsync(tenant, UserId.New(), "TX-D-OUTSCOPE");

        var deals = DealsFor(tenant, out _);
        // A Self grant for a DIFFERENT user: the account is out of the caller's scope, so a deal may not be
        // opened against it. Fail-closed — reported as not-found.
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(UserId.New()));

        var result = await deals.CreateAsync(scopes, DealFor(account.Id, "intruder"));

        Assert.Equal(DealCreateStatus.AccountNotFound, result.Status);
    }

    [Fact]
    public async Task Create_against_a_cross_tenant_account_fails_closed()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        var accountInA = await NewAccountAsync(tenantA, UserId.New(), "TX-D-XTENANT");

        var dealsInB = DealsFor(tenantB, out _);
        var scopes = DataScopeSet.Of(tenantB, new DataScope.AllTenant());

        var result = await dealsInB.CreateAsync(scopes, DealFor(accountInA.Id, "crosser"));

        Assert.Equal(DealCreateStatus.AccountNotFound, result.Status);
    }

    [Fact]
    public void A_deal_cannot_be_constructed_without_an_account()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Deal(Guid.NewGuid(), null!, "x", 1m, 0m));
    }

    // ---- Add line (the aggregate owns its lines) -----------------------------------------------

    [Fact]
    public async Task Add_line_saves_the_line_with_the_deal_and_read_back_returns_it()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner, "TX-D-LINE");

        var deals = DealsFor(tenant, out _);
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));
        var deal = (await deals.CreateAsync(scopes, DealFor(account.Id, "with-line"))).Deal!;

        var added = await deals.AddLineAsync(
            scopes, deal.Id, new AddDealLineRequest("SKU-1", UnitPrice: 100m, Quantity: 3, PriceListVersion: "v1"));

        Assert.Equal(DealLineAddStatus.Added, added.Status);
        var line = Assert.Single(added.Deal!.Lines);
        Assert.Equal("SKU-1", line.ProductRef);
        Assert.Equal(100m, line.UnitPrice);
        Assert.Equal(3, line.Quantity);
        Assert.Equal("v1", line.PriceListVersion);

        // A fresh read of the aggregate carries the line — it was saved with the deal.
        var reread = await DealsFor(tenant, out _).GetAsync(scopes, deal.Id);
        Assert.Single(reread!.Lines);
    }

    [Fact]
    public async Task Add_line_to_a_deal_outside_the_callers_scope_is_reported_as_not_found()
    {
        var tenant = TenantId.New();
        var account = await NewAccountAsync(tenant, UserId.New(), "TX-D-LINE-SCOPE");
        var deals = DealsFor(tenant, out _);

        var broad = DataScopeSet.Of(tenant, new DataScope.Account(account.Id));
        var deal = (await deals.CreateAsync(broad, DealFor(account.Id, "hidden"))).Deal!;

        var narrow = DataScopeSet.Of(tenant, new DataScope.Self(UserId.New()));
        var result = await deals.AddLineAsync(
            narrow, deal.Id, new AddDealLineRequest("SKU-X", 1m, 1, null));

        Assert.Equal(DealLineAddStatus.DealNotFound, result.Status);
    }

    // ---- Tenant isolation + empty-scope deny ---------------------------------------------------

    [Fact]
    public async Task An_agent_in_one_tenant_sees_only_that_tenants_deals_through_both_paths()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        var owner = UserId.New();

        var accA = await NewAccountAsync(tenantA, owner, "TX-D-ISO-A");
        var accB = await NewAccountAsync(tenantB, owner, "TX-D-ISO-B");

        var dealsA = DealsFor(tenantA, out var dbA);
        var dealsB = DealsFor(tenantB, out _);
        var scopeA = DataScopeSet.Of(tenantA, new DataScope.Self(owner));
        var scopeB = DataScopeSet.Of(tenantB, new DataScope.Self(owner));

        var mine = (await dealsA.CreateAsync(scopeA, DealFor(accA.Id, "mine"))).Deal!.Id;
        await dealsB.CreateAsync(scopeB, DealFor(accB.Id, "theirs"));

        var efIds = await dbA.Deals.WhereInScope(scopeA).Select(d => d.Id).ToListAsync();
        Assert.Equal(new[] { mine }, efIds);

        var gridIds = await AllGridIdsAsync(dealsA, scopeA);
        Assert.Equal(new[] { mine }, gridIds);
    }

    [Fact]
    public async Task An_empty_scope_set_returns_zero_deals_through_both_paths()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner, "TX-D-EMPTY");

        var deals = DealsFor(tenant, out var db);
        var broad = DataScopeSet.Of(tenant, new DataScope.Self(owner));
        await deals.CreateAsync(broad, DealFor(account.Id, "hidden"));

        var none = DataScopeSet.None(tenant);

        Assert.Null(await deals.GetAsync(none, Guid.NewGuid()));
        Assert.Empty(await db.Deals.WhereInScope(none).ToListAsync());
        Assert.Empty(await AllGridIdsAsync(deals, none));
    }
}
