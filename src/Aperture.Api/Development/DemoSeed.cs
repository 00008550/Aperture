using Aperture.Modules.Access.Domain;
using Aperture.Modules.Access.Persistence;
using Aperture.Modules.Sales.Application;
using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data.RowLevelSecurity;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Aperture.Api.Development;

/// <summary>One seeded demo user, and the one-line reason it exists.</summary>
public sealed record DemoPersona(
    Guid UserId,
    string Email,
    string DisplayName,
    string Demonstrates,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<DemoGrant> Grants);

/// <summary>A scope grant a persona holds. <see cref="Region"/> names a seeded region, or is null.</summary>
public sealed record DemoGrant(ScopeGrantKind Kind, string? Region = null);

/// <summary>
/// The Development-only demo tenant (010-P5a): one tenant, five users that each demonstrate one access
/// state, and enough Sales data for every screen to have something to show.
/// <para>
/// Runs only from <c>--seed-demo</c> on a Development host (see <see cref="DemoSeedCommand"/>), against the
/// dedicated <c>aperture_dev</c> database. Idempotent: every row is keyed by a natural key — the tenant
/// slug, user emails, region/role names, account tax ids, contact and deal names under their account — so
/// a re-run finds what exists and adds only what is missing. It never deletes and never updates.
/// </para>
/// <para>
/// Access rows go through the Access domain types and <see cref="AccessDbContext"/> (there is no
/// provisioning service yet, and inventing one is out of scope). Sales rows go through the Sales module's
/// own services, so every aggregate rule and scope inheritance applies exactly as it does behind the API.
/// </para>
/// </summary>
public sealed class DemoSeed(
    AccessDbContext access,
    SalesDbContext sales,
    IAccountService accounts,
    IContactService contacts,
    IDealService deals,
    IConfiguration configuration,
    ILogger<DemoSeed> logger)
{
    /// <summary>The fixed demo tenant slug. <c>GET /api/dev/users</c> lists this tenant and no other.</summary>
    public const string TenantSlug = "northwind-demo";

    public const string TenantName = "Northwind Supply";

    /// <summary>The only database the seed will write to. The compose default <c>aperture</c> database
    /// may hold a stale early schema and belongs to the user — the seed refuses to touch it.</summary>
    public const string DatabaseName = "aperture_dev";

    private const string East = "East";
    private const string West = "West";

    private static readonly string[] SalesRead =
        [Permissions.AccountsRead, Permissions.ContactsRead, Permissions.DealsRead];

    private static readonly string[] SalesReadWrite =
        [.. SalesRead, Permissions.AccountsWrite, Permissions.ContactsWrite, Permissions.DealsWrite];

    /// <summary>The five demo users, in the order the sign-in picker lists them.</summary>
    public static IReadOnlyList<DemoPersona> Personas { get; } =
    [
        new(Guid.Parse("0d000000-0000-4000-8000-00000000a0a1"), "ada@northwind.example", "Ada Admin",
            "Admin: every permission, whole tenant",
            [.. Permissions.All.Order(StringComparer.Ordinal)],
            [new(ScopeGrantKind.AllTenant)]),
        new(Guid.Parse("0d000000-0000-4000-8000-00000000e0e1"), "ethan@northwind.example", "Ethan East",
            "East region, read-write",
            SalesReadWrite,
            [new(ScopeGrantKind.Self), new(ScopeGrantKind.Region, East)]),
        new(Guid.Parse("0d000000-0000-4000-8000-00000000f0f1"), "vera@northwind.example", "Vera Viewer",
            "East region, read-only",
            SalesRead,
            [new(ScopeGrantKind.Region, East)]),
        new(Guid.Parse("0d000000-0000-4000-8000-00000000b0b1"), "nolan@northwind.example", "Nolan NoScope",
            "Reads, but no data scope: sees nothing",
            SalesRead,
            []),
        new(Guid.Parse("0d000000-0000-4000-8000-00000000c0c1"), "lara@northwind.example", "Lara Locked",
            "No sales permissions: locked navigation",
            [Permissions.OrdersRead],
            [new(ScopeGrantKind.Self)]),
    ];

    private static readonly TenantId DemoTenantId = new(Guid.Parse("0d000000-0000-4000-8000-000000000001"));

    private static readonly Dictionary<string, Guid> RegionIds = new(StringComparer.Ordinal)
    {
        [East] = Guid.Parse("0d000000-0000-4000-8000-0000000e0001"),
        [West] = Guid.Parse("0d000000-0000-4000-8000-0000000e0002"),
    };

    private sealed record SeedAccount(string TaxId, string Name, string OwnerEmail, string Region, decimal CreditLimit);

    private sealed record SeedContact(string AccountTaxId, string Name, string? Email, bool Departed = false);

    private sealed record SeedLine(string ProductRef, decimal UnitPrice, int Quantity);

    private sealed record SeedDeal(string AccountTaxId, string Name, decimal Amount, decimal DiscountPct, SeedLine[] Lines);

    private static readonly SeedAccount[] Accounts =
    [
        new("NW-DEMO-0001", "Contoso East Traders", "ethan@northwind.example", East, 50_000m),
        new("NW-DEMO-0002", "Fabrikam Harbour Supply", "ada@northwind.example", East, 120_000m),
        new("NW-DEMO-0003", "Tailspin Freight", "ethan@northwind.example", East, 25_000m),
        new("NW-DEMO-0004", "Litware Pacific", "ada@northwind.example", West, 80_000m),
        new("NW-DEMO-0005", "Adventure Works West", "ada@northwind.example", West, 60_000m),
    ];

    private static readonly SeedContact[] Contacts =
    [
        new("NW-DEMO-0001", "Priya Raman", "priya.raman@contoso.example"),
        new("NW-DEMO-0001", "Tom Okafor", "tom.okafor@contoso.example", Departed: true),
        new("NW-DEMO-0002", "Grace Liu", "grace.liu@fabrikam.example"),
        new("NW-DEMO-0003", "Mateo Silva", "mateo.silva@tailspin.example"),
        new("NW-DEMO-0004", "Hannah Berg", "hannah.berg@litware.example"),
        new("NW-DEMO-0005", "Oscar Novak", "oscar.novak@adventure-works.example"),
    ];

    private static readonly SeedDeal[] Deals =
    [
        new("NW-DEMO-0001", "Q4 restock", 18_400m, 5m,
            [new("PAL-STD-1200", 42m, 300), new("WRAP-500", 11m, 400)]),
        // Above the 20% discount-approval threshold: winning it needs deals.discount.approve (P7).
        new("NW-DEMO-0002", "Fleet renewal", 96_000m, 30m,
            [new("CRATE-HD", 240m, 250), new("SVC-PLAN-12M", 36_000m, 1)]),
        new("NW-DEMO-0003", "Cold-chain pilot", 7_500m, 0m,
            [new("REEFER-PAL", 75m, 100)]),
        new("NW-DEMO-0004", "Pacific expansion", 52_000m, 12m,
            [new("PAL-STD-1200", 40m, 1000), new("SVC-ONBOARD", 12_000m, 1)]),
    ];

    private const string PriceListVersion = "2026-Q3";

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);

        // Access first (it creates the aperture_reader role), then Sales (its migrations grant to it).
        await access.Database.MigrateAsync(cancellationToken);
        await sales.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Seed: access and sales schemas migrated.");

        await SetReaderPasswordAsync(cancellationToken);

        // Tenants and users are not tenant-owned; everything after them is, so it runs under the demo
        // tenant's ambient context — the same filter every request runs under.
        var tenantId = await SeedTenantAndUsersAsync(cancellationToken);
        using (AmbientTenantContext.Begin(tenantId))
        {
            await SeedMembershipsAsync(tenantId, cancellationToken);
            await SeedSalesAsync(tenantId, cancellationToken);
        }

        logger.LogInformation("Seed: demo tenant {Slug} is complete.", TenantSlug);
    }

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        var database = access.Database.GetDbConnection().Database;
        if (!string.Equals(database, DatabaseName, StringComparison.Ordinal))
        {
            // Fail closed: the only database this command may create or write is the dedicated dev one.
            throw new InvalidOperationException(
                $"The demo seed writes only to '{DatabaseName}', but ConnectionStrings:Aperture names '{database}'. " +
                "Nothing was changed.");
        }

        // The provider's own database creator: it connects to the maintenance database as the owner and
        // issues CREATE DATABASE. Exists/Create, never EnsureCreated (which would bypass migrations) and
        // never anything that drops.
        var creator = access.Database.GetService<IRelationalDatabaseCreator>();
        if (await creator.ExistsAsync(cancellationToken))
        {
            logger.LogInformation("Seed: database {Database} exists.", database);
            return;
        }

        await creator.CreateAsync(cancellationToken);
        logger.LogInformation("Seed: database {Database} created.", database);
    }

    /// <summary>
    /// Gives the password-less <c>aperture_reader</c> role (created by the Access migration) the dev
    /// password, so the scoped raw-read path can authenticate. ALTER ROLE cannot take a bind parameter, so
    /// the value travels as a parameterised session setting and a DO block formats it with <c>%L</c>
    /// (literal-quoted) — no string concatenation of the secret into SQL, and no raw-SQL entry point.
    /// This is a role statement, not a read or write of any tenant's rows.
    /// </summary>
    private async Task SetReaderPasswordAsync(CancellationToken cancellationToken)
    {
        var password = configuration["Aperture:ReaderPassword"];
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("Aperture:ReaderPassword is not configured for Development.");
        }

        // One transaction: set_config(..., is_local: true) scopes the secret to it, so it is gone at commit.
        await using var transaction = await access.Database.BeginTransactionAsync(cancellationToken);

        // Cluster-level role statements, not tenant data: no tenant_id applies — they read and write no
        // tenant's rows (the gate's tenant-predicate rule is about reads and writes of tenant-owned tables).
        await access.Database.ExecuteSqlAsync(
            $"SELECT set_config('aperture.seed_reader_password', {password}, true)",
            cancellationToken);
        await access.Database.ExecuteSqlAsync(
            $"""
            DO $$ BEGIN
              EXECUTE format('ALTER ROLE %I PASSWORD %L', 'aperture_reader',
                             current_setting('aperture.seed_reader_password'));
            END $$
            """,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Seed: {Role} password set from Aperture:ReaderPassword.", ScopeRlsPolicy.ReaderRole);
    }

    private async Task<TenantId> SeedTenantAndUsersAsync(CancellationToken cancellationToken)
    {
        var tenant = await access.Tenants.SingleOrDefaultAsync(t => t.Slug == TenantSlug, cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant(DemoTenantId, TenantName, TenantSlug);
            access.Tenants.Add(tenant);
            logger.LogInformation("Seed: tenant {Slug} added.", TenantSlug);
        }

        foreach (var persona in Personas)
        {
            var email = persona.Email.ToLowerInvariant();
            if (!await access.Users.AnyAsync(u => u.Email == email, cancellationToken))
            {
                access.Users.Add(new User(new UserId(persona.UserId), email, persona.DisplayName));
                logger.LogInformation("Seed: user {Email} added.", email);
            }
        }

        await access.SaveChangesAsync(cancellationToken);
        return tenant.Id;
    }

    private async Task SeedMembershipsAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        foreach (var (name, id) in RegionIds)
        {
            if (!await access.Regions.AnyAsync(r => r.Name == name, cancellationToken))
            {
                access.Regions.Add(new Region(id, tenantId, name));
            }
        }

        await access.SaveChangesAsync(cancellationToken);
        var regions = await access.Regions.ToDictionaryAsync(r => r.Name, r => r.Id, cancellationToken);

        foreach (var persona in Personas)
        {
            var email = persona.Email.ToLowerInvariant();
            var user = await access.Users.SingleAsync(u => u.Email == email, cancellationToken);

            var membership = await access.Memberships.SingleOrDefaultAsync(m => m.UserId == user.Id, cancellationToken);
            if (membership is null)
            {
                membership = new Membership(Guid.NewGuid(), tenantId, user.Id);
                access.Memberships.Add(membership);
            }

            var roleName = $"Demo: {persona.DisplayName}";
            var role = await access.Roles.SingleOrDefaultAsync(r => r.Name == roleName, cancellationToken);
            if (role is null)
            {
                role = new Role(Guid.NewGuid(), tenantId, roleName);
                access.Roles.Add(role);
            }

            var held = await access.RolePermissions
                .Where(p => p.RoleId == role.Id)
                .Select(p => p.Permission)
                .ToListAsync(cancellationToken);
            foreach (var permission in persona.Permissions.Except(held, StringComparer.Ordinal))
            {
                access.RolePermissions.Add(new RolePermission(Guid.NewGuid(), tenantId, role.Id, permission));
            }

            if (!await access.MembershipRoles.AnyAsync(
                    mr => mr.MembershipId == membership.Id && mr.RoleId == role.Id, cancellationToken))
            {
                access.MembershipRoles.Add(new MembershipRole(Guid.NewGuid(), tenantId, membership.Id, role.Id));
            }

            var grants = await access.ScopeGrants
                .Where(g => g.MembershipId == membership.Id)
                .Select(g => new { g.Kind, g.TargetId })
                .ToListAsync(cancellationToken);
            foreach (var grant in persona.Grants)
            {
                Guid? target = grant.Region is null ? null : regions[grant.Region];
                if (!grants.Any(g => g.Kind == grant.Kind && g.TargetId == target))
                {
                    access.ScopeGrants.Add(new ScopeGrant(Guid.NewGuid(), tenantId, membership.Id, grant.Kind, target));
                }
            }

            await access.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Seed: {Count} demo memberships with roles and grants in place.", Personas.Count);
    }

    private async Task SeedSalesAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        // The seed acts tenant-wide: it is populating the tenant, not acting as any one user.
        var everything = DataScopeSet.Of(tenantId, new DataScope.AllTenant());
        var regions = await access.Regions.ToDictionaryAsync(r => r.Name, r => r.Id, cancellationToken);
        var users = await access.Users.ToDictionaryAsync(u => u.Email, u => u.Id, cancellationToken);

        // Accounts, keyed by tax id (unique per tenant).
        var byTaxId = (await accounts.ListAsync(everything, 200, null, cancellationToken)).Items
            .ToDictionary(a => a.TaxId, a => a.Id, StringComparer.Ordinal);
        var addedAccounts = 0;
        foreach (var a in Accounts)
        {
            if (byTaxId.ContainsKey(a.TaxId))
            {
                continue;
            }

            var created = await accounts.CreateAsync(
                tenantId,
                users[a.OwnerEmail],
                new CreateAccountRequest(a.Name, a.TaxId, a.CreditLimit, 30, regions[a.Region], null),
                cancellationToken);
            byTaxId[a.TaxId] = created.Account?.Id
                ?? throw new InvalidOperationException($"Seed: account {a.TaxId} was not created ({created.Status}).");
            addedAccounts++;
        }

        // Contacts, keyed by (account, name). The departed one stays, marked — depart is not delete.
        var existingContacts = (await contacts.ListAsync(everything, includeDeparted: true, 200, null, cancellationToken)).Items;
        var addedContacts = 0;
        foreach (var c in Contacts)
        {
            var accountId = byTaxId[c.AccountTaxId];
            var contact = existingContacts.FirstOrDefault(x => x.AccountId == accountId && x.Name == c.Name);
            if (contact is null)
            {
                var created = await contacts.CreateAsync(
                    everything, accountId, new CreateContactRequest(c.Name, c.Email, null, null), cancellationToken);
                contact = created.Contact
                    ?? throw new InvalidOperationException($"Seed: contact {c.Name} was not created ({created.Status}).");
                addedContacts++;
            }

            if (c.Departed && !contact.IsDeparted)
            {
                await contacts.DepartAsync(everything, contact.Id, cancellationToken);
            }
        }

        // Deals, keyed by (account, name); lines keyed by product within the deal.
        var existingDeals = (await deals.ListAsync(everything, 200, null, cancellationToken)).Items;
        var addedDeals = 0;
        var addedLines = 0;
        foreach (var d in Deals)
        {
            var accountId = byTaxId[d.AccountTaxId];
            var dealId = existingDeals.FirstOrDefault(x => x.AccountId == accountId && x.Name == d.Name)?.Id;
            if (dealId is null)
            {
                var created = await deals.CreateAsync(
                    everything, new CreateDealRequest(accountId, d.Name, d.Amount, d.DiscountPct), cancellationToken);
                dealId = created.Deal?.Id
                    ?? throw new InvalidOperationException($"Seed: deal {d.Name} was not created ({created.Status}).");
                addedDeals++;
            }

            var deal = await deals.GetAsync(everything, dealId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Seed: deal {d.Name} is not readable.");
            foreach (var line in d.Lines.Where(l => deal.Lines.All(x => x.ProductRef != l.ProductRef)))
            {
                await deals.AddLineAsync(
                    everything,
                    deal.Id,
                    new AddDealLineRequest(line.ProductRef, line.UnitPrice, line.Quantity, PriceListVersion),
                    cancellationToken);
                addedLines++;
            }
        }

        logger.LogInformation(
            "Seed: sales added {Accounts} accounts, {Contacts} contacts, {Deals} deals, {Lines} deal lines " +
            "(everything else already present).",
            addedAccounts,
            addedContacts,
            addedDeals,
            addedLines);
    }
}
