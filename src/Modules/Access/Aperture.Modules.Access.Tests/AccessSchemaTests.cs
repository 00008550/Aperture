using Aperture.Modules.Access.Domain;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aperture.Modules.Access.Tests;

/// <summary>
/// Integration tests for 001-P2, against a real PostgreSQL with the real migration applied.
/// Test names map onto the plan's <em>Tests</em> line for P2.
/// <para>
/// Every test mints its own tenant. The container is shared for speed, so any fixed tenant id
/// would be shared mutable state across tests — which it briefly was, and the second test to
/// insert the tenant row failed on the primary key.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AccessSchemaTests(PostgresFixture postgres)
{
    private async Task<TenantId> NewTenantAsync(string label)
    {
        var tenant = TenantId.New();
        await using var db = postgres.CreateContext(tenant);
        db.Tenants.Add(new Tenant(tenant, label, $"{label}-{Guid.NewGuid():N}"[..20]));
        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task<(Guid MembershipId, UserId UserId)> SeedMembershipAsync(TenantId tenant)
    {
        await using var db = postgres.CreateContext(tenant);

        var userId = UserId.New();
        db.Users.Add(new User(userId, $"{Guid.NewGuid():N}@example.com", "Test User"));

        var membershipId = Guid.NewGuid();
        db.Memberships.Add(new Membership(membershipId, tenant, userId));

        await db.SaveChangesAsync();
        return (membershipId, userId);
    }

    [Fact]
    public async Task The_migration_creates_the_access_schema_and_its_history_table()
    {
        await using var db = postgres.CreateContext(TenantId.New());

        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());

        // The history table must be the module's own, not the shared default. This is the
        // regression test for the design-time / runtime mismatch found while building P2.
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'access' AND table_name = '__migrations'
            """,
            connection);

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_membership_round_trips()
    {
        var tenant = await NewTenantAsync("roundtrip");
        var (membershipId, userId) = await SeedMembershipAsync(tenant);

        await using var db = postgres.CreateContext(tenant);
        var loaded = await db.Memberships.SingleAsync(m => m.Id == membershipId);

        Assert.Equal(tenant, loaded.TenantId);
        Assert.Equal(userId, loaded.UserId);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task A_read_in_one_tenant_cannot_see_another_tenants_rows()
    {
        var acme = await NewTenantAsync("acme");
        var globex = await NewTenantAsync("globex");
        var (acmeMembership, _) = await SeedMembershipAsync(acme);

        await using var globexView = postgres.CreateContext(globex);

        Assert.False(await globexView.Memberships.AnyAsync(m => m.Id == acmeMembership));
        Assert.Null(await globexView.Memberships.SingleOrDefaultAsync(m => m.Id == acmeMembership));
        Assert.DoesNotContain(await globexView.Memberships.ToListAsync(), m => m.TenantId == acme);
    }

    [Fact]
    public void The_tenant_filter_is_applied_in_SQL_not_in_memory()
    {
        // A result-count assertion passes just as well against an in-memory filter, which would
        // still have pulled every tenant's rows over the wire. Assert the predicate reached SQL.
        using var db = postgres.CreateContext(TenantId.New());

        var sql = db.Memberships.ToQueryString();

        Assert.Contains("tenant_id", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_scope_grant_that_needs_a_target_is_rejected_by_the_database()
    {
        var tenant = await NewTenantAsync("checkc");
        var (membershipId, _) = await SeedMembershipAsync(tenant);

        // Bypasses the constructor deliberately: the point is that the check constraint holds
        // for every path into the table, including the ones that are not this application.
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO access.scope_grants (id, tenant_id, membership_id, kind, target_id)
            VALUES (@id, @tenant, @membership, 2, NULL)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("membership", membershipId);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23514", error.SqlState); // check_violation
        Assert.Contains("ck_scope_grants_target", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scope_grant_round_trips_into_the_in_memory_scope_model()
    {
        var tenant = await NewTenantAsync("scopes");
        var (membershipId, userId) = await SeedMembershipAsync(tenant);
        var teamId = Guid.NewGuid();

        await using (var write = postgres.CreateContext(tenant))
        {
            write.Teams.Add(new Team(teamId, tenant, $"team-{teamId:N}"[..12]));
            write.ScopeGrants.Add(
                new ScopeGrant(Guid.NewGuid(), tenant, membershipId, ScopeGrantKind.Team, teamId));
            write.ScopeGrants.Add(
                new ScopeGrant(Guid.NewGuid(), tenant, membershipId, ScopeGrantKind.Self, null));
            await write.SaveChangesAsync();
        }

        await using var db = postgres.CreateContext(tenant);
        var grants = await db.ScopeGrants.Where(g => g.MembershipId == membershipId).ToListAsync();

        var scopes = DataScopeSet.Of(tenant, grants.Select(g => g.ToDataScope(userId)));

        Assert.Equal(2, scopes.Count);
        Assert.Contains(new DataScope.Team(teamId), scopes.Scopes);
        Assert.Contains(new DataScope.Self(userId), scopes.Scopes);
    }

    [Fact]
    public async Task A_user_cannot_hold_two_memberships_in_one_tenant()
    {
        var tenant = await NewTenantAsync("dupmem");
        var (_, userId) = await SeedMembershipAsync(tenant);

        await using var db = postgres.CreateContext(tenant);
        db.Memberships.Add(new Membership(Guid.NewGuid(), tenant, userId));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", ((PostgresException)error.InnerException!).SqlState); // unique_violation
    }

    [Fact]
    public async Task One_user_may_hold_memberships_in_several_tenants()
    {
        // The assumption recorded as open question 3 in the plan, made testable: identity is
        // global, authorization is per membership.
        var first = await NewTenantAsync("multi-a");
        var second = await NewTenantAsync("multi-b");
        var (_, userId) = await SeedMembershipAsync(first);

        await using (var other = postgres.CreateContext(second))
        {
            other.Memberships.Add(new Membership(Guid.NewGuid(), second, userId));
            await other.SaveChangesAsync();
        }

        // The same identity, two memberships — and each tenant sees exactly one of them.
        await using var firstView = postgres.CreateContext(first);
        Assert.Single(await firstView.Memberships.Where(m => m.UserId == userId).ToListAsync());

        await using var secondView = postgres.CreateContext(second);
        Assert.Single(await secondView.Memberships.Where(m => m.UserId == userId).ToListAsync());
    }

    [Fact]
    public void An_undeclared_permission_cannot_be_granted_to_a_role()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new RolePermission(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), "deals.delete"));

        Assert.Contains("not a declared permission", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_permission_is_granted_at_most_once_per_role()
    {
        var tenant = await NewTenantAsync("dupperm");

        await using var db = postgres.CreateContext(tenant);
        var roleId = Guid.NewGuid();
        db.Roles.Add(new Role(roleId, tenant, $"role-{Guid.NewGuid():N}"[..12]));
        db.RolePermissions.Add(new RolePermission(Guid.NewGuid(), tenant, roleId, Permissions.DealsRead));
        db.RolePermissions.Add(new RolePermission(Guid.NewGuid(), tenant, roleId, Permissions.DealsRead));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", ((PostgresException)error.InnerException!).SqlState);
    }
}
