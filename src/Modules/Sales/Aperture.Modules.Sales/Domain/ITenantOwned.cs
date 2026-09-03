using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Sales.Domain;

/// <summary>
/// Marks a row as belonging to exactly one tenant. Implementing this is what makes
/// <see cref="Persistence.SalesDbContext"/> apply the tenant query filter, and a convention test
/// fails the build if an entity carries a TenantId without declaring it here — the filter must not
/// depend on anyone remembering to add it.
/// <para>
/// This is the Sales module's own marker, deliberately not shared with Access: a module owns its
/// schema and its conventions and reaches others only through <c>Aperture.Contracts</c>
/// (ARCHITECTURE.md §1). A shared marker interface would be a cross-module coupling.
/// </para>
/// </summary>
public interface ITenantOwned
{
    TenantId TenantId { get; }
}
