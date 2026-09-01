using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Multitenancy;
using Npgsql;

namespace Aperture.Modules.Access.Tests;

/// <summary>
/// 009-P3: the row-level-security foundation, proven at the DBMS on a real PostgreSQL container.
/// <para>
/// This is where the <em>third</em> encoding of the scope rule — the RLS <c>USING</c> policy fed by
/// <see cref="ScopeSessionContext"/> — is pinned honest against the same intent as the EF predicate
/// (001-P4) and the P2 fragment. The point that makes it structural rather than by-convention: the
/// reads here run through the least-privilege <c>aperture_reader</c> role and the policy filters them
/// at the database, independent of the <c>SELECT</c> text. A connection with no session context set
/// returns nothing (fail-closed), and the owner role bypasses the policy entirely (blast radius
/// contained).
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopeRlsTests(PostgresFixture postgres)
{
    private readonly TenantId _tenant = TenantId.New();
    private readonly TenantId _otherTenant = TenantId.New();
    private readonly UserId _ivanov = UserId.New();
    private readonly UserId _petrova = UserId.New();
    private readonly Guid _teamA = Guid.NewGuid();
    private readonly Guid _north = Guid.NewGuid();
    private readonly Guid _keyAccount = Guid.NewGuid();

    // Seeded row ids, captured so each test asserts membership rather than a count over a table the
    // whole collection shares.
    private readonly Guid _self = Guid.NewGuid();      // tenant T, owner ivanov, all scope cols NULL
    private readonly Guid _team = Guid.NewGuid();      // tenant T, owner petrova, team A
    private readonly Guid _region = Guid.NewGuid();    // tenant T, owner petrova, region north
    private readonly Guid _account = Guid.NewGuid();   // tenant T, owner petrova, account key
    private readonly Guid _bare = Guid.NewGuid();      // tenant T, owner petrova, all scope cols NULL
    private readonly Guid _foreign = Guid.NewGuid();   // OTHER tenant, matches every grant on its cols

    private async Task SeedAsync()
    {
        await using var db = postgres.CreateScopeProbeContext();

        db.Rows.AddRange(
            Row(_self, _tenant, _ivanov),
            Row(_team, _tenant, _petrova, team: _teamA),
            Row(_region, _tenant, _petrova, region: _north),
            Row(_account, _tenant, _petrova, account: _keyAccount),
            Row(_bare, _tenant, _petrova),
            // The fail-open row: it carries every scope value under test, but in the other tenant.
            // Only the tenant term in the policy keeps it out — the guarantee edge 4 asserts.
            Row(_foreign, _otherTenant, _ivanov, _teamA, _north, _keyAccount));

        await db.SaveChangesAsync();
    }

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

    /// <summary>
    /// Reads <c>scope_probe.rows</c> as the reader role. When <paramref name="scopes"/> is supplied it
    /// establishes session context first via <see cref="ScopeSessionContext"/>; when it is
    /// <c>null</c> no context is set at all — the fail-closed case. The <c>SELECT</c> is deliberately
    /// unfiltered, so what comes back is what the RLS policy alone admitted.
    /// </summary>
    private async Task<HashSet<Guid>> ReadAsReaderAsync(DataScopeSet? scopes)
    {
        await using var conn = new NpgsqlConnection(postgres.ReaderConnectionString);
        await conn.OpenAsync();

        // A transaction so set_config(is_local => true) is scoped to this read and cannot leak across
        // pooled reuse — the same discipline the production wrapper uses.
        await using var tx = await conn.BeginTransactionAsync();

        if (scopes is not null)
        {
            var session = ScopeSessionContext.Build(scopes);
            await using var setCmd = new NpgsqlCommand(session.Sql, conn, tx);
            foreach (var (name, value) in session.Parameters)
            {
                setCmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            await setCmd.ExecuteNonQueryAsync();
        }

        return await SelectIdsAsync(conn, tx);
    }

    private static async Task<HashSet<Guid>> SelectIdsAsync(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        var ids = new HashSet<Guid>();
        await using var cmd = new NpgsqlCommand("SELECT id FROM scope_probe.rows", conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private DataScopeSet Scope(params DataScope[] scopes) => DataScopeSet.Of(_tenant, scopes);

    [Fact]
    public async Task Reader_with_no_session_context_sees_zero_rows_though_rows_exist()
    {
        await SeedAsync();

        // Edge 17. The catastrophe this whole plan exists to prevent — a misconfigured reader
        // connection returning everything — is instead a reader connection returning nothing.
        var ids = await ReadAsReaderAsync(scopes: null);

        Assert.DoesNotContain(_self, ids);
        Assert.DoesNotContain(_team, ids);
        Assert.DoesNotContain(_bare, ids);
        Assert.DoesNotContain(_foreign, ids);
    }

    [Fact]
    public async Task Self_scope_admits_only_the_principals_own_row()
    {
        await SeedAsync();

        var ids = await ReadAsReaderAsync(Scope(new DataScope.Self(_ivanov)));

        Assert.Equal(new[] { _self }, ids.Intersect(AllSeeded).OrderBy(x => x));
    }

    [Fact]
    public async Task Team_scope_admits_the_team_row_and_excludes_null_teams()
    {
        await SeedAsync();

        // Edge 5: _self and _bare have team_id IS NULL and must be absent; _foreign has team A but
        // sits in the other tenant and must be absent too (edge 4).
        var ids = await ReadAsReaderAsync(Scope(new DataScope.Team(_teamA)));

        Assert.Equal(new[] { _team }, ids.Intersect(AllSeeded).OrderBy(x => x));
    }

    [Fact]
    public async Task Region_scope_admits_the_region_row_and_excludes_null_regions()
    {
        await SeedAsync();

        var ids = await ReadAsReaderAsync(Scope(new DataScope.Region(_north)));

        Assert.Equal(new[] { _region }, ids.Intersect(AllSeeded).OrderBy(x => x));
    }

    [Fact]
    public async Task Account_scope_admits_the_account_row_and_excludes_null_accounts()
    {
        await SeedAsync();

        var ids = await ReadAsReaderAsync(Scope(new DataScope.Account(_keyAccount)));

        Assert.Equal(new[] { _account }, ids.Intersect(AllSeeded).OrderBy(x => x));
    }

    [Fact]
    public async Task All_tenant_scope_admits_every_row_in_the_tenant_and_no_other_tenants()
    {
        await SeedAsync();

        // Edge 4: AllTenant means everything in *this* tenant, never everything. _foreign carries
        // every scope value but belongs to the other tenant, so it stays out.
        var ids = await ReadAsReaderAsync(Scope(new DataScope.AllTenant()));

        Assert.Equal(
            new[] { _self, _team, _region, _account, _bare }.OrderBy(x => x),
            ids.Intersect(AllSeeded).OrderBy(x => x));
        Assert.DoesNotContain(_foreign, ids);
    }

    [Fact]
    public async Task The_union_of_two_scopes_admits_rows_matching_either()
    {
        await SeedAsync();

        var ids = await ReadAsReaderAsync(
            Scope(new DataScope.Team(_teamA), new DataScope.Region(_north)));

        // Union, not intersection: the team row and the region row, nothing else.
        Assert.Equal(
            new[] { _team, _region }.OrderBy(x => x),
            ids.Intersect(AllSeeded).OrderBy(x => x));
    }

    [Fact]
    public async Task The_empty_scope_set_admits_nothing_even_with_the_tenant_set()
    {
        await SeedAsync();

        // Edge 3, execution half: tenant context is established but no grant is, so the policy's
        // grant union is false for every row — zero rows, decided at the DBMS.
        var ids = await ReadAsReaderAsync(DataScopeSet.None(_tenant));

        Assert.Empty(ids.Intersect(AllSeeded));
    }

    [Fact]
    public async Task Owner_role_bypasses_the_policy_and_forces_is_off()
    {
        await SeedAsync();

        // Edge 18: the owner role (EF/migrations) is not subject to RLS, so the very read that
        // returns nothing for the reader-with-no-context returns every seeded row here — including
        // the other tenant's. Enabling a policy therefore never changes EF behaviour.
        await using var conn = new NpgsqlConnection(postgres.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var ids = await SelectIdsAsync(conn, tx);
        await tx.RollbackAsync();

        Assert.All(AllSeeded, id => Assert.Contains(id, ids));

        // And the reason it bypasses is that FORCE is deliberately off. Assert it, so a future
        // FORCE ROW LEVEL SECURITY added by accident is caught here rather than in production.
        Assert.True(await RowSecurityEnabledAsync());
        Assert.False(await ForceRowSecurityAsync());
    }

    private async Task<bool> RowSecurityEnabledAsync() => await RelBoolAsync("relrowsecurity");

    private async Task<bool> ForceRowSecurityAsync() => await RelBoolAsync("relforcerowsecurity");

    private async Task<bool> RelBoolAsync(string column)
    {
        await using var conn = new NpgsqlConnection(postgres.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {column} FROM pg_class WHERE oid = 'scope_probe.rows'::regclass;", conn);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private IReadOnlyCollection<Guid> AllSeeded =>
        new[] { _self, _team, _region, _account, _bare, _foreign };
}
