using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Access.Tests;

/// <summary>
/// 001-P4: a <see cref="DataScopeSet"/> becomes a <c>WHERE</c> clause the database evaluates.
/// <para>
/// Every test here asserts the <em>generated SQL</em>, not just the rows that came back. A result
/// count proves nothing: an in-memory filter over every row in the table produces exactly the same
/// count, while leaking the whole table to the application, breaking paging, and scaling with the
/// tenant's size rather than the user's scope. That regression is invisible to a count assertion,
/// which is why the plan calls for this one.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopePredicateSqlTests(PostgresFixture postgres)
{
    private readonly TenantId _tenant = TenantId.New();
    private readonly TenantId _otherTenant = TenantId.New();
    private readonly UserId _ivanov = UserId.New();
    private readonly UserId _petrova = UserId.New();
    private readonly Guid _teamA = Guid.NewGuid();
    private readonly Guid _teamB = Guid.NewGuid();
    private readonly Guid _north = Guid.NewGuid();
    private readonly Guid _south = Guid.NewGuid();
    private readonly Guid _keyAccount = Guid.NewGuid();
    private readonly Guid _foreignTeam = Guid.NewGuid();

    /// <summary>
    /// Seeds seven rows in one tenant, and two in a second tenant — one of which matches every
    /// scope under test on its own columns. That row is the one a fail-open predicate returns.
    /// <para>
    /// Each test instance mints its own tenant ids: the container is shared across the
    /// collection, so a fixed tenant would make the tests read each other's rows.
    /// </para>
    /// </summary>
    private async Task<ScopeProbeDbContext> SeedAsync()
    {
        var db = postgres.CreateScopeProbeContext();

        db.Rows.AddRange(
            Row(_tenant, _ivanov),
            Row(_tenant, _petrova),
            Row(_tenant, _petrova, team: _teamA),
            Row(_tenant, _petrova, team: _teamB),
            Row(_tenant, _petrova, region: _north),
            Row(_tenant, _petrova, region: _south),
            Row(_tenant, _petrova, account: _keyAccount),
            Row(_otherTenant, _ivanov, _teamA, _north, _keyAccount),
            Row(_otherTenant, _petrova, team: _foreignTeam));

        await db.SaveChangesAsync();
        return db;
    }

    private static ScopedRow Row(
        TenantId tenant,
        UserId owner,
        Guid? team = null,
        Guid? region = null,
        Guid? account = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            OwnerUserId = owner,
            TeamId = team,
            RegionId = region,
            AccountId = account,
        };

    /// <summary>
    /// The rows this scope set returns, and the <c>WHERE</c> clause that selected them. Client
    /// evaluation of a <c>Where</c> throws in EF Core, so a query that runs at all was translated
    /// — the clause is asserted on top of that, so a change that reintroduces client-side
    /// filtering by some other route still fails here rather than passing on row counts.
    /// </summary>
    private async Task<(List<ScopedRow> Rows, string Where)> QueryAsync(DataScopeSet scopes)
    {
        await using var db = await SeedAsync();

        var query = db.Rows.AsNoTracking().WhereInScope(scopes);
        var rows = await query.ToListAsync();

        return (rows, WhereClause(query.ToQueryString()));
    }

    private static string WhereClause(string sql)
    {
        var index = sql.IndexOf("WHERE ", StringComparison.Ordinal);

        // No WHERE at all is the fail-open shape this portion exists to prevent, so it fails
        // here rather than returning an empty string some assertion might tolerate.
        Assert.True(index >= 0, $"The generated SQL has no WHERE clause:{Environment.NewLine}{sql}");

        return sql[(index + "WHERE ".Length)..].Trim();
    }

    [Fact]
    public async Task The_self_scope_filters_by_owner_in_sql()
    {
        var (rows, where) = await QueryAsync(DataScopeSet.Of(_tenant, new DataScope.Self(_ivanov)));

        Assert.Equal("r.tenant_id = @Value AND r.owner_user_id = @Value1", where);
        Assert.Equal(_ivanov, Assert.Single(rows).OwnerUserId);
    }

    [Fact]
    public async Task The_team_scope_filters_by_team_in_sql_and_excludes_null_teams()
    {
        var (rows, where) = await QueryAsync(DataScopeSet.Of(_tenant, new DataScope.Team(_teamA)));

        // Plain equality against a parameter, not IS NOT DISTINCT FROM: a NULL team_id compares
        // unknown and is therefore not a match. Absent data narrows, never widens.
        Assert.Equal("r.tenant_id = @Value AND r.team_id = @Value1", where);
        Assert.Equal(_teamA, Assert.Single(rows).TeamId);
    }

    [Fact]
    public async Task The_region_scope_filters_by_region_in_sql_and_excludes_null_regions()
    {
        var (rows, where) = await QueryAsync(DataScopeSet.Of(_tenant, new DataScope.Region(_north)));

        Assert.Equal("r.tenant_id = @Value AND r.region_id = @Value1", where);
        Assert.Equal(_north, Assert.Single(rows).RegionId);
    }

    [Fact]
    public async Task The_account_scope_filters_by_account_in_sql_and_excludes_null_accounts()
    {
        var (rows, where) = await QueryAsync(
            DataScopeSet.Of(_tenant, new DataScope.Account(_keyAccount)));

        Assert.Equal("r.tenant_id = @Value AND r.account_id = @Value1", where);
        Assert.Equal(_keyAccount, Assert.Single(rows).AccountId);
    }

    [Fact]
    public async Task The_all_tenant_scope_filters_by_tenant_in_sql_and_stops_at_the_tenant_boundary()
    {
        var (rows, where) = await QueryAsync(DataScopeSet.Of(_tenant, new DataScope.AllTenant()));

        // "Everything in this tenant" is still a tenant predicate, never an unfiltered scan.
        Assert.Equal("r.tenant_id = @Value", where);
        Assert.Equal(7, rows.Count);
        Assert.All(rows, r => Assert.Equal(_tenant, r.TenantId));
    }

    [Fact]
    public async Task The_union_of_two_scopes_is_one_sql_predicate_over_both_columns()
    {
        var (rows, where) = await QueryAsync(
            DataScopeSet.Of(_tenant, new DataScope.Team(_teamA), new DataScope.Region(_north)));

        // The set is unordered, so the two disjuncts may appear either way round.
        Assert.StartsWith("r.tenant_id = @Value AND (", where, StringComparison.Ordinal);
        Assert.Contains("r.team_id = @", where, StringComparison.Ordinal);
        Assert.Contains("r.region_id = @", where, StringComparison.Ordinal);
        Assert.Contains(" OR ", where, StringComparison.Ordinal);

        // Union, not intersection: the team-A row and the north row, and nothing else.
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.TeamId == _teamA);
        Assert.Contains(rows, r => r.RegionId == _north);
    }

    [Fact]
    public async Task The_empty_scope_set_returns_nothing_and_never_a_query_without_a_filter()
    {
        var (rows, where) = await QueryAsync(DataScopeSet.None(_tenant));

        // The dangerous outcome is not "wrong rows", it is a SELECT with no predicate. The empty
        // set compiles to a clause the database can only ever answer with nothing.
        Assert.Equal("FALSE", where);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task A_scope_matching_a_row_in_another_tenant_still_returns_nothing()
    {
        // The foreign team exists, and exactly one row carries it — in the other tenant. Only
        // the tenant conjunct keeps it out, which is the guarantee under test.
        var (rows, where) = await QueryAsync(
            DataScopeSet.Of(_tenant, new DataScope.Team(_foreignTeam)));

        Assert.Equal("r.tenant_id = @Value AND r.team_id = @Value1", where);
        Assert.Empty(rows);
    }
}
