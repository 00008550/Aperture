using Microsoft.Extensions.DependencyInjection;

namespace Aperture.Modules.Access;

/// <summary>
/// The module's single public surface. Everything else in this assembly is internal:
/// that is what makes the boundary in ARCHITECTURE.md §1 real rather than aspirational.
/// </summary>
public static class AccessModule
{
    public static IServiceCollection AddAccessModule(this IServiceCollection services)
    {
        // Registrations land with 001-P2 (schema) and 001-P3 (authentication).
        return services;
    }
}
