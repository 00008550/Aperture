using System.Text;
using Aperture.Modules.Sales.Domain;
using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Sales.Application;

/// <summary>
/// Deals, done through the two sanctioned paths, exactly as <see cref="AccountService"/> and
/// <see cref="ContactService"/>: writes and read-your-writes through <see cref="SalesDbContext"/> (EF,
/// tenant global filter, <c>xmin</c> concurrency), the grid through <see cref="ScopedConnection"/> (reader
/// role + RLS). The one-account rule and scope inheritance are enforced by loading the parent account
/// through the caller's scope and building the deal from it — the caller never names a tenant, an owner,
/// or a second account. P4 has no lifecycle transitions; the deal opens in <c>new</c> and stays there.
/// </summary>
internal sealed class DealService : IDealService
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private static readonly ScopeColumns GridColumns = ScopeColumns.For("d");

    // No trailing semicolon: ScopedConnection wraps this as a subquery. The scope columns keep their
    // snake_case names so the belt fragment resolves against the wrapper alias. The grid returns the deal
    // header only — lines are read with a single deal, not on the list.
    private const string GridSql =
        """
        SELECT id, tenant_id, account_id, owner_user_id, team_id, region_id,
               name, stage, amount, discount_pct, frozen_price_list_version,
               pending_approval, lost_reason_code, created_at, xmin AS version
        FROM sales.deals
        WHERE (@HasCursor = FALSE OR (created_at, id) > (@AfterCreatedAt, @AfterId))
        ORDER BY created_at, id
        LIMIT @Limit
        """;

    private readonly SalesDbContext _db;
    private readonly ScopedConnection _reader;

    public DealService(SalesDbContext db, ScopedConnection reader)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<DealCreateResult> CreateAsync(
        DataScopeSet scopes,
        CreateDealRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(request);

        // The one-account rule and the visibility check in a single load: the account must exist AND be
        // within the caller's tenant and scope. WhereInScope on an empty scope set yields 1=0, so an
        // out-of-scope or cross-tenant account is indistinguishable from a missing one — a fail-closed
        // deny that never lets a caller open a deal against an account they cannot see.
        var account = await _db.Accounts
            .WhereInScope(scopes)
            .SingleOrDefaultAsync(a => a.Id == request.AccountId, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return new DealCreateResult(DealCreateStatus.AccountNotFound, null);
        }

        // Built from the account: tenant and all five scope facts are stamped from the parent, never from
        // the request. This is where scope inheritance happens.
        var deal = new Deal(Guid.NewGuid(), account, request.Name, request.Amount, request.DiscountPct);

        _db.Deals.Add(deal);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new DealCreateResult(DealCreateStatus.Created, ToView(deal));
    }

    public async Task<DealView?> GetAsync(
        DataScopeSet scopes,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var deal = await _db.Deals
            .AsNoTracking()
            .Include(d => d.Lines)
            .WhereInScope(scopes)
            .SingleOrDefaultAsync(d => d.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return deal is null ? null : ToView(deal);
    }

    public async Task<DealLineAddResult> AddLineAsync(
        DataScopeSet scopes,
        Guid dealId,
        AddDealLineRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(request);

        // Loaded whole (with its lines) through the scope predicate: a deal outside the caller's scope
        // cannot gain a line, and is reported as not-found rather than forbidden (the non-leaking deny the
        // account/contact paths use).
        var deal = await _db.Deals
            .Include(d => d.Lines)
            .WhereInScope(scopes)
            .SingleOrDefaultAsync(d => d.Id == dealId, cancellationToken)
            .ConfigureAwait(false);

        if (deal is null)
        {
            return new DealLineAddResult(DealLineAddStatus.DealNotFound, null);
        }

        // Add the line to the aggregate, then mark it Added explicitly. The deal was loaded by a query
        // (tracked, Unchanged); a child appended to a tracked parent's navigation with a client-set Guid
        // key is otherwise inferred by change detection as an existing row and issued as an UPDATE (which
        // affects zero rows and throws). Adding it through the DbSet pins the Added state, exactly as the
        // Account/Contact create paths do.
        var line = deal.AddLine(
            request.ProductRef, request.UnitPrice, request.Quantity, request.PriceListVersion);
        _db.DealLines.Add(line);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new DealLineAddResult(DealLineAddStatus.Added, ToView(deal));
    }

    public async Task<DealsPage> ListAsync(
        DataScopeSet scopes,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var pageSize = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
        var (hasCursor, afterCreatedAt, afterId) = DecodeCursor(cursor);

        // Tenant and scope are enforced structurally: `scopes` carries the tenant id, and ScopedConnection
        // runs the read as the RLS reader role whose policy re-asserts tenant_id (and the scope union) on
        // every row below the string. One extra row tells us whether a further page exists.
        var rows = await _reader.QueryAsync<DealGridRow>(
            scopes,
            GridColumns,
            GridSql,
            new
            {
                HasCursor = hasCursor,
                AfterCreatedAt = afterCreatedAt,
                AfterId = afterId,
                Limit = pageSize + 1,
            },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > pageSize;
        var items = (hasMore ? rows.Take(pageSize) : rows).Select(ToView).ToArray();
        var next = hasMore ? EncodeCursor(items[^1]) : null;

        return new DealsPage(items, next);
    }

    // The raw-read target for the grid. A mutable class mapped by property injection, mirroring
    // AccountGridRow: the reader returns created_at as System.DateTime and constructor matching would
    // reject a DateTimeOffset parameter over that; ToView converts. Scope columns keep snake_case names.
    private sealed class DealGridRow
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }

        public Guid AccountId { get; set; }

        public Guid OwnerUserId { get; set; }

        public Guid? TeamId { get; set; }

        public Guid? RegionId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Stage { get; set; } = string.Empty;

        public decimal Amount { get; set; }

        public decimal DiscountPct { get; set; }

        public string? FrozenPriceListVersion { get; set; }

        public bool PendingApproval { get; set; }

        public string? LostReasonCode { get; set; }

        public DateTime CreatedAt { get; set; }

        public uint Version { get; set; }
    }

    private static DealView ToView(DealGridRow r) =>
        new(
            r.Id,
            r.TenantId,
            r.AccountId,
            r.OwnerUserId,
            r.TeamId,
            r.RegionId,
            r.Name,
            r.Stage,
            r.Amount,
            r.DiscountPct,
            r.FrozenPriceListVersion,
            r.PendingApproval,
            r.LostReasonCode,
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
            r.Version,
            // The grid is the header only; a single-deal read carries the lines.
            Array.Empty<DealLineView>());

    private static DealView ToView(Deal d) =>
        new(
            d.Id,
            d.TenantId.Value,
            d.AccountId!.Value,
            d.OwnerUserId.Value,
            d.TeamId,
            d.RegionId,
            d.Name,
            d.Stage,
            d.Amount,
            d.DiscountPct,
            d.FrozenPriceListVersion,
            d.PendingApproval,
            d.LostReasonCode,
            d.CreatedAt,
            d.Version,
            d.Lines
                .OrderBy(l => l.Id)
                .Select(l => new DealLineView(l.Id, l.DealId, l.ProductRef, l.UnitPrice, l.Quantity, l.PriceListVersion))
                .ToArray());

    // The cursor is the last row's (created_at, id), the same tuple the keyset WHERE compares. Opaque to
    // the client (base64) so it is treated as a token to echo back, not a field to hand-craft.
    private static string EncodeCursor(DealView last) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{last.CreatedAt.UtcTicks:D}:{last.Id:D}"));

    private static (bool HasCursor, DateTimeOffset AfterCreatedAt, Guid AfterId) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (false, default, default);
        }

        var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        var separator = text.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0
            || !long.TryParse(text[..separator], out var ticks)
            || !Guid.TryParse(text[(separator + 1)..], out var id))
        {
            throw new ArgumentException("The pagination cursor is malformed.", nameof(cursor));
        }

        return (true, new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }
}
