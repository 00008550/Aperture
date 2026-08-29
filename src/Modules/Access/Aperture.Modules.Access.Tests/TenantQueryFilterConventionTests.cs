using Aperture.Modules.Access.Domain;
using Aperture.Modules.Access.Persistence;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Access.Tests;

/// <summary>
/// The load-bearing test of 001-P2. It builds the model and asserts, by enumeration, that no
/// tenant-owned entity escaped the tenant filter.
/// <para>
/// This runs without a database on purpose: a missing filter must fail in milliseconds on every
/// build, not only when someone writes an integration test for the one table that was missed.
/// </para>
/// </summary>
public sealed class TenantQueryFilterConventionTests
{
    private static AccessDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<AccessDbContext>()
            // Never opened — building the model needs a provider, not a server.
            .UseNpgsql("Host=127.0.0.1;Database=model-only")
            .Options;

        return new AccessDbContext(options, new FixedTenantContext(TenantId.New()));
    }

    [Fact]
    public void Every_tenant_owned_entity_has_a_query_filter()
    {
        using var context = BuildContext();
        var model = context.Model;

        var unfiltered = model.GetEntityTypes()
            .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType))
            .Where(e => e.GetDeclaredQueryFilters().Count == 0)
            .Select(e => e.ClrType.Name)
            .ToList();

        Assert.Empty(unfiltered);
    }

    [Fact]
    public void Every_entity_carrying_a_tenant_id_declares_ITenantOwned()
    {
        // The inverse direction, and the one that actually catches mistakes: an entity can be
        // given a TenantId property without implementing the marker, in which case the
        // convention silently skips it and the table is readable across tenants.
        using var context = BuildContext();

        var undeclared = context.Model.GetEntityTypes()
            .Where(e => e.FindProperty(nameof(ITenantOwned.TenantId)) is not null)
            .Where(e => !typeof(ITenantOwned).IsAssignableFrom(e.ClrType))
            .Select(e => e.ClrType.Name)
            .ToList();

        Assert.Empty(undeclared);
    }

    [Fact]
    public void The_convention_actually_covered_every_tenant_owned_type_in_the_assembly()
    {
        // Guards the guard: if an entity is never added to a DbSet or configuration it is not in
        // the model at all, so the first test passes vacuously for it.
        using var context = BuildContext();

        var mapped = context.Model.GetEntityTypes().Select(e => e.ClrType).ToHashSet();
        var missing = AccessDbContext.TenantOwnedTypes.Where(t => !mapped.Contains(t)).Select(t => t.Name);

        Assert.Empty(missing);
        Assert.NotEmpty(AccessDbContext.TenantOwnedTypes);
    }

    [Fact]
    public void Tenant_and_user_are_deliberately_not_tenant_filtered()
    {
        // Tenant *is* the tenant, and User is a global identity that may hold memberships in
        // several tenants (ARCHITECTURE.md §2). Filtering either would be wrong, so the absence
        // of a filter here is asserted rather than left to look like an oversight.
        using var context = BuildContext();

        Assert.Empty(context.Model.FindEntityType(typeof(Tenant))!.GetDeclaredQueryFilters());
        Assert.Empty(context.Model.FindEntityType(typeof(User))!.GetDeclaredQueryFilters());
    }

    private sealed class FixedTenantContext(TenantId tenantId) : ITenantContext
    {
        public bool HasTenant => true;

        public TenantId TenantId => tenantId;
    }
}
