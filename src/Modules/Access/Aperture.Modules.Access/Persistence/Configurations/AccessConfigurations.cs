using Aperture.Modules.Access.Domain;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aperture.Modules.Access.Persistence.Configurations;

/// <summary>
/// Converters for the typed ids. Stored as plain <c>uuid</c>, so the strong typing costs
/// nothing in the database and the columns stay joinable from psql.
/// </summary>
internal static class TypedIdConverters
{
    public static readonly ValueConverter<TenantId, Guid> Tenant =
        new(id => id.Value, value => new TenantId(value));

    public static readonly ValueConverter<UserId, Guid> User =
        new(id => id.Value, value => new UserId(value));
}

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants", AccessDbContext.Schema);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").HasConversion(TypedIdConverters.Tenant);
        builder.Property(t => t.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(t => t.Slug).HasColumnName("slug").HasMaxLength(64).IsRequired();
        builder.Property(t => t.IsActive).HasColumnName("is_active");
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.HasIndex(t => t.Slug).IsUnique();
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", AccessDbContext.Schema);
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id").HasConversion(TypedIdConverters.User);
        builder.Property(u => u.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
        builder.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(u => u.IsActive).HasColumnName("is_active");
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");

        // Email is the login, so uniqueness is platform-wide, not per tenant. Lower-cased on
        // the way in; the index is over the stored value rather than an expression so it can
        // serve equality lookups directly.
        builder.HasIndex(u => u.Email).IsUnique();
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.ToTable("memberships", AccessDbContext.Schema);
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id");
        builder.Property(m => m.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        builder.Property(m => m.UserId).HasColumnName("user_id").HasConversion(TypedIdConverters.User);
        builder.Property(m => m.IsActive).HasColumnName("is_active");
        builder.Property(m => m.CreatedAt).HasColumnName("created_at");

        // One membership per user per tenant. Without this, a double-provisioning gives a user
        // two role sets in one tenant and the effective permissions become order-dependent.
        builder.HasIndex(m => new { m.TenantId, m.UserId }).IsUnique();

        builder.HasMany(m => m.Roles).WithOne().HasForeignKey(r => r.MembershipId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(m => m.ScopeGrants).WithOne().HasForeignKey(g => g.MembershipId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles", AccessDbContext.Schema);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();

        builder.HasMany(r => r.Permissions).WithOne().HasForeignKey(p => p.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions", AccessDbContext.Schema);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id");
        builder.Property(p => p.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        builder.Property(p => p.RoleId).HasColumnName("role_id");
        builder.Property(p => p.Permission).HasColumnName("permission").HasMaxLength(100).IsRequired();

        // Granting the same permission twice must be a no-op, not two rows to reconcile later.
        builder.HasIndex(p => new { p.RoleId, p.Permission }).IsUnique();
    }
}

internal sealed class MembershipRoleConfiguration : IEntityTypeConfiguration<MembershipRole>
{
    public void Configure(EntityTypeBuilder<MembershipRole> builder)
    {
        builder.ToTable("membership_roles", AccessDbContext.Schema);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        builder.Property(r => r.MembershipId).HasColumnName("membership_id");
        builder.Property(r => r.RoleId).HasColumnName("role_id");
        builder.HasIndex(r => new { r.MembershipId, r.RoleId }).IsUnique();

        builder.HasOne<Role>().WithMany().HasForeignKey(r => r.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ScopeGrantConfiguration : IEntityTypeConfiguration<ScopeGrant>
{
    public void Configure(EntityTypeBuilder<ScopeGrant> builder)
    {
        builder.ToTable("scope_grants", AccessDbContext.Schema, t =>
            // The constructor enforces this too. Both, because the constructor protects the
            // application path and the constraint protects every other path — a seed script, a
            // data fix, a future bulk import.
            t.HasCheckConstraint(
                "ck_scope_grants_target",
                """
                (kind IN (1, 5) AND target_id IS NULL)
                OR (kind IN (2, 3, 4) AND target_id IS NOT NULL)
                """));

        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).HasColumnName("id");
        builder.Property(g => g.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        builder.Property(g => g.MembershipId).HasColumnName("membership_id");
        builder.Property(g => g.Kind).HasColumnName("kind").HasConversion<int>();
        builder.Property(g => g.TargetId).HasColumnName("target_id");

        // Resolving a principal's scopes is the hottest read on this table: every request does
        // it once. The index covers that lookup exactly.
        builder.HasIndex(g => new { g.TenantId, g.MembershipId });
        builder.HasIndex(g => new { g.MembershipId, g.Kind, g.TargetId }).IsUnique();
    }
}

internal sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("teams", AccessDbContext.Schema);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        builder.Property(t => t.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.HasIndex(t => new { t.TenantId, t.Name }).IsUnique();
    }
}

internal sealed class RegionConfiguration : IEntityTypeConfiguration<Region>
{
    public void Configure(EntityTypeBuilder<Region> builder)
    {
        builder.ToTable("regions", AccessDbContext.Schema);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();
    }
}
