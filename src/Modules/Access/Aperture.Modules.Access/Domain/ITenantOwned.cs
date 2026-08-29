using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Domain;

/// <summary>
/// Marks a row as belonging to exactly one tenant. Implementing this is what makes
/// <see cref="Persistence.AccessDbContext"/> apply the tenant query filter, and a convention
/// test fails the build if an entity carries a TenantId without declaring it here — the
/// filter must not depend on anyone remembering to add it.
/// </summary>
public interface ITenantOwned
{
    TenantId TenantId { get; }
}
