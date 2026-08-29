using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Aperture.Modules.Access.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without an application host.
/// <para>
/// The tenant context it supplies always throws, on purpose. Building the model only needs the
/// filter <em>expression</em>, never its value — so if a design-time command ever evaluates a
/// tenant, that is a bug in the model and it should surface as an exception rather than as a
/// migration generated against tenant zero.
/// </para>
/// </summary>
internal sealed class AccessDbContextFactory : IDesignTimeDbContextFactory<AccessDbContext>
{
    public AccessDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AccessDbContext>()
            .UseAccessNpgsql(
                Environment.GetEnvironmentVariable("APERTURE_DB")
                ?? "Host=localhost;Port=5433;Database=aperture;Username=aperture;Password=aperture")
            .Options;

        return new AccessDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public bool HasTenant => false;

        public TenantId TenantId => throw new TenantContextMissingException();
    }
}
