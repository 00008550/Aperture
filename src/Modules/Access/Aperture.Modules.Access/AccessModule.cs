using Aperture.Modules.Access.Persistence;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.Extensions.DependencyInjection;

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
