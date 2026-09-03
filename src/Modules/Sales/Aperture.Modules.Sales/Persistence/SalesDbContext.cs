using System.Linq.Expressions;
using Aperture.Modules.Sales.Domain;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Sales.Persistence;

/// <summary>
/// The Sales module's own context. It maps the <c>sales</c> schema and nothing else — a module owns a
/// schema and reaches others only through contracts (ARCHITECTURE.md §1).
/// <para>
/// The tenant query-filter convention is a verbatim copy of Access's (001-P2): a filter applied per
/// entity by hand is a filter that will eventually be forgotten on the one table where it matters, and
/// the omission returns too much rather than throwing. A convention test asserts it covered every
/// tenant-owned type.
/// </para>
/// </summary>
public sealed class SalesDbContext(DbContextOptions<SalesDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public const string Schema = "sales";

    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SalesDbContext).Assembly);

        ApplyTenantQueryFilters(modelBuilder);
    }

    /// <summary>
    /// Applies <c>WHERE tenant_id = @current</c> to every <see cref="ITenantOwned"/> entity, by
    /// convention. The filter closes over <c>this</c>, so it reads the tenant at query time rather than
    /// at model-build time — the model is cached per context type, and capturing a tenant into it would
    /// pin the first request's tenant for the process lifetime.
    /// </summary>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var tenantProperty = Expression.Property(parameter, nameof(ITenantOwned.TenantId));

            // this.CurrentTenantId — read per query, never captured by value.
            var currentTenant = Expression.Property(
                Expression.Constant(this),
                nameof(CurrentTenantId));

            var body = Expression.Equal(tenantProperty, currentTenant);
            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }

    /// <summary>
    /// The tenant every filtered query compares against. Public because the filter expression
    /// references it; not meant to be called directly.
    /// </summary>
    public TenantId CurrentTenantId => tenant.TenantId;

    /// <summary>
    /// Every tenant-owned CLR type in this module. The convention test uses it to assert the filter was
    /// applied to all of them. Empty until P2 introduces the first Sales aggregate — the convention and
    /// its test are in place first so no entity can land unfiltered.
    /// </summary>
    public static IReadOnlyCollection<Type> TenantOwnedTypes { get; } =
        typeof(SalesDbContext).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITenantOwned).IsAssignableFrom(t))
            .ToArray();
}
