using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Domain;

/// <summary>
/// A customer of the platform. Deliberately <b>not</b> <see cref="ITenantOwned"/>: it is the
/// tenant, so filtering it by itself is meaningless. Reading this table is an administrative
/// operation, gated by permission rather than by scope.
/// </summary>
public sealed class Tenant
{
    private Tenant()
    {
    }

    public Tenant(TenantId id, string name, string slug)
    {
        Id = id;
        Name = name;
        Slug = slug;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public TenantId Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Stable, URL-safe key. Unique across the whole platform.</summary>
    public string Slug { get; private set; } = string.Empty;

    public bool IsActive { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }
}
