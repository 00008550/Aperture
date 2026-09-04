using Aperture.Modules.Sales.Application;
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
/// Plan 002-P4, edge 8 — the account-reassignment re-stamp, both halves (contacts and deals). When an
/// account is reassigned to a new owner / region / team, every child row that denormalises those inherited
/// scope columns must be re-stamped in the <em>same transaction</em> as the account edit, so no child is
/// left visible under a stale grant. This is proven on <b>both</b> the EF write-model read
/// (<see cref="ScopeQuerying.WhereInScope{T}"/>) and the reader-role grid (<see cref="ScopedConnection"/> +
/// RLS): after reassignment the old-grant agent sees none of the account, its contact or its deal, and the
/// new-grant agent sees all three; <c>tenant_id</c> and <c>account_id</c> are never mutated; and a failed
/// edit rolls the child re-stamp back with it (one unit of work).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AccountReassignmentTests(PostgresFixture postgres)
{
    static AccountReassignmentTests() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private string ReaderConn => postgres.ReaderConnectionString;

    private AccountService AccountsFor(TenantId tenant, out SalesDbContext db)
    {
        db = postgres.CreateContext(tenant);
        return new AccountService(db, new ScopedConnection(
            NpgsqlDataSource.Create(ReaderConn), NullLogger<ScopedConnection>.Instance));
    }

    private ContactService ContactsFor(TenantId tenant, out SalesDbContext db)
    {
        db = postgres.CreateContext(tenant);
        return new ContactService(db, new ScopedConnection(
            NpgsqlDataSource.Create(ReaderConn), NullLogger<ScopedConnection>.Instance));
    }

    private DealService DealsFor(TenantId tenant, out SalesDbContext db)
    {
        db = postgres.CreateContext(tenant);
        return new DealService(db, new ScopedConnection(
            NpgsqlDataSource.Create(ReaderConn), NullLogger<ScopedConnection>.Instance));
    }

    private static async Task<IReadOnlyList<Guid>> AccountGridAsync(AccountService s, DataScopeSet scopes)
    {
        var page = await s.ListAsync(scopes, limit: 200, cursor: null);
        return page.Items.Select(i => i.Id).ToList();
    }

    private static async Task<IReadOnlyList<Guid>> ContactGridAsync(ContactService s, DataScopeSet scopes)
    {
        var page = await s.ListAsync(scopes, includeDeparted: true, limit: 200, cursor: null);
        return page.Items.Select(i => i.Id).ToList();
    }

    private static async Task<IReadOnlyList<Guid>> DealGridAsync(DealService s, DataScopeSet scopes)
    {
        var page = await s.ListAsync(scopes, limit: 200, cursor: null);
        return page.Items.Select(i => i.Id).ToList();
    }

    [Fact]
    public async Task Reassigning_an_account_restamps_its_contact_and_deal_in_one_transaction_through_both_paths()
    {
        var tenant = TenantId.New();
        var u1 = UserId.New();
        var r1 = Guid.NewGuid();
        var t1 = Guid.NewGuid();
        var u2 = UserId.New();
        var r2 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        // acc owned by u1 in region r1 / team t1, with a contact and a deal under it.
        var accounts = AccountsFor(tenant, out _);
        var account = (await accounts.CreateAsync(
            tenant, u1, new CreateAccountRequest("Acme", "TX-REASSIGN", 1000m, 30, r1, t1))).Account!;

        var broad = DataScopeSet.Of(tenant, new DataScope.Account(account.Id));
        var contact = (await ContactsFor(tenant, out _).CreateAsync(
            broad, account.Id, new CreateContactRequest("alice", null, null, null))).Contact!;
        var deal = (await DealsFor(tenant, out _).CreateAsync(
            broad, new CreateDealRequest(account.Id, "big", 5000m, 0m))).Deal!;

        // Reassign owner, region and team in one PATCH.
        var reassign = await AccountsFor(tenant, out _).UpdateAsync(
            broad, account.Id,
            new UpdateAccountRequest(u2.Value, "Acme", 1000m, 30, r2, t2, account.Version));
        Assert.Equal(AccountUpdateStatus.Updated, reassign.Status);

        // ---- EF write-model read: the child rows carry the NEW scope; tenant_id / account_id unchanged.
        await using (var db = postgres.CreateContext(tenant))
        {
            var c = await db.Contacts.AsNoTracking().SingleAsync(x => x.Id == contact.Id);
            Assert.Equal(u2.Value, c.OwnerUserId.Value);
            Assert.Equal(r2, c.RegionId);
            Assert.Equal(t2, c.TeamId);
            Assert.Equal(account.Id, c.AccountId);          // immutable
            Assert.Equal(tenant.Value, c.TenantId.Value);   // immutable

            var d = await db.Deals.AsNoTracking().SingleAsync(x => x.Id == deal.Id);
            Assert.Equal(u2.Value, d.OwnerUserId.Value);
            Assert.Equal(r2, d.RegionId);
            Assert.Equal(t2, d.TeamId);
            Assert.Equal(account.Id, d.AccountId);          // immutable
            Assert.Equal(tenant.Value, d.TenantId.Value);   // immutable
        }

        // The old-grant agent (Self(u1) ∪ Region(r1) ∪ Team(t1)) now sees NONE of the three, on both paths.
        var oldGrant = DataScopeSet.Of(
            tenant, new DataScope.Self(u1), new DataScope.Region(r1), new DataScope.Team(t1));
        var newGrant = DataScopeSet.Of(
            tenant, new DataScope.Self(u2), new DataScope.Region(r2), new DataScope.Team(t2));

        var accounts2 = AccountsFor(tenant, out var accDb);
        var contacts2 = ContactsFor(tenant, out var conDb);
        var deals2 = DealsFor(tenant, out var dealDb);

        // EF path — old grant sees nothing.
        Assert.Empty(await accDb.Accounts.WhereInScope(oldGrant).Where(a => a.Id == account.Id).ToListAsync());
        Assert.Empty(await conDb.Contacts.WhereInScope(oldGrant).Where(c => c.Id == contact.Id).ToListAsync());
        Assert.Empty(await dealDb.Deals.WhereInScope(oldGrant).Where(d => d.Id == deal.Id).ToListAsync());

        // EF path — new grant sees all three.
        Assert.NotEmpty(await accDb.Accounts.WhereInScope(newGrant).Where(a => a.Id == account.Id).ToListAsync());
        Assert.NotEmpty(await conDb.Contacts.WhereInScope(newGrant).Where(c => c.Id == contact.Id).ToListAsync());
        Assert.NotEmpty(await dealDb.Deals.WhereInScope(newGrant).Where(d => d.Id == deal.Id).ToListAsync());

        // RLS grid — old grant sees nothing.
        Assert.DoesNotContain(account.Id, await AccountGridAsync(accounts2, oldGrant));
        Assert.DoesNotContain(contact.Id, await ContactGridAsync(contacts2, oldGrant));
        Assert.DoesNotContain(deal.Id, await DealGridAsync(deals2, oldGrant));

        // RLS grid — new grant sees all three.
        Assert.Contains(account.Id, await AccountGridAsync(accounts2, newGrant));
        Assert.Contains(contact.Id, await ContactGridAsync(contacts2, newGrant));
        Assert.Contains(deal.Id, await DealGridAsync(deals2, newGrant));
    }

    [Fact]
    public async Task A_conflicting_reassignment_rolls_back_the_account_edit_and_the_child_restamp_together()
    {
        var tenant = TenantId.New();
        var u1 = UserId.New();
        var r1 = Guid.NewGuid();
        var u2 = UserId.New();
        var r2 = Guid.NewGuid();

        var account = (await AccountsFor(tenant, out _).CreateAsync(
            tenant, u1, new CreateAccountRequest("Acme", "TX-REASSIGN-RB", 1000m, 30, r1, null))).Account!;
        var broad = DataScopeSet.Of(tenant, new DataScope.Account(account.Id));
        var contact = (await ContactsFor(tenant, out _).CreateAsync(
            broad, account.Id, new CreateContactRequest("bob", null, null, null))).Contact!;
        var deal = (await DealsFor(tenant, out _).CreateAsync(
            broad, new CreateDealRequest(account.Id, "d", 1m, 0m))).Deal!;

        // A first, successful edit changes a business field so the row's xmin actually moves forward (an
        // EF update of identical values is a no-op that would not advance the token).
        var first = await AccountsFor(tenant, out _).UpdateAsync(
            broad, account.Id,
            new UpdateAccountRequest(u1.Value, "Acme renamed", 1000m, 30, r1, null, account.Version));
        Assert.Equal(AccountUpdateStatus.Updated, first.Status);

        // A second edit replaying the ORIGINAL (now stale) version is a conflict: nothing commits — neither
        // the account edit nor the child re-stamp. This is the same-unit-of-work proof.
        var stale = await AccountsFor(tenant, out _).UpdateAsync(
            broad, account.Id,
            new UpdateAccountRequest(u2.Value, "Acme", 1000m, 30, r2, null, account.Version));
        Assert.Equal(AccountUpdateStatus.Conflict, stale.Status);

        // The children still carry the ORIGINAL owner/region — the stale edit's re-stamp was rolled back.
        await using var db = postgres.CreateContext(tenant);
        var c = await db.Contacts.AsNoTracking().SingleAsync(x => x.Id == contact.Id);
        Assert.Equal(u1.Value, c.OwnerUserId.Value);
        Assert.Equal(r1, c.RegionId);
        var d = await db.Deals.AsNoTracking().SingleAsync(x => x.Id == deal.Id);
        Assert.Equal(u1.Value, d.OwnerUserId.Value);
        Assert.Equal(r1, d.RegionId);
    }
}
