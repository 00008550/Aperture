using Aperture.Modules.Sales.Domain;
using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Sales.Tests;

/// <summary>
/// The Sales copy of Access's load-bearing convention test (001-P2). It builds the model and asserts,
/// by enumeration, that no tenant-owned entity escaped the tenant filter — and, in the inverse
/// direction, that no entity carries a <c>tenant_id</c> without declaring <see cref="ITenantOwned"/>,
/// which would let the convention silently skip it.
/// <para>
/// In P1 the Sales model has no entities yet, so the enumerations are trivially empty. That is
/// deliberate: the convention and its guard are in place <em>before</em> the first aggregate lands
/// (P2), so no Sales entity can ever be added unfiltered. P2 tightens this by adding an entity and
/// re-running it.
/// </para>
/// </summary>
public sealed class TenantQueryFilterConventionTests
{
    private static SalesDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            // Never opened — building the model needs a provider, not a server.
            .UseNpgsql("Host=127.0.0.1;Database=model-only")
            .Options;

        return new SalesDbContext(options, new FixedTenantContext(TenantId.New()));
    }

    [Fact]
    public void Every_tenant_owned_entity_has_a_query_filter()
    {
        using var context = BuildContext();

        var unfiltered = context.Model.GetEntityTypes()
            .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType))
            .Where(e => e.GetDeclaredQueryFilters().Count == 0)
            .Select(e => e.ClrType.Name)
            .ToList();

        Assert.Empty(unfiltered);
    }

    [Fact]
    public void Every_entity_carrying_a_tenant_id_declares_ITenantOwned()
    {
        // The inverse direction, and the one that actually catches mistakes: an entity given a
        // TenantId property without the marker is silently skipped by the convention and becomes
        // readable across tenants.
        using var context = BuildContext();

        var undeclared = context.Model.GetEntityTypes()
            .Where(e => e.FindProperty(nameof(ITenantOwned.TenantId)) is not null)
            .Where(e => !typeof(ITenantOwned).IsAssignableFrom(e.ClrType))
            .Select(e => e.ClrType.Name)
            .ToList();

        Assert.Empty(undeclared);
    }

    [Fact]
    public void The_convention_covered_every_tenant_owned_type_in_the_assembly()
    {
        // Guards the guard: an entity never added to a DbSet or configuration is not in the model at
        // all, so the first test passes vacuously for it. In P1 both sets are empty — the assertion is
        // that they AGREE (every declared tenant-owned type is mapped), which holds trivially now and
        // becomes load-bearing the moment P2 adds Account.
        using var context = BuildContext();

        var mapped = context.Model.GetEntityTypes().Select(e => e.ClrType).ToHashSet();
        var missing = SalesDbContext.TenantOwnedTypes.Where(t => !mapped.Contains(t)).Select(t => t.Name);

        Assert.Empty(missing);
    }

    private sealed class FixedTenantContext(TenantId tenantId) : ITenantContext
    {
        public bool HasTenant => true;

        public TenantId TenantId => tenantId;
    }
}
