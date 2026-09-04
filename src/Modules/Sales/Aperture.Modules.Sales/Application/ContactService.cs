using System.Text;
using Aperture.Modules.Sales.Domain;
using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Sales.Application;

/// <summary>
/// Contacts, done through the two sanctioned paths, exactly as <see cref="AccountService"/>: writes and
/// read-your-writes through <see cref="SalesDbContext"/> (EF, tenant global filter), the grid through
/// <see cref="ScopedConnection"/> (reader role + RLS). The one-account rule and scope inheritance are
/// enforced by loading the parent account through the caller's scope and building the contact from it —
/// the caller never names a tenant, an owner, or a second account.
/// </summary>
internal sealed class ContactService : IContactService
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private static readonly ScopeColumns GridColumns = ScopeColumns.For("c");

    // No trailing semicolon: ScopedConnection wraps this as a subquery. The scope columns keep their
    // snake_case names so the belt fragment resolves against the wrapper alias. @IncludeDeparted lets the
    // history view opt in; the default active grid excludes departed rows below the SQL, not in memory.
    private const string GridSql =
        """
        SELECT id, tenant_id, account_id, owner_user_id, team_id, region_id,
               name, email, phone, messenger, is_departed, departed_at, created_at
        FROM sales.contacts
        WHERE (@IncludeDeparted = TRUE OR is_departed = FALSE)
          AND (@HasCursor = FALSE OR (created_at, id) > (@AfterCreatedAt, @AfterId))
        ORDER BY created_at, id
        LIMIT @Limit
        """;

    private readonly SalesDbContext _db;
    private readonly ScopedConnection _reader;

    public ContactService(SalesDbContext db, ScopedConnection reader)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<ContactCreateResult> CreateAsync(
        DataScopeSet scopes,
        Guid accountId,
        CreateContactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(request);

        // The one-account rule and the visibility check in a single load: the account must exist AND be
        // within the caller's tenant and scope. WhereInScope on an empty scope set yields 1=0, so an
        // out-of-scope or cross-tenant account is indistinguishable from a missing one — a fail-closed deny
        // that never lets a caller attach a contact to an account they cannot see.
        var account = await _db.Accounts
            .WhereInScope(scopes)
            .SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return new ContactCreateResult(ContactCreateStatus.AccountNotFound, null);
        }

        // Built from the account: tenant and all five scope facts are stamped from the parent, never from
        // the request. This is where scope inheritance happens (edge 7).
        var contact = new Contact(
            Guid.NewGuid(),
            account,
            request.Name,
            request.Email,
            request.Phone,
            request.Messenger);

        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ContactCreateResult(ContactCreateStatus.Created, ToView(contact));
    }

    public async Task<ContactDepartResult> DepartAsync(
        DataScopeSet scopes,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        // Loaded through the scope predicate: a contact outside the caller's scope cannot be departed, and
        // is reported as not-found rather than forbidden (the same non-leaking deny as the account paths).
        var contact = await _db.Contacts
            .WhereInScope(scopes)
            .SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (contact is null)
        {
            return new ContactDepartResult(ContactDepartStatus.NotFound, null);
        }

        // "Removing" a contact marks it departed; the row stays, so history remains attributable
        // (DOMAIN.md §2). Depart is idempotent, so a replayed submit does not rewrite the timestamp.
        contact.Depart();
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ContactDepartResult(ContactDepartStatus.Departed, ToView(contact));
    }

    public async Task<ContactsPage> ListAsync(
        DataScopeSet scopes,
        bool includeDeparted,
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
        var rows = await _reader.QueryAsync<ContactGridRow>(
            scopes,
            GridColumns,
            GridSql,
            new
            {
                IncludeDeparted = includeDeparted,
                HasCursor = hasCursor,
                AfterCreatedAt = afterCreatedAt,
                AfterId = afterId,
                Limit = pageSize + 1,
            },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > pageSize;
        var items = (hasMore ? rows.Take(pageSize) : rows).Select(ToView).ToArray();
        var next = hasMore ? EncodeCursor(items[^1]) : null;

        return new ContactsPage(items, next);
    }

    // The raw-read target for the grid. A mutable class mapped by property injection, mirroring
    // AccountGridRow: the reader returns the timestamps as System.DateTime and constructor matching would
    // reject a DateTimeOffset parameter over that; ToView converts. Scope columns keep snake_case names.
    private sealed class ContactGridRow
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }

        public Guid AccountId { get; set; }

        public Guid OwnerUserId { get; set; }

        public Guid? TeamId { get; set; }

        public Guid? RegionId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Email { get; set; }

        public string? Phone { get; set; }

        public string? Messenger { get; set; }

        public bool IsDeparted { get; set; }

        public DateTime? DepartedAt { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    private static ContactView ToView(ContactGridRow r) =>
        new(
            r.Id,
            r.TenantId,
            r.AccountId,
            r.OwnerUserId,
            r.TeamId,
            r.RegionId,
            r.Name,
            r.Email,
            r.Phone,
            r.Messenger,
            r.IsDeparted,
            r.DepartedAt is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)) : null,
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)));

    private static ContactView ToView(Contact c) =>
        new(
            c.Id,
            c.TenantId.Value,
            c.AccountId!.Value,
            c.OwnerUserId.Value,
            c.TeamId,
            c.RegionId,
            c.Name,
            c.Email,
            c.Phone,
            c.Messenger,
            c.IsDeparted,
            c.DepartedAt,
            c.CreatedAt);

    // The cursor is the last row's (created_at, id), the same tuple the keyset WHERE compares. Opaque to
    // the client (base64) so it is treated as a token to echo back, not a field to hand-craft.
    private static string EncodeCursor(ContactView last) =>
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
