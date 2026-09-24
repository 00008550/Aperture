using Aperture.Modules.Sales.Application;
using Aperture.Modules.Sales.Domain;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Multitenancy;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aperture.Modules.Sales.Tests;

/// <summary>
/// Plan 011-P4, edges 17–19, against a real PostgreSQL: the contact and deal read models carry the parent
/// account's <em>current</em> name, and that name is never a scope leak. The differential runs across
/// Self/Team/Region/Account grants on <b>both</b> paths — the reader-role grid (<see cref="ScopedConnection"/>
/// + the <c>LEFT JOIN sales.accounts</c> under RLS) and the EF read (<c>WhereInScope</c> account lookup) —
/// and asserts every non-null name belongs to an account the same scope's accounts grid returns. An account
/// made invisible under RLS (its scope columns diverged from the children's behind the service's back)
/// yields a visible child with a <c>null</c> name: fail closed on the label, never on the row.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AccountNameReadModelTests(PostgresFixture postgres)
{
    static AccountNameReadModelTests() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private ScopedConnection Reader() =>
        new(NpgsqlDataSource.Create(postgres.ReaderConnectionString), NullLogger<ScopedConnection>.Instance);

    private AccountService AccountsFor(TenantId tenant) => new(postgres.CreateContext(tenant), Reader());

    private ContactService ContactsFor(TenantId tenant) => new(postgres.CreateContext(tenant), Reader());

    private DealService DealsFor(TenantId tenant) =>
        new(postgres.CreateContext(tenant), Reader(), new ConfiguredDiscountThresholdProvider(100m));

    private async Task<AccountView> NewAccountAsync(
        TenantId tenant, UserId owner, string name, Guid? region = null, Guid? team = null)
    {
        var result = await AccountsFor(tenant).CreateAsync(
            tenant, owner, new CreateAccountRequest(name, $"TX-{Guid.NewGuid():N}", 1000m, 30, region, team));
        Assert.Equal(AccountCreateStatus.Created, result.Status);
        return result.Account!;
    }

    private static CreateContactRequest Person(string name) =>
        new(name, Email: null, Phone: null, Messenger: null);

    private static CreateDealRequest DealFor(Guid accountId, string name) =>
        new(accountId, name, Amount: 5000m, DiscountPct: 5m);

    private static async Task<IReadOnlyList<ContactView>> ContactGridAsync(ContactService s, DataScopeSet scopes)
    {
        var rows = new List<ContactView>();
        string? cursor = null;
        do
        {
            var page = await s.ListAsync(scopes, includeDeparted: true, limit: 100, cursor);
            rows.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return rows;
    }

    private static async Task<IReadOnlyList<DealView>> DealGridAsync(DealService s, DataScopeSet scopes)
    {
        var rows = new List<DealView>();
        string? cursor = null;
        do
        {
            var page = await s.ListAsync(scopes, limit: 100, cursor);
            rows.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return rows;
    }

    private static async Task<IReadOnlyDictionary<Guid, string>> AccountGridAsync(
        AccountService s, DataScopeSet scopes)
    {
        var rows = new Dictionary<Guid, string>();
        string? cursor = null;
        do
        {
            var page = await s.ListAsync(scopes, limit: 100, cursor);
            foreach (var a in page.Items)
            {
                rows[a.Id] = a.Name;
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return rows;
    }

    // Moves an account's scope columns away from its children's through the owner connection (bypasses RLS
    // and the service's same-transaction re-stamp), so under RLS the account is invisible to a grant that
    // still admits the children. This is the "divergence" the plan's failure-modes table says must fail
    // closed; the service itself never produces it.
    private async Task DivergeAccountScopeAsync(Guid accountId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE sales.accounts
            SET owner_user_id = @owner, team_id = NULL, region_id = NULL
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("owner", Guid.NewGuid());
        command.Parameters.AddWithValue("id", accountId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    public enum Grant
    {
        Self,
        Team,
        Region,
        Account,
    }

    // ---- Edge 17: the current name on grids, detail, and the write responses ---------------------

    [Fact]
    public async Task Contacts_grid_deals_grid_and_deal_detail_carry_the_parents_current_name()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner, "Globex Corp");
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));

        var contact = (await ContactsFor(tenant).CreateAsync(scopes, account.Id, Person("Hank"))).Contact!;
        var deal = (await DealsFor(tenant).CreateAsync(scopes, DealFor(account.Id, "Globex renewal"))).Deal!;

        Assert.Equal("Globex Corp", Assert.Single(await ContactGridAsync(ContactsFor(tenant), scopes)).AccountName);
        Assert.Equal("Globex Corp", Assert.Single(await DealGridAsync(DealsFor(tenant), scopes)).AccountName);
        Assert.Equal("Globex Corp", (await DealsFor(tenant).GetAsync(scopes, deal.Id))!.AccountName);
        Assert.Equal("Globex Corp", contact.AccountName);
        Assert.Equal("Globex Corp", deal.AccountName);
    }

    [Fact]
    public async Task Create_depart_transition_and_add_line_responses_carry_the_account_name()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner, "Initech");
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));

        var contact = (await ContactsFor(tenant).CreateAsync(scopes, account.Id, Person("Peter"))).Contact!;
        Assert.Equal("Initech", contact.AccountName);

        var departed = await ContactsFor(tenant).DepartAsync(scopes, contact.Id);
        Assert.Equal(ContactDepartStatus.Departed, departed.Status);
        Assert.Equal("Initech", departed.Contact!.AccountName);

        var deal = (await DealsFor(tenant).CreateAsync(scopes, DealFor(account.Id, "TPS reports"))).Deal!;
        Assert.Equal("Initech", deal.AccountName);

        var added = await DealsFor(tenant).AddLineAsync(
            scopes, deal.Id, new AddDealLineRequest("SKU-1", 10m, 2, "v1"));
        Assert.Equal(DealLineAddStatus.Added, added.Status);
        Assert.Equal("Initech", added.Deal!.AccountName);

        var moved = await DealsFor(tenant).TransitionAsync(
            scopes, deal.Id, new TransitionDealRequest(Deal.Stages.Qualified));
        Assert.Equal(DealTransitionOutcome.Transitioned, moved.Outcome);
        Assert.Equal("Initech", moved.Deal!.AccountName);

        // A stale add-line's 409 body is the current deal — it carries the name too.
        var stale = await DealsFor(tenant).AddLineAsync(
            scopes, deal.Id, new AddDealLineRequest("SKU-2", 1m, 1, "v1", ExpectedVersion: deal.Version));
        Assert.Equal(DealLineAddStatus.Conflict, stale.Status);
        Assert.Equal("Initech", stale.Deal!.AccountName);
    }

    [Fact]
    public async Task After_the_account_is_renamed_the_next_read_shows_the_new_name_on_every_path()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner, "Old Name Ltd");
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));
        await ContactsFor(tenant).CreateAsync(scopes, account.Id, Person("Ann"));
        var deal = (await DealsFor(tenant).CreateAsync(scopes, DealFor(account.Id, "d"))).Deal!;

        var renamed = await AccountsFor(tenant).UpdateAsync(
            scopes,
            account.Id,
            new UpdateAccountRequest(
                owner.Value, "New Name Ltd", account.CreditLimit, account.PaymentTermsDays,
                account.RegionId, account.TeamId, account.Version));
        Assert.Equal(AccountUpdateStatus.Updated, renamed.Status);

        Assert.Equal("New Name Ltd", Assert.Single(await ContactGridAsync(ContactsFor(tenant), scopes)).AccountName);
        Assert.Equal("New Name Ltd", Assert.Single(await DealGridAsync(DealsFor(tenant), scopes)).AccountName);
        Assert.Equal("New Name Ltd", (await DealsFor(tenant).GetAsync(scopes, deal.Id))!.AccountName);
    }

    // ---- Edge 18: the name is not a scope leak --------------------------------------------------

    [Theory]
    [InlineData(Grant.Self)]
    [InlineData(Grant.Team)]
    [InlineData(Grant.Region)]
    [InlineData(Grant.Account)]
    public async Task Every_account_name_on_the_child_grids_and_detail_belongs_to_an_account_the_same_scope_can_read(
        Grant grant)
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var team = Guid.NewGuid();
        var region = Guid.NewGuid();
        var mine = await NewAccountAsync(tenant, owner, "In scope", region, team);
        var theirs = await NewAccountAsync(tenant, UserId.New(), "Out of scope", Guid.NewGuid(), Guid.NewGuid());

        // Children created by a caller who can see each parent.
        var mineScopes = DataScopeSet.Of(tenant, new DataScope.Account(mine.Id));
        var theirScopes = DataScopeSet.Of(tenant, new DataScope.Account(theirs.Id));
        await ContactsFor(tenant).CreateAsync(mineScopes, mine.Id, Person("m"));
        await ContactsFor(tenant).CreateAsync(theirScopes, theirs.Id, Person("t"));
        var myDeal = (await DealsFor(tenant).CreateAsync(mineScopes, DealFor(mine.Id, "m"))).Deal!;
        var theirDeal = (await DealsFor(tenant).CreateAsync(theirScopes, DealFor(theirs.Id, "t"))).Deal!;

        DataScope scope = grant switch
        {
            Grant.Self => new DataScope.Self(owner),
            Grant.Team => new DataScope.Team(team),
            Grant.Region => new DataScope.Region(region),
            Grant.Account => new DataScope.Account(mine.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(grant)),
        };
        var s = DataScopeSet.Of(tenant, scope);

        var readableAccounts = await AccountGridAsync(AccountsFor(tenant), s);
        Assert.Equal(new[] { mine.Id }, readableAccounts.Keys);

        var contacts = await ContactGridAsync(ContactsFor(tenant), s);
        var deals = await DealGridAsync(DealsFor(tenant), s);
        var detail = await DealsFor(tenant).GetAsync(s, myDeal.Id);

        // ScopedConnection path: every non-null name belongs to an account the same scope reads, and is
        // exactly that account's name.
        Assert.NotEmpty(contacts);
        Assert.NotEmpty(deals);
        foreach (var (accountId, name) in contacts.Select(c => (c.AccountId, c.AccountName))
                     .Concat(deals.Select(d => (d.AccountId, d.AccountName))))
        {
            Assert.NotNull(name);
            Assert.True(readableAccounts.TryGetValue(accountId, out var readable), "name for an unreadable account");
            Assert.Equal(readable, name);
        }

        // EF path: the same.
        Assert.NotNull(detail);
        Assert.Equal(readableAccounts[detail!.AccountId], detail.AccountName);

        // And the out-of-scope parent's name never surfaces anywhere.
        Assert.DoesNotContain(contacts, c => c.AccountName == "Out of scope");
        Assert.DoesNotContain(deals, d => d.AccountName == "Out of scope");
        Assert.Null(await DealsFor(tenant).GetAsync(s, theirDeal.Id));
    }

    [Fact]
    public async Task A_child_whose_account_is_invisible_under_rls_still_returns_with_a_null_name_on_both_paths()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner, "Soon invisible");
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));
        var contact = (await ContactsFor(tenant).CreateAsync(scopes, account.Id, Person("c"))).Contact!;
        var deal = (await DealsFor(tenant).CreateAsync(scopes, DealFor(account.Id, "d"))).Deal!;

        await DivergeAccountScopeAsync(account.Id);

        // The account is no longer readable under this scope...
        Assert.Empty(await AccountGridAsync(AccountsFor(tenant), scopes));
        Assert.Null(await AccountsFor(tenant).GetAsync(scopes, account.Id));

        // ...but its children, whose denormalised columns still match, are — with the name withheld.
        var contactRow = Assert.Single(await ContactGridAsync(ContactsFor(tenant), scopes));
        Assert.Equal(contact.Id, contactRow.Id);
        Assert.Null(contactRow.AccountName);

        var dealRow = Assert.Single(await DealGridAsync(DealsFor(tenant), scopes));
        Assert.Equal(deal.Id, dealRow.Id);
        Assert.Null(dealRow.AccountName);

        var detail = await DealsFor(tenant).GetAsync(scopes, deal.Id);
        Assert.NotNull(detail);
        Assert.Null(detail!.AccountName);

        var moved = await DealsFor(tenant).TransitionAsync(
            scopes, deal.Id, new TransitionDealRequest(Deal.Stages.Qualified));
        Assert.Equal(DealTransitionOutcome.Transitioned, moved.Outcome);
        Assert.Null(moved.Deal!.AccountName);

        var departed = await ContactsFor(tenant).DepartAsync(scopes, contact.Id);
        Assert.Null(departed.Contact!.AccountName);
    }

    [Fact]
    public async Task A_child_pointing_at_another_tenants_account_never_shows_that_tenants_name()
    {
        // Rows seeded through the owner connection: a deal and a contact in tenant A whose account_id names an
        // account in tenant B. The service can never produce this; the join must still not cross tenants.
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        var ownerA = UserId.New();
        var foreignAccount = Guid.NewGuid();
        await postgres.SeedAccountAsync(foreignAccount, tenantB, UserId.New(), $"TX-{Guid.NewGuid():N}");
        var dealId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        await postgres.SeedDealAsync(dealId, tenantA, foreignAccount, ownerA);
        await postgres.SeedContactAsync(contactId, tenantA, foreignAccount, ownerA);

        var scopes = DataScopeSet.Of(tenantA, new DataScope.Self(ownerA));

        var dealRow = Assert.Single(await DealGridAsync(DealsFor(tenantA), scopes));
        Assert.Equal(dealId, dealRow.Id);
        Assert.Null(dealRow.AccountName);

        var contactRow = Assert.Single(await ContactGridAsync(ContactsFor(tenantA), scopes));
        Assert.Equal(contactId, contactRow.Id);
        Assert.Null(contactRow.AccountName);

        Assert.Null((await DealsFor(tenantA).GetAsync(scopes, dealId))!.AccountName);
    }

    // ---- Edge 19: an empty scope set still denies -----------------------------------------------

    [Fact]
    public async Task An_empty_scope_set_returns_no_child_rows_and_so_no_names_through_both_paths()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner, "Hidden Inc");
        var broad = DataScopeSet.Of(tenant, new DataScope.Self(owner));
        await ContactsFor(tenant).CreateAsync(broad, account.Id, Person("c"));
        var deal = (await DealsFor(tenant).CreateAsync(broad, DealFor(account.Id, "d"))).Deal!;

        var none = DataScopeSet.None(tenant);

        Assert.Empty(await ContactGridAsync(ContactsFor(tenant), none));
        Assert.Empty(await DealGridAsync(DealsFor(tenant), none));
        Assert.Null(await DealsFor(tenant).GetAsync(none, deal.Id));
    }
}
