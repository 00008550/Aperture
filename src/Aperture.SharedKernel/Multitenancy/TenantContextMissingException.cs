namespace Aperture.SharedKernel.Multitenancy;

/// <summary>
/// Thrown when tenant-scoped work runs without a tenant. This is a loud failure on purpose:
/// the alternative — a default tenant — corrupts data silently and is not detectable later.
/// </summary>
public sealed class TenantContextMissingException() : InvalidOperationException(
    "No tenant scope is established on this execution context. " +
    "Tenant-scoped work must run inside AmbientTenantContext.Begin(tenantId).");
