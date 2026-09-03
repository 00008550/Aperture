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
/// Plan 002-P2's test list, by name, against a real PostgreSQL: tenant isolation, empty-scope deny,
/// scope union, absent-column narrowing, tax-id dedup, keyset pagination, and xmin concurrency — proven
/// on <b>both</b> the EF write-model read (<see cref="ScopeQuerying.WhereInScope{T}"/>) and the reader-role
/// grid (<see cref="ScopedConnection"/> + RLS), which must agree (edges 1–5, 16, 18 for accounts).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AccountServiceTests(PostgresFixture postgres)
{
    static AccountServiceTests() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private AccountService ServiceFor(TenantId tenant, out SalesDbContext db)
    {
        db = postgres.CreateContext(tenant);
        var reader = NpgsqlDataSource.Create(postgres.ReaderConnectionString);
        return new AccountService(db, new ScopedConnection(reader, NullLogger<ScopedConnection>.Instance));
    }

    private static CreateAccountRequest Create(string taxId, Guid? region = null, Guid? team = null) =>
        new($"Acme {taxId}", taxId, CreditLimit: 1000m, PaymentTermsDays: 30, region, team);

    private static async Task<IReadOnlyList<Guid>> AllGridIdsAsync(AccountService service, DataScopeSet scopes)
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

    // ---- Create + read-your-writes -------------------------------------------------------------

    [Fact]
    public async Task Create_stamps_tenant_owner_and_self_account_id_from_the_principal()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var service = ServiceFor(tenant, out _);

        var result = await service.CreateAsync(tenant, owner, Create("TX-CREATE-1"));

        Assert.Equal(AccountCreateStatus.Created, result.Status);
        var account = result.Account!;
        Assert.Equal(tenant.Value, account.TenantId);
        Assert.Equal(owner.Value, account.OwnerUserId);
        // account_id equals the row's own id: a DataScope.Account(id) grant admits the account itself.
        Assert.Equal(account.Id, account.AccountId);
    }

    // ---- Edge 5: tax-id dedup, within and across tenants --------------------------------------

    [Fact]
    public async Task Duplicate_tax_id_in_the_same_tenant_is_a_domain_error_not_a_second_row()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var service = ServiceFor(tenant, out var db);

        var first = await service.CreateAsync(tenant, owner, Create("TX-DUP"));
        var second = await service.CreateAsync(tenant, owner, Create("TX-DUP"));

        Assert.Equal(AccountCreateStatus.Created, first.Status);
        Assert.Equal(AccountCreateStatus.DuplicateTaxId, second.Status);
        Assert.Null(second.Account);

        var rows = await db.Accounts.IgnoreQueryFilters().CountAsync(a => a.TaxId == "TX-DUP");
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task The_same_tax_id_in_a_different_tenant_is_a_distinct_account()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        var serviceA = ServiceFor(tenantA, out _);
        var serviceB = ServiceFor(tenantB, out _);

        var a = await serviceA.CreateAsync(tenantA, UserId.New(), Create("TX-SHARED"));
        var b = await serviceB.CreateAsync(tenantB, UserId.New(), Create("TX-SHARED"));

        Assert.Equal(AccountCreateStatus.Created, a.Status);
        Assert.Equal(AccountCreateStatus.Created, b.Status);
        Assert.NotEqual(a.Account!.Id, b.Account!.Id);
    }

    // ---- Edge 1: tenant isolation, EF and RLS -------------------------------------------------

    [Fact]
    public async Task An_agent_in_one_tenant_sees_only_that_tenants_accounts_through_both_paths()
    {
        // Reuse the SAME guids as owner in both tenants: only the tenant boundary keeps them apart, so a
        // filter that forgot tenant would leak here.
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        var owner = UserId.New();

        var serviceA = ServiceFor(tenantA, out var dbA);
        var serviceB = ServiceFor(tenantB, out _);

        var mine = (await serviceA.CreateAsync(tenantA, owner, Create("TX-A"))).Account!.Id;
        await serviceB.CreateAsync(tenantB, owner, Create("TX-B"));

        var scopeA = DataScopeSet.Of(tenantA, new DataScope.Self(owner));

        // EF path.
        var efIds = await dbA.Accounts.WhereInScope(scopeA).Select(a => a.Id).ToListAsync();
        Assert.Equal(new[] { mine }, efIds);

        // RLS grid path.
        var gridIds = await AllGridIdsAsync(serviceA, scopeA);
        Assert.Equal(new[] { mine }, gridIds);
    }

    // ---- Edge 2: empty scope set denies, EF and RLS ------------------------------------------

    [Fact]
    public async Task An_empty_scope_set_returns_zero_rows_through_both_paths()
    {
        var tenant = TenantId.New();
        var service = ServiceFor(tenant, out var db);
        var created = (await service.CreateAsync(tenant, UserId.New(), Create("TX-EMPTY"))).Account!;

        var none = DataScopeSet.None(tenant);

        Assert.Null(await service.GetAsync(none, created.Id));
        Assert.Empty(await db.Accounts.WhereInScope(none).ToListAsync());
        Assert.Empty(await AllGridIdsAsync(service, none));
    }

    // ---- Edge 3: scope union ------------------------------------------------------------------

    [Fact]
    public async Task A_union_of_self_team_and_region_returns_exactly_that_union_through_both_paths()
    {
        var tenant = TenantId.New();
        var me = UserId.New();
        var team = Guid.NewGuid();
        var region = Guid.NewGuid();
        var service = ServiceFor(tenant, out var db);

        var mine = (await service.CreateAsync(tenant, me, Create("TX-MINE"))).Account!.Id;
        var teamAcc = (await service.CreateAsync(tenant, UserId.New(), Create("TX-TEAM", team: team))).Account!.Id;
        var regionAcc = (await service.CreateAsync(tenant, UserId.New(), Create("TX-REGION", region: region))).Account!.Id;
        // Neither mine, nor the team, nor the region: must not appear.
        await service.CreateAsync(tenant, UserId.New(), Create("TX-OTHER"));

        var scopes = DataScopeSet.Of(
            tenant,
            new DataScope.Self(me),
            new DataScope.Team(team),
            new DataScope.Region(region));

        var expected = new[] { mine, teamAcc, regionAcc }.OrderBy(x => x).ToArray();

        var efIds = await db.Accounts.WhereInScope(scopes).Select(a => a.Id).ToListAsync();
        Assert.Equal(expected, efIds.OrderBy(x => x).ToArray());

        var gridIds = await AllGridIdsAsync(service, scopes);
        Assert.Equal(expected, gridIds.OrderBy(x => x).ToArray());
    }

    // ---- Edge 4: absent scope column narrows -------------------------------------------------

    [Fact]
    public async Task A_team_grant_excludes_an_account_with_no_team_through_both_paths()
    {
        var tenant = TenantId.New();
        var team = Guid.NewGuid();
        var service = ServiceFor(tenant, out var db);

        // An account with team_id NULL: a Team grant must not admit it (NULL <> ANY).
        var noTeam = (await service.CreateAsync(tenant, UserId.New(), Create("TX-NOTEAM"))).Account!.Id;
        var withTeam = (await service.CreateAsync(tenant, UserId.New(), Create("TX-WITHTEAM", team: team))).Account!.Id;

        var scopes = DataScopeSet.Of(tenant, new DataScope.Team(team));

        var efIds = await db.Accounts.WhereInScope(scopes).Select(a => a.Id).ToListAsync();
        Assert.Equal(new[] { withTeam }, efIds);
        Assert.DoesNotContain(noTeam, efIds);

        var gridIds = await AllGridIdsAsync(service, scopes);
        Assert.Equal(new[] { withTeam }, gridIds);
        Assert.DoesNotContain(noTeam, gridIds);
    }

    // ---- Edge 16: keyset pagination stability -----------------------------------------------

    [Fact]
    public async Task Keyset_pagination_pages_every_row_once_under_concurrent_insert()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var service = ServiceFor(tenant, out _);
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));

        var seeded = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            seeded.Add((await service.CreateAsync(tenant, owner, Create($"TX-PAGE-{i:D2}"))).Account!.Id);
        }

        // Page with a small limit; after the first page, insert a new row (concurrent insert). Keyset
        // paging by (created_at, id) must neither skip nor duplicate a row across the pages.
        var seen = new List<Guid>();
        string? cursor = null;
        var inserted = false;
        do
        {
            var page = await service.ListAsync(scopes, limit: 10, cursor);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;

            if (!inserted && cursor is not null)
            {
                // A brand-new row sorts after everything already paged (later created_at), so it may or
                // may not appear on a later page, but it can never displace an already-seen one.
                await service.CreateAsync(tenant, owner, Create("TX-PAGE-NEW"));
                inserted = true;
            }
        }
        while (cursor is not null);

        // Every originally-seeded row appears exactly once; no duplicates anywhere.
        Assert.Equal(seeded.OrderBy(x => x), seen.Intersect(seeded).OrderBy(x => x));
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    // ---- xmin optimistic concurrency ---------------------------------------------------------

    [Fact]
    public async Task An_update_with_a_stale_version_conflicts_rather_than_losing_the_update()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var service = ServiceFor(tenant, out _);
        var created = (await service.CreateAsync(tenant, owner, Create("TX-XMIN"))).Account!;
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));

        // First update succeeds and moves xmin forward.
        var first = await service.UpdateAsync(
            scopes, created.Id,
            new UpdateAccountRequest(owner.Value, "Renamed", 2000m, 45, null, null, created.Version));
        Assert.Equal(AccountUpdateStatus.Updated, first.Status);

        // A second update replaying the ORIGINAL version is stale — the row moved on — so it 409s.
        // A fresh context, because the first update's tracker already holds the new version.
        var freshService = ServiceFor(tenant, out _);
        var second = await freshService.UpdateAsync(
            scopes, created.Id,
            new UpdateAccountRequest(owner.Value, "Renamed again", 3000m, 60, null, null, created.Version));
        Assert.Equal(AccountUpdateStatus.Conflict, second.Status);
    }

    [Fact]
    public async Task Update_outside_the_callers_scope_is_reported_as_not_found()
    {
        var tenant = TenantId.New();
        var service = ServiceFor(tenant, out _);
        var created = (await service.CreateAsync(tenant, UserId.New(), Create("TX-SCOPED-UPDATE"))).Account!;

        // The caller holds a Self grant for a DIFFERENT user, so the account is out of scope.
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(UserId.New()));
        var result = await service.UpdateAsync(
            scopes, created.Id,
            new UpdateAccountRequest(created.OwnerUserId, "Nope", 1m, 1, null, null, created.Version));

        Assert.Equal(AccountUpdateStatus.NotFound, result.Status);
    }
}
