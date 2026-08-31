using Aperture.Modules.Access.Auditing;
using Aperture.Modules.Access.Authentication;
using Aperture.Modules.Access.Persistence;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aperture.Modules.Access;

/// <summary>
/// The module's single public surface. Everything else in this assembly is internal:
/// that is what makes the boundary in ARCHITECTURE.md §1 real rather than aspirational.
/// </summary>
public static class AccessModule
{
    public static IServiceCollection AddAccessModule(this IServiceCollection services, string connectionString)
    {
        services.TryAddTenantContext();

        services.AddDbContext<AccessDbContext>(options => options.UseAccessNpgsql(connectionString));

        // Scoped: it holds the request's DbContext. The resolver is the only way anything
        // outside this assembly learns what a user holds (001-P3).
        services.AddScoped<IAccessPrincipalResolver, AccessPrincipalResolver>();

        // Scoped: it writes through the request's DbContext, so a mutation and its audit row
        // share one unit of work (001-P6).
        services.AddScoped<IAuditTrail, AuditTrail>();

        // The clock the audit trail stamps rows with. TryAdd so a host that already registered a
        // TimeProvider — a test freezing time, say — keeps its own.
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    private static void TryAddTenantContext(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(ITenantContext)))
        {
            services.AddSingleton<ITenantContext, AmbientTenantContext>();
        }
    }
}
