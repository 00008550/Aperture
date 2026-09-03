using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Data.RowLevelSecurity;
using Aperture.SharedKernel.Multitenancy;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aperture.Modules.Access.Tests;

/// <summary>
/// 009-P5: the honesty pin for the whole RLS redesign — the differential test. The scope rule is
/// encoded <em>three</em> times in this codebase, and the three encodings must agree, at the DBMS,
/// on identical adversarial data, or the redesign has drifted:
/// <list type="number">
/// <item>the EF <see cref="Expression"/> — <see cref="ScopeQuerying.WhereInScope{T}"/> (001-P4),
/// run through the owner role, which bypasses RLS so the predicate is the only filter;</item>
/// <item>the P2 SQL fragment — <see cref="ScopeSql.ToSqlFragment"/> (009-P2), run through the owner
/// role with only the fragment in the <c>WHERE</c>, so the fragment is the only filter;</item>
/// <item>the RLS <c>USING</c> policy — fed by <see cref="ScopeSessionContext"/> (009-P3), run through
/// the least-privilege reader role with an unfiltered <c>SELECT</c>, so the policy is the only
/// filter.</item>
/// </list>
/// A fourth reading, the production door <see cref="ScopedConnection"/> (009-P4), which applies the
/// fragment <em>and</em> RLS together, must land on the same set. And a fifth, the original in-memory
/// <see cref="DataScopeSet.Admits"/> (001-P1), is the reference oracle every SQL encoding is measured
/// against — so a case where all the SQL encodings agree on the <em>wrong</em> answer is still caught.
/// <para>
/// Every case asserts the concrete id <em>set</em> returned, over an <b>adversarial</b> seed: two
/// tenants; the same user id, team, region and account values reused across both tenants; rows with
/// <c>NULL</c> scope columns; rows matching more than one grant at once; and a foreign-tenant row
/// that matches every grant on its columns and must never appear. A divergence between any two of the
/// five encodings on any case is exactly the drift this test exists to catch — it is a finding about
/// the merged P1/P2/P3/P4 code, not a reason to weaken the assertion.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopeRlsEquivalenceTests(PostgresFixture postgres)
{
    static ScopeRlsEquivalenceTests() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private readonly TenantId _tenant = TenantId.New();
    private readonly TenantId _otherTenant = TenantId.New();

    private readonly UserId _ivanov = UserId.New();
    private readonly UserId _petrova = UserId.New();
    private readonly UserId _sidorov = UserId.New();

    private readonly Guid _teamA = Guid.NewGuid();
    private readonly Guid _teamB = Guid.NewGuid();
    private readonly Guid _north = Guid.NewGuid();
    private readonly Guid _south = Guid.NewGuid();
    private readonly Guid _keyAccount = Guid.NewGuid();
    private readonly Guid _secondAccount = Guid.NewGuid();

    // --- Tenant T rows -------------------------------------------------------------------------
    // Two rows owned by ivanov with every scope column NULL: only Self(ivanov) or AllTenant reach
    // them, and a Self scope must return *both* — duplicate ownership.
    private readonly Guid _selfIvanov1 = Guid.NewGuid();
    private readonly Guid _selfIvanov2 = Guid.NewGuid();
    private readonly Guid _selfPetrova = Guid.NewGuid();   // owner petrova, all NULL
    private readonly Guid _teamA1 = Guid.NewGuid();         // team A
    private readonly Guid _teamA2 = Guid.NewGuid();         // team A too — overlapping membership
    private readonly Guid _teamB1 = Guid.NewGuid();         // team B
    private readonly Guid _regionNorth = Guid.NewGuid();    // region north
    private readonly Guid _teamARegionNorth = Guid.NewGuid(); // team A AND region north — two grants
    private readonly Guid _account = Guid.NewGuid();        // account key
    private readonly Guid _bare = Guid.NewGuid();           // owner sidorov, all NULL — AllTenant only

    // --- Other-tenant rows (must never appear for a tenant-T principal) -------------------------
    // Matches every scope value under test — only the tenant term keeps it out.
    private readonly Guid _foreignEverything = Guid.NewGuid();
    // Same owner id as the tenant-T principal, all NULL: proves the tenant term beats an owner match.
    private readonly Guid _foreignSelfIvanov = Guid.NewGuid();
    private readonly Guid _foreignTeamA = Guid.NewGuid();   // team A, in the other tenant

    private const string ProbeSql =
        "SELECT id, tenant_id, owner_user_id, team_id, region_id, account_id FROM scope_probe.rows";

    private static readonly ScopeColumns Columns = ScopeColumns.For("t");

    private ScopedRow[] SeededRows() =>
    [
        Row(_selfIvanov1, _tenant, _ivanov),
        Row(_selfIvanov2, _tenant, _ivanov),
        Row(_selfPetrova, _tenant, _petrova),
        Row(_teamA1, _tenant, _petrova, team: _teamA),
        Row(_teamA2, _tenant, _sidorov, team: _teamA),
        Row(_teamB1, _tenant, _sidorov, team: _teamB),
        Row(_regionNorth, _tenant, _petrova, region: _north),
        Row(_teamARegionNorth, _tenant, _sidorov, team: _teamA, region: _north),
        Row(_account, _tenant, _petrova, account: _keyAccount),
        Row(_bare, _tenant, _sidorov),
        // Adversarial cross-tenant rows: same ids/values as tenant T, different tenant.
        Row(_foreignEverything, _otherTenant, _ivanov, _teamA, _north, _keyAccount),
        Row(_foreignSelfIvanov, _otherTenant, _ivanov),
        Row(_foreignTeamA, _otherTenant, _petrova, team: _teamA),
    ];

    private Guid[] AllSeeded => SeededRows().Select(r => r.Id).ToArray();

    private static ScopedRow Row(
        Guid id,
        TenantId tenant,
        UserId owner,
        Guid? team = null,
        Guid? region = null,
        Guid? account = null) =>
        new()
        {
            Id = id,
            TenantId = tenant,
            OwnerUserId = owner,
            TeamId = team,
            RegionId = region,
            AccountId = account,
        };

    private DataScopeSet Scope(params DataScope[] scopes) => DataScopeSet.Of(_tenant, scopes);

    private async Task SeedAsync()
    {
        await using var db = postgres.CreateScopeProbeContext();
        db.Rows.AddRange(SeededRows());
        await db.SaveChangesAsync();
    }

    // === The differential harness ==============================================================

    // Asserts all five encodings of the scope rule select the identical id set (restricted to this
    // test's own seeded rows, since the container's probe table is shared across the collection).
    private async Task AssertAllEncodingsAgree(DataScopeSet scopes)
    {
        await SeedAsync();

        var inMemory = InMemoryAdmits(scopes);
        var ef = await EfWhereInScopeIdsAsync(scopes);
        var fragment = await FragmentOnlyIdsAsync(scopes);
        var rls = await RlsOnlyIdsAsync(scopes);
        var door = await ScopedConnectionIdsAsync(scopes);

        // Each SQL encoding measured against the 001-P1 in-memory reference, so agreement on a wrong
        // answer is impossible; then the four to each other, so any pairwise drift is named.
        Assert.Equal(inMemory, ef);
        Assert.Equal(inMemory, fragment);
        Assert.Equal(inMemory, rls);
        Assert.Equal(inMemory, door);

        // No foreign-tenant row ever leaks into any encoding.
        Assert.DoesNotContain(_foreignEverything, door);
        Assert.DoesNotContain(_foreignSelfIvanov, door);
        Assert.DoesNotContain(_foreignTeamA, door);
    }

    // Encoding 0: the in-memory reference oracle (001-P1).
    private Guid[] InMemoryAdmits(DataScopeSet scopes) =>
        SeededRows().Where(scopes.Admits).Select(r => r.Id).OrderBy(x => x).ToArray();

    // Encoding 1: the EF expression, owner role (RLS bypassed → the predicate is the only filter).
    private async Task<Guid[]> EfWhereInScopeIdsAsync(DataScopeSet scopes)
    {
        await using var db = postgres.CreateScopeProbeContext();
        var ids = await db.Rows.WhereInScope(scopes).Select(r => r.Id).ToListAsync();
        return Restrict(ids);
    }

    // Encoding 2: the P2 SQL fragment alone, owner role (RLS bypassed → the fragment is the only
    // filter). The caller query is wrapped exactly as the production door wraps it, but run as the
    // owner so nothing but the fragment can affect the result.
    private async Task<Guid[]> FragmentOnlyIdsAsync(DataScopeSet scopes)
    {
        var fragment = scopes.ToSqlFragment(Columns);
        var sql = $"SELECT t.id FROM ({ProbeSql}) AS t WHERE ({fragment.Sql})";

        await using var conn = new NpgsqlConnection(postgres.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in fragment.Parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return Restrict(await ReadIdsAsync(cmd));
    }

    // Encoding 3: the RLS policy alone, reader role, unfiltered SELECT (→ the policy is the only
    // filter). Session context is established from the same scope set, transaction-local.
    private async Task<Guid[]> RlsOnlyIdsAsync(DataScopeSet scopes)
    {
        await using var conn = new NpgsqlConnection(postgres.ReaderConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var session = ScopeSessionContext.Build(scopes);
        await using (var setCmd = new NpgsqlCommand(session.Sql, conn, tx))
        {
            foreach (var (name, value) in session.Parameters)
            {
                setCmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            await setCmd.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand("SELECT id FROM scope_probe.rows", conn, tx);
        return Restrict(await ReadIdsAsync(cmd));
    }

    // The production door (009-P4): fragment AND RLS, reader role. Must land where the isolated
    // encodings land.
    private async Task<Guid[]> ScopedConnectionIdsAsync(DataScopeSet scopes)
    {
        await using var reader = NpgsqlDataSource.Create(postgres.ReaderConnectionString);
        var sut = new ScopedConnection(reader, NullLogger<ScopedConnection>.Instance);
        var rows = await sut.QueryAsync<ProbeDto>(scopes, Columns, ProbeSql);
        return Restrict(rows.Select(r => r.Id));
    }

    private static async Task<List<Guid>> ReadIdsAsync(NpgsqlCommand cmd)
    {
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private Guid[] Restrict(IEnumerable<Guid> ids) =>
        ids.Intersect(AllSeeded).OrderBy(x => x).ToArray();

    // === The cases: every scope kind, the union, and the empty set =============================

    [Fact]
    public Task Self_scope_agrees_across_all_encodings() =>
        AssertAllEncodingsAgree(Scope(new DataScope.Self(_ivanov)));

    [Fact]
    public Task Team_scope_agrees_across_all_encodings() =>
        AssertAllEncodingsAgree(Scope(new DataScope.Team(_teamA)));

    [Fact]
    public Task Region_scope_agrees_across_all_encodings() =>
        AssertAllEncodingsAgree(Scope(new DataScope.Region(_north)));

    [Fact]
    public Task Account_scope_agrees_across_all_encodings() =>
        AssertAllEncodingsAgree(Scope(new DataScope.Account(_keyAccount)));

    [Fact]
    public Task All_tenant_scope_agrees_across_all_encodings() =>
        AssertAllEncodingsAgree(Scope(new DataScope.AllTenant()));

    [Fact]
    public Task Union_of_team_and_region_agrees_across_all_encodings() =>
        // Overlap under test: _teamARegionNorth satisfies both grants, so the union must dedup it.
        AssertAllEncodingsAgree(Scope(new DataScope.Team(_teamA), new DataScope.Region(_north)));

    [Fact]
    public Task Union_of_self_and_account_agrees_across_all_encodings() =>
        AssertAllEncodingsAgree(Scope(new DataScope.Self(_ivanov), new DataScope.Account(_keyAccount)));

    [Fact]
    public Task Overlapping_team_and_all_tenant_agrees_across_all_encodings() =>
        // AllTenant subsumes Team(A): the union must equal AllTenant, not double-count.
        AssertAllEncodingsAgree(Scope(new DataScope.Team(_teamA), new DataScope.AllTenant()));

    [Fact]
    public Task Empty_scope_set_returns_the_empty_set_in_every_encoding() =>
        // The DOMAIN.md §5.1 case, at the DBMS: every encoding must return nothing, not everything.
        AssertAllEncodingsAgree(DataScopeSet.None(_tenant));

    private sealed record ProbeDto(
        Guid Id,
        Guid TenantId,
        Guid OwnerUserId,
        Guid? TeamId,
        Guid? RegionId,
        Guid? AccountId);

    private sealed class NullLogger<T> : ILogger<T>
    {
        public static readonly NullLogger<T> Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
