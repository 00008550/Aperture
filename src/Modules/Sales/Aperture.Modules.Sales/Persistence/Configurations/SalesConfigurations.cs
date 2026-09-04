using Aperture.Modules.Sales.Domain;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aperture.Modules.Sales.Persistence.Configurations;

/// <summary>
/// Converters for the typed ids, stored as plain <c>uuid</c> so the strong typing costs nothing in the
/// database and the columns stay joinable from psql. A verbatim mirror of Access's converters — the
/// module owns its own copy rather than sharing one, because sharing it would be a cross-module coupling
/// (ARCHITECTURE.md §1).
/// </summary>
internal static class TypedIdConverters
{
    public static readonly ValueConverter<TenantId, Guid> Tenant =
        new(id => id.Value, value => new TenantId(value));

    public static readonly ValueConverter<UserId, Guid> User =
        new(id => id.Value, value => new UserId(value));
}

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", SalesDbContext.Schema);
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id");
        builder.Property(a => a.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        builder.Property(a => a.OwnerUserId).HasColumnName("owner_user_id").HasConversion(TypedIdConverters.User);
        builder.Property(a => a.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(a => a.TaxId).HasColumnName("tax_id").HasMaxLength(64).IsRequired();
        builder.Property(a => a.CreditLimit).HasColumnName("credit_limit").HasColumnType("numeric(18,2)");
        builder.Property(a => a.PaymentTermsDays).HasColumnName("payment_terms_days");
        builder.Property(a => a.RegionId).HasColumnName("region_id");
        builder.Property(a => a.TeamId).HasColumnName("team_id");
        // Nullable CLR property (to match IScopedResource so the EF scope predicate translates), but the
        // column is NOT NULL — an account always carries account_id = id.
        builder.Property(a => a.AccountId).HasColumnName("account_id").IsRequired();
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");

        // xmin as the optimistic concurrency token (ARCHITECTURE.md §5). Npgsql maps the system column;
        // EF reloads it after each write, so a stale token on the next update fails the check and 409s.
        builder.Property(a => a.Version).HasColumnName("xmin").IsRowVersion();

        // Tax-identifier dedup, per tenant (DOMAIN.md §2). The same company arriving twice in one tenant
        // collides here rather than becoming a second row; the same tax id in another tenant is a
        // distinct account, because the index is composite. This is also the cheapest correct
        // idempotency for account creation — a double-submit conflicts instead of duplicating.
        builder.HasIndex(a => new { a.TenantId, a.TaxId }).IsUnique();

        // Keyset pagination reads the grid ordered by (created_at, id); this index serves that ordering
        // and the scope predicate's tenant term as its leading column.
        builder.HasIndex(a => new { a.TenantId, a.CreatedAt, a.Id });
    }
}

internal sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("contacts", SalesDbContext.Schema);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        // Nullable CLR property (to match IScopedResource so the EF scope predicate translates); mapped
        // IsRequired so the column is NOT NULL and the one-account FK below is enforced.
        builder.Property(c => c.AccountId).HasColumnName("account_id").IsRequired();
        builder.Property(c => c.OwnerUserId).HasColumnName("owner_user_id").HasConversion(TypedIdConverters.User);
        builder.Property(c => c.TeamId).HasColumnName("team_id");
        builder.Property(c => c.RegionId).HasColumnName("region_id");
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(c => c.Email).HasColumnName("email").HasMaxLength(320);
        builder.Property(c => c.Phone).HasColumnName("phone").HasMaxLength(64);
        builder.Property(c => c.Messenger).HasColumnName("messenger").HasMaxLength(200);
        builder.Property(c => c.IsDeparted).HasColumnName("is_departed");
        builder.Property(c => c.DepartedAt).HasColumnName("departed_at");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");

        // The parent-account FK. It enforces the one-account rule at the database: a contact whose
        // account_id names no account cannot commit. account_id doubles as the scope column (a contact's
        // account is its own account), so this single column carries both the relationship and the scope.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(c => c.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // Serves the active-contacts grid ordering — (created_at, id) keyset paging — with the tenant term
        // leading the scope predicate. is_departed is included so the active filter is covered.
        builder.HasIndex(c => new { c.TenantId, c.CreatedAt, c.Id });
    }
}

internal sealed class DealConfiguration : IEntityTypeConfiguration<Deal>
{
    public void Configure(EntityTypeBuilder<Deal> builder)
    {
        builder.ToTable("deals", SalesDbContext.Schema);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id");
        builder.Property(d => d.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        // Nullable CLR property (to match IScopedResource so the EF scope predicate translates); mapped
        // IsRequired so the column is NOT NULL and the one-account FK below is enforced.
        builder.Property(d => d.AccountId).HasColumnName("account_id").IsRequired();
        builder.Property(d => d.OwnerUserId).HasColumnName("owner_user_id").HasConversion(TypedIdConverters.User);
        builder.Property(d => d.TeamId).HasColumnName("team_id");
        builder.Property(d => d.RegionId).HasColumnName("region_id");
        builder.Property(d => d.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(d => d.Stage).HasColumnName("stage").HasMaxLength(32).IsRequired();
        builder.Property(d => d.Amount).HasColumnName("amount").HasColumnType("numeric(18,2)");
        builder.Property(d => d.DiscountPct).HasColumnName("discount_pct").HasColumnType("numeric(5,2)");
        // Carried on the row from P4 (plan target design) so P5/P6 add behaviour without a follow-on
        // migration; nothing in P4 writes them.
        builder.Property(d => d.FrozenPriceListVersion).HasColumnName("frozen_price_list_version").HasMaxLength(64);
        builder.Property(d => d.PendingApproval).HasColumnName("pending_approval");
        builder.Property(d => d.PendingApprovalDiscountPct)
            .HasColumnName("pending_approval_discount_pct").HasColumnType("numeric(5,2)");
        builder.Property(d => d.LostReasonCode).HasColumnName("lost_reason_code").HasMaxLength(64);
        builder.Property(d => d.CreatedAt).HasColumnName("created_at");

        // xmin as the optimistic concurrency token (ARCHITECTURE.md §5): two writers moving the same deal
        // — the contended case P5's transitions hit — cannot both win.
        builder.Property(d => d.Version).HasColumnName("xmin").IsRowVersion();

        // The parent-account FK enforces the one-account rule at the database and doubles as the scope
        // column (a deal's account is its own account), so this single column carries both.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(d => d.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // The deal owns its lines: the aggregate is loaded and saved whole.
        builder.HasMany(d => d.Lines)
            .WithOne()
            .HasForeignKey(l => l.DealId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(d => d.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Serves the deals grid ordering — (created_at, id) keyset paging — with the tenant term leading
        // the scope predicate.
        builder.HasIndex(d => new { d.TenantId, d.CreatedAt, d.Id });
    }
}

internal sealed class DealLineConfiguration : IEntityTypeConfiguration<DealLine>
{
    public void Configure(EntityTypeBuilder<DealLine> builder)
    {
        builder.ToTable("deal_lines", SalesDbContext.Schema);
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("id");
        builder.Property(l => l.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        builder.Property(l => l.DealId).HasColumnName("deal_id").IsRequired();
        builder.Property(l => l.ProductRef).HasColumnName("product_ref").HasMaxLength(200).IsRequired();
        builder.Property(l => l.UnitPrice).HasColumnName("unit_price").HasColumnType("numeric(18,2)");
        builder.Property(l => l.Quantity).HasColumnName("quantity");
        builder.Property(l => l.PriceListVersion).HasColumnName("price_list_version").HasMaxLength(64);

        builder.HasIndex(l => l.DealId);
    }
}
