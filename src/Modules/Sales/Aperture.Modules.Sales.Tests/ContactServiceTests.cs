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
/// Plan 002-P3's test list, by name, against a real PostgreSQL: the one-account rule (a contact requires a
/// valid, in-scope account; a cross-tenant/out-of-scope account fails closed), departed-not-deleted (the
/// row stays and is excluded from active lists but present in history), and scope inheritance (a contact
/// is visible under exactly the grants that see its parent account) — proven on <b>both</b> the EF read
/// (<see cref="ScopeQuerying.WhereInScope{T}"/>) and the reader-role grid (<see cref="ScopedConnection"/> +
/// RLS), which must agree (edges 6 and 7, plus tenant isolation and empty-scope deny).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContactServiceTests(PostgresFixture postgres)
{
    static ContactServiceTests() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private ContactService ContactsFor(TenantId tenant, out SalesDbContext db)
    {
        db = postgres.CreateContext(tenant);
        var reader = NpgsqlDataSource.Create(postgres.ReaderConnectionString);
        return new ContactService(db, new ScopedConnection(reader, NullLogger<ScopedConnection>.Instance));
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

    private static CreateContactRequest Person(string name) =>
        new(name, Email: $"{name}@example.com", Phone: null, Messenger: null);

    private static async Task<IReadOnlyList<Guid>> AllGridIdsAsync(
        ContactService service, DataScopeSet scopes, bool includeDeparted = false)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await service.ListAsync(scopes, includeDeparted, limit: 100, cursor);
            ids.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return ids;
    }

    // ---- One-account rule + scope inheritance (edge 7) ---------------------------------------

    [Fact]
    public async Task Create_inherits_tenant_owner_team_and_region_from_the_parent_account()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var team = Guid.NewGuid();
        var region = Guid.NewGuid();
        var account = await NewAccountAsync(tenant, owner, "TX-C-INHERIT", region, team);

        var contacts = ContactsFor(tenant, out _);
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));

        var result = await contacts.CreateAsync(scopes, account.Id, Person("alice"));

        Assert.Equal(ContactCreateStatus.Created, result.Status);
        var contact = result.Contact!;
        Assert.Equal(tenant.Value, contact.TenantId);
        Assert.Equal(account.Id, contact.AccountId);
        Assert.Equal(owner.Value, contact.OwnerUserId);
        Assert.Equal(team, contact.TeamId);
        Assert.Equal(region, contact.RegionId);
        Assert.False(contact.IsDeparted);
    }

    [Fact]
    public async Task A_contact_is_visible_under_a_region_grant_that_sees_its_parent_account_through_both_paths()
    {
        var tenant = TenantId.New();
        var region = Guid.NewGuid();
        var account = await NewAccountAsync(tenant, UserId.New(), "TX-C-REGION", region: region);

        var contacts = ContactsFor(tenant, out var db);
        // The creator holds a broad grant to attach the contact; the *reader* below holds only Region(r).
        var creatorScopes = DataScopeSet.Of(tenant, new DataScope.Account(account.Id));
        var created = (await contacts.CreateAsync(creatorScopes, account.Id, Person("bob"))).Contact!;

        // A Region(r) agent — who sees the parent account by region — must see the contact on both paths.
        var regionScopes = DataScopeSet.Of(tenant, new DataScope.Region(region));

        var efIds = await db.Contacts.WhereInScope(regionScopes).Select(c => c.Id).ToListAsync();
        Assert.Equal(new[] { created.Id }, efIds);

        var gridIds = await AllGridIdsAsync(contacts, regionScopes);
        Assert.Equal(new[] { created.Id }, gridIds);
    }

    [Fact]
    public async Task A_contact_is_visible_under_an_account_grant_that_sees_its_parent_account_through_both_paths()
    {
        var tenant = TenantId.New();
        var account = await NewAccountAsync(tenant, UserId.New(), "TX-C-ACCGRANT");

        var contacts = ContactsFor(tenant, out var db);
        var accountScopes = DataScopeSet.Of(tenant, new DataScope.Account(account.Id));
        var created = (await contacts.CreateAsync(accountScopes, account.Id, Person("carol"))).Contact!;

        var efIds = await db.Contacts.WhereInScope(accountScopes).Select(c => c.Id).ToListAsync();
        Assert.Equal(new[] { created.Id }, efIds);

        var gridIds = await AllGridIdsAsync(contacts, accountScopes);
        Assert.Equal(new[] { created.Id }, gridIds);
    }

    // ---- One-account rule: valid + in-scope account required (edge 6) ------------------------

    [Fact]
    public async Task Create_against_an_unknown_account_is_account_not_found()
    {
        var tenant = TenantId.New();
        var contacts = ContactsFor(tenant, out _);
        var scopes = DataScopeSet.Of(tenant, new DataScope.AllTenant());

        var result = await contacts.CreateAsync(scopes, Guid.NewGuid(), Person("nobody"));

        Assert.Equal(ContactCreateStatus.AccountNotFound, result.Status);
        Assert.Null(result.Contact);
    }

    [Fact]
    public async Task Create_against_an_account_outside_the_callers_scope_fails_closed()
    {
        var tenant = TenantId.New();
        var accountOwner = UserId.New();
        var account = await NewAccountAsync(tenant, accountOwner, "TX-C-OUTSCOPE");

        var contacts = ContactsFor(tenant, out _);
        // A Self grant for a DIFFERENT user: the account is out of the caller's scope, so a contact may not
        // be attached to it. This is the fail-closed case — an out-of-scope account is not-found.
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(UserId.New()));

        var result = await contacts.CreateAsync(scopes, account.Id, Person("intruder"));

        Assert.Equal(ContactCreateStatus.AccountNotFound, result.Status);
    }

    [Fact]
    public async Task Create_against_a_cross_tenant_account_fails_closed()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        var owner = UserId.New();
        var accountInA = await NewAccountAsync(tenantA, owner, "TX-C-XTENANT");

        // A tenant-B caller, even with an AllTenant grant, cannot reference tenant A's account: the tenant
        // global filter hides it and the create fails closed rather than crossing the boundary.
        var contactsInB = ContactsFor(tenantB, out _);
        var scopes = DataScopeSet.Of(tenantB, new DataScope.AllTenant());

        var result = await contactsInB.CreateAsync(scopes, accountInA.Id, Person("crosser"));

        Assert.Equal(ContactCreateStatus.AccountNotFound, result.Status);
    }

    [Fact]
    public void A_contact_cannot_be_constructed_without_an_account()
    {
        // The one-account rule at its root: there is no constructor that admits a null (or second) account.
        Assert.Throws<ArgumentNullException>(
            () => new Contact(Guid.NewGuid(), null!, "x", null, null, null));
    }

    // ---- Departed, not deleted (edge 6) ------------------------------------------------------

    [Fact]
    public async Task A_departed_contact_stays_in_the_row_store_excluded_from_active_lists_but_in_history()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner, "TX-C-DEPART");

        var contacts = ContactsFor(tenant, out var db);
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));

        var active = (await contacts.CreateAsync(scopes, account.Id, Person("stays"))).Contact!;
        var leaving = (await contacts.CreateAsync(scopes, account.Id, Person("leaves"))).Contact!;

        var departed = await contacts.DepartAsync(scopes, leaving.Id);
        Assert.Equal(ContactDepartStatus.Departed, departed.Status);
        Assert.True(departed.Contact!.IsDeparted);
        Assert.NotNull(departed.Contact.DepartedAt);

        // The row is NOT deleted — it is still present in the store, attributable.
        var stillThere = await db.Contacts.AsNoTracking()
            .IgnoreQueryFilters().CountAsync(c => c.Id == leaving.Id);
        Assert.Equal(1, stillThere);

        // Active grid (RLS path) excludes the departed contact.
        var activeIds = await AllGridIdsAsync(contacts, scopes, includeDeparted: false);
        Assert.Contains(active.Id, activeIds);
        Assert.DoesNotContain(leaving.Id, activeIds);

        // History (includeDeparted) shows it again.
        var historyIds = await AllGridIdsAsync(contacts, scopes, includeDeparted: true);
        Assert.Contains(active.Id, historyIds);
        Assert.Contains(leaving.Id, historyIds);
    }

    [Fact]
    public async Task Depart_is_idempotent_and_keeps_the_original_timestamp()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner, "TX-C-DEPART2X");
        var contacts = ContactsFor(tenant, out _);
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));

        var contact = (await contacts.CreateAsync(scopes, account.Id, Person("twice"))).Contact!;

        await contacts.DepartAsync(scopes, contact.Id);

        // Reload the PERSISTED departed-at (a fresh service, so it comes from the row, at the database's
        // microsecond resolution) — this is the value idempotence must preserve.
        var afterFirst = await ContactsFor(tenant, out _).DepartAsync(scopes, contact.Id);
        // A second, independent depart must not rewrite it: same persisted timestamp, not a new one.
        var afterSecond = await ContactsFor(tenant, out _).DepartAsync(scopes, contact.Id);

        Assert.Equal(ContactDepartStatus.Departed, afterFirst.Status);
        Assert.Equal(ContactDepartStatus.Departed, afterSecond.Status);
        Assert.True(afterFirst.Contact!.IsDeparted);
        Assert.Equal(afterFirst.Contact.DepartedAt, afterSecond.Contact!.DepartedAt);
    }

    [Fact]
    public async Task Depart_outside_the_callers_scope_is_reported_as_not_found()
    {
        var tenant = TenantId.New();
        var account = await NewAccountAsync(tenant, UserId.New(), "TX-C-DEPART-SCOPE");
        var contacts = ContactsFor(tenant, out _);

        var broad = DataScopeSet.Of(tenant, new DataScope.Account(account.Id));
        var contact = (await contacts.CreateAsync(broad, account.Id, Person("hidden"))).Contact!;

        // A caller whose scope does not admit the contact cannot depart it — reported as not-found.
        var narrow = DataScopeSet.Of(tenant, new DataScope.Self(UserId.New()));
        var result = await contacts.DepartAsync(narrow, contact.Id);

        Assert.Equal(ContactDepartStatus.NotFound, result.Status);
    }

    // ---- Tenant isolation + empty-scope deny -------------------------------------------------

    [Fact]
    public async Task An_agent_in_one_tenant_sees_only_that_tenants_contacts_through_both_paths()
    {
        // Reuse the SAME guids as owner in both tenants: only the tenant boundary keeps them apart.
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        var owner = UserId.New();

        var accA = await NewAccountAsync(tenantA, owner, "TX-C-ISO-A");
        var accB = await NewAccountAsync(tenantB, owner, "TX-C-ISO-B");

        var contactsA = ContactsFor(tenantA, out var dbA);
        var contactsB = ContactsFor(tenantB, out _);
        var scopeA = DataScopeSet.Of(tenantA, new DataScope.Self(owner));
        var scopeB = DataScopeSet.Of(tenantB, new DataScope.Self(owner));

        var mine = (await contactsA.CreateAsync(scopeA, accA.Id, Person("mine"))).Contact!.Id;
        await contactsB.CreateAsync(scopeB, accB.Id, Person("theirs"));

        var efIds = await dbA.Contacts.WhereInScope(scopeA).Select(c => c.Id).ToListAsync();
        Assert.Equal(new[] { mine }, efIds);

        var gridIds = await AllGridIdsAsync(contactsA, scopeA);
        Assert.Equal(new[] { mine }, gridIds);
    }

    [Fact]
    public async Task An_empty_scope_set_returns_zero_contacts_through_both_paths()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner, "TX-C-EMPTY");

        var contacts = ContactsFor(tenant, out var db);
        var broad = DataScopeSet.Of(tenant, new DataScope.Self(owner));
        await contacts.CreateAsync(broad, account.Id, Person("hidden"));

        var none = DataScopeSet.None(tenant);

        Assert.Empty(await db.Contacts.WhereInScope(none).ToListAsync());
        Assert.Empty(await AllGridIdsAsync(contacts, none, includeDeparted: true));
    }
}
