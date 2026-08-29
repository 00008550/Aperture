namespace Aperture.SharedKernel.Multitenancy;

/// <summary>
/// The tenant the current unit of work belongs to.
/// <para>
/// There is deliberately no default and no setter. A default tenant is how a background job
/// writes into the wrong customer's data, and a settable one is how a request talks itself
/// into another tenant. Reading <see cref="TenantId"/> without an established scope throws.
/// </para>
/// </summary>
public interface ITenantContext
{
    /// <summary>True when a tenant scope is established on this execution context.</summary>
    bool HasTenant { get; }

    /// <summary>
    /// The current tenant.
    /// </summary>
    /// <exception cref="TenantContextMissingException">No tenant scope is established.</exception>
    TenantId TenantId { get; }
}
