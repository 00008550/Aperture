using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Aperture.Modules.Sales;

/// <summary>
/// The module's single public surface. Everything else in this assembly is internal: that is what
/// makes the boundary in ARCHITECTURE.md §1 real rather than aspirational.
/// <para>
/// P1 registers the <see cref="SalesDbContext"/> only — the empty <c>sales</c> schema and its tenant
/// query-filter convention. Aggregates, application services and endpoints arrive in later portions.
/// </para>
/// </summary>
public static class SalesModule
{
    public static IServiceCollection AddSalesModule(this IServiceCollection services, string connectionString)
    {
        services.TryAddTenantContext();

        services.AddDbContext<SalesDbContext>(options => options.UseSalesNpgsql(connectionString));

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
