using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Api.Authentication;

/// <summary>
/// Establishes the ambient tenant for the rest of the request, from the resolved principal and
/// from nothing else.
/// <para>
/// It has to be its own middleware rather than part of token validation: the authentication
/// handler's execution context does not flow forward, so an <see cref="AsyncLocal{T}"/> set
/// there is gone by the time an endpoint runs.
/// </para>
/// <para>
/// A request with no principal gets <em>no</em> tenant, so anything tenant-scoped it touches
/// throws <see cref="TenantContextMissingException"/>. That is the intended outcome — the
/// alternative is a default tenant, which is how work lands in the wrong customer's data
/// (ARCHITECTURE.md §2).
/// </para>
/// </summary>
public sealed class TenantScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var principal = context.FindAccessPrincipal();
        if (principal is null)
        {
            await next(context);
            return;
        }

        using (AmbientTenantContext.Begin(principal.TenantId))
        {
            await next(context);
        }
    }
}
