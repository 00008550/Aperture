using Aperture.Modules.Access.Auditing;
using Aperture.Modules.Access.Authentication;
using Aperture.Modules.Access.Domain;
using Aperture.Modules.Access.Persistence;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Access.Tests;

/// <summary>
/// Integration tests for 001-P6, against a real PostgreSQL with the real migration applied.
/// Test names map onto the plan's <em>Tests</em> line for P6: a deny is audited, a mutation is
/// audited, and audit rows are tenant-scoped like everything else.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuditTrailTests(PostgresFixture postgres)
{
    private sealed class FixedTenant(TenantId tenantId) : ITenantContext
    {
        public bool HasTenant => true;

        public TenantId TenantId => tenantId;
    }

    private async Task<TenantId> NewTenantAsync(string label, bool isActive = true)
    {
        var tenant = TenantId.New();
        await using var db = postgres.CreateContext(tenant);
        var row = new Tenant(tenant, label, $"{label}-{Guid.NewGuid():N}"[..20]);
        if (!isActive)
        {
            typeof(Tenant).GetProperty("IsActive")!.SetValue(row, false);
        }

        db.Tenants.Add(row);
        await db.SaveChangesAsync();
        return tenant;
    }

    private IAuditTrail AuditFor(AccessDbContext db, TenantId tenant) =>
        new AuditTrail(db, new FixedTenant(tenant), TimeProvider.System);

    [Fact]
    public async Task A_deny_is_audited()
    {
        // A validly-resolved-then-refused caller: the tenant is active, but the user holds no
        // membership in it. That is exactly the deny path P3 plumbed a reason onto, and P6
        // attaches the audit row to.
        var tenant = await NewTenantAsync("deny");
        var userId = UserId.New();

        await using (var db = postgres.CreateContext(tenant))
        {
            var resolver = new AccessPrincipalResolver(db, AuditFor(db, tenant));
            var resolution = await resolver.ResolveAsync(tenant, userId, CancellationToken.None);

            Assert.False(resolution.IsGranted);
            Assert.Equal(AccessDenialReason.NoActiveMembership, resolution.Reason);
        }

        await using var read = postgres.CreateContext(tenant);
        var row = await read.AuditEvents.SingleAsync(e => e.ActorUserId == userId);

        Assert.Equal(AuditCategory.AuthenticationDenied, row.Category);
        Assert.Equal(ActorKind.Human, row.ActorKind);
        Assert.Equal(tenant, row.TenantId);
        Assert.Equal(nameof(AccessDenialReason.NoActiveMembership), row.Reason);
        // An authentication denial fails before any permission or scope is reached.
        Assert.Null(row.Permission);
        Assert.Null(row.ScopeDecision);
    }

    [Fact]
    public async Task A_mutation_is_audited_in_the_same_unit_of_work()
    {
        // Record() stages the audit row without saving, so the mutation and its record commit
        // together under one SaveChanges. Here a region is the stand-in mutation.
        var tenant = await NewTenantAsync("mutate");
        var userId = UserId.New();
        var regionId = Guid.NewGuid();

        await using (var db = postgres.CreateContext(tenant))
        {
            db.Regions.Add(new Region(regionId, tenant, $"region-{regionId:N}"[..12]));
            AuditFor(db, tenant).Record(
                new AuditEntry(AuditCategory.Mutation, ActorKind.Human, userId)
                {
                    Permission = "regions.write",
                    Action = "POST /api/regions",
                    ScopeDecision = "AllTenant",
                });

            await db.SaveChangesAsync();
        }

        await using var read = postgres.CreateContext(tenant);
        Assert.True(await read.Regions.AnyAsync(r => r.Id == regionId));

        var row = await read.AuditEvents.SingleAsync(e => e.ActorUserId == userId);
        Assert.Equal(AuditCategory.Mutation, row.Category);
        Assert.Equal("regions.write", row.Permission);
        Assert.Equal("POST /api/regions", row.Action);
        Assert.Null(row.Reason);
    }

    [Fact]
    public async Task A_mutation_and_its_audit_row_roll_back_together()
    {
        // The reason Record() stages rather than saves: a rolled-back mutation must leave no
        // audit row claiming it happened. Force the mutation to fail on save and assert neither
        // lands.
        var tenant = await NewTenantAsync("rollback");
        var userId = UserId.New();
        var duplicateName = $"region-{Guid.NewGuid():N}"[..12];

        await using (var seed = postgres.CreateContext(tenant))
        {
            seed.Regions.Add(new Region(Guid.NewGuid(), tenant, duplicateName));
            await seed.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext(tenant))
        {
            // Same (tenant, name) as the seeded region violates the unique index on save.
            db.Regions.Add(new Region(Guid.NewGuid(), tenant, duplicateName));
            AuditFor(db, tenant).Record(
                new AuditEntry(AuditCategory.Mutation, ActorKind.Human, userId)
                {
                    Action = "POST /api/regions",
                });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using var read = postgres.CreateContext(tenant);
        Assert.False(await read.AuditEvents.AnyAsync(e => e.ActorUserId == userId));
    }

    [Fact]
    public async Task Audit_rows_are_tenant_scoped_like_everything_else()
    {
        var acme = await NewTenantAsync("acme-audit");
        var globex = await NewTenantAsync("globex-audit");
        var userId = UserId.New();

        await using (var db = postgres.CreateContext(acme))
        {
            await AuditFor(db, acme).RecordAsync(
                new AuditEntry(AuditCategory.AuthorizationDenied, ActorKind.Human, userId)
                {
                    Permission = "deals.read",
                });
        }

        // The other tenant's reader, going through the same global query filter every other row
        // obeys, sees nothing of acme's trail.
        await using var globexView = postgres.CreateContext(globex);
        Assert.Empty(await globexView.AuditEvents.ToListAsync());
        Assert.False(await globexView.AuditEvents.AnyAsync(e => e.ActorUserId == userId));

        await using var acmeView = postgres.CreateContext(acme);
        Assert.Single(await acmeView.AuditEvents.Where(e => e.ActorUserId == userId).ToListAsync());
    }

    [Fact]
    public async Task An_assistants_call_is_marked_as_such()
    {
        // The distinction the audit trail must carry: a human and the assistant are different
        // accountable actors (ARCHITECTURE.md §9), and the row records which one.
        var tenant = await NewTenantAsync("assistant");
        var userId = UserId.New();

        await using (var db = postgres.CreateContext(tenant))
        {
            await AuditFor(db, tenant).RecordAsync(
                new AuditEntry(AuditCategory.Mutation, ActorKind.Assistant, userId)
                {
                    Permission = "orders.confirm",
                    Action = "assistant: confirm order",
                });
        }

        await using var read = postgres.CreateContext(tenant);
        var row = await read.AuditEvents.SingleAsync(e => e.ActorUserId == userId);
        Assert.Equal(ActorKind.Assistant, row.ActorKind);
    }

    [Fact]
    public async Task Recording_with_no_established_tenant_fails_closed()
    {
        // An audit row with no tenant is worse than none. The write must throw rather than land
        // unattributed — reusing the same fail-closed tenant context the rest of the module does.
        var tenant = await NewTenantAsync("notenant");

        await using var db = postgres.CreateContext(tenant);
        var audit = new AuditTrail(db, new ThrowingTenantContext(), TimeProvider.System);

        await Assert.ThrowsAsync<TenantContextMissingException>(() =>
            audit.RecordAsync(new AuditEntry(AuditCategory.Mutation, ActorKind.Human, UserId.New())));
    }

    private sealed class ThrowingTenantContext : ITenantContext
    {
        public bool HasTenant => false;

        public TenantId TenantId => throw new TenantContextMissingException();
    }
}
