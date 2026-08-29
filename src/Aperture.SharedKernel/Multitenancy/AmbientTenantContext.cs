namespace Aperture.SharedKernel.Multitenancy;

/// <summary>
/// <see cref="ITenantContext"/> backed by <see cref="AsyncLocal{T}"/>, so the tenant flows
/// with the logical call and never across concurrent ones.
/// <para>
/// Scopes nest and restore: disposing an inner scope puts the outer tenant back rather than
/// clearing it. Without that, a nested scope silently ends tenancy for the rest of the
/// request, and the next query runs unscoped.
/// </para>
/// </summary>
public sealed class AmbientTenantContext : ITenantContext
{
    private static readonly AsyncLocal<TenantId?> Ambient = new();

    public bool HasTenant => Ambient.Value.HasValue;

    public TenantId TenantId => Ambient.Value ?? throw new TenantContextMissingException();

    /// <summary>Establishes <paramref name="tenantId"/> until the returned scope is disposed.</summary>
    public static IDisposable Begin(TenantId tenantId) => new TenantScope(tenantId);

    private sealed class TenantScope : IDisposable
    {
        private readonly TenantId? _previous;
        private bool _disposed;

        public TenantScope(TenantId tenantId)
        {
            _previous = Ambient.Value;
            Ambient.Value = tenantId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = _previous;
        }
    }
}
