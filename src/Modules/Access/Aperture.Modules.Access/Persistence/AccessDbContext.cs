using System.Linq.Expressions;
using Aperture.Modules.Access.Domain;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Access.Persistence;

/// <summary>
/// The Access module's own context. It maps the <c>access</c> schema and nothing else —
/// a module owns a schema and reaches others only through contracts (ARCHITECTURE.md §1).
/// </summary>
public sealed class AccessDbContext(DbContextOptions<AccessDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public const string Schema = "access";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<MembershipRole> MembershipRoles => Set<MembershipRole>();

    public DbSet<ScopeGrant> ScopeGrants => Set<ScopeGrant>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<Region> Regions => Set<Region>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AccessDbContext).Assembly);

        ApplyTenantQueryFilters(modelBuilder);
    }

    /// <summary>
    /// Applies <c>WHERE tenant_id = @current</c> to every <see cref="ITenantOwned"/> entity, by
    /// convention.
    /// <para>
    /// Deliberately not written per entity. A filter that each configuration must remember to add
    /// is a filter that will eventually be forgotten on the one table where it matters, and the
    /// omission is invisible in review — the query still works, it just returns too much.
    /// A convention test asserts this covered every tenant-owned type.
    /// </para>
    /// <para>
    /// The filter closes over <c>this</c>, so it reads the tenant at query time rather than at
    /// model-build time. The model is cached per context type; capturing a tenant into it would
    /// pin the first request's tenant for the lifetime of the process.
    /// </para>
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
    /// Every tenant-owned CLR type in this module. The convention test uses it to assert the
    /// filter was applied to all of them.
    /// </summary>
    public static IReadOnlyCollection<Type> TenantOwnedTypes { get; } =
        typeof(AccessDbContext).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITenantOwned).IsAssignableFrom(t))
            .ToArray();
}
