using System.Text;
using Aperture.Modules.Sales.Domain;
using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aperture.Modules.Sales.Application;

/// <summary>
/// Accounts, done through the two sanctioned paths: writes and read-your-writes go through
/// <see cref="SalesDbContext"/> (EF, tenant global filter, <c>xmin</c> concurrency), and the grid goes
/// through <see cref="ScopedConnection"/> (the reader role and its row-security policy). Neither path can
/// be widened from here — the empty scope set denies on both, and the tenant is never the caller's to name.
/// </summary>
internal sealed class AccountService : IAccountService
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    // The unique-violation SQLSTATE. A create that collides on (tenant_id, tax_id) surfaces here rather
    // than as a 500 — the tax-id dedup is a domain outcome, not a crash (DOMAIN.md §2).
    private const string UniqueViolation = PostgresErrorCodes.UniqueViolation;

    private static readonly ScopeColumns GridColumns = ScopeColumns.For("a");

    // No trailing semicolon: ScopedConnection wraps this as a subquery and a ';' would close the
    // statement before the wrapper's parenthesis. The scope columns are projected under their snake_case
    // names so the belt fragment (t.tenant_id, t.owner_user_id, …) resolves against the wrapper alias.
    private const string GridSql =
        """
        SELECT id, tenant_id, owner_user_id, name, tax_id, credit_limit,
               payment_terms_days, region_id, team_id, account_id, created_at,
               xmin AS version
        FROM sales.accounts
        WHERE (@HasCursor = FALSE OR (created_at, id) > (@AfterCreatedAt, @AfterId))
        ORDER BY created_at, id
        LIMIT @Limit
        """;

    private readonly SalesDbContext _db;
    private readonly ScopedConnection _reader;

    public AccountService(SalesDbContext db, ScopedConnection reader)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<AccountCreateResult> CreateAsync(
        TenantId tenant,
        UserId owner,
        CreateAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var account = new Account(
            Guid.NewGuid(),
            tenant,
            owner,
            request.Name,
            request.TaxId,
            request.CreditLimit,
            request.PaymentTermsDays,
            request.RegionId,
            request.TeamId);

        _db.Accounts.Add(account);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent or repeated create with the same tax id. Detach so the failed insert does not
            // linger in the change tracker, and report the duplicate as a domain outcome.
            _db.Entry(account).State = EntityState.Detached;
            return new AccountCreateResult(AccountCreateStatus.DuplicateTaxId, null);
        }

        return new AccountCreateResult(AccountCreateStatus.Created, ToView(account));
    }

    public async Task<AccountView?> GetAsync(
        DataScopeSet scopes,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        // EF path, tenant-filtered by the global query filter and scope-filtered by WhereInScope. An
        // empty scope set yields a 1=0 predicate, so an out-of-scope account is indistinguishable from a
        // missing one — a deny that does not leak existence.
        var account = await _db.Accounts
            .AsNoTracking()
            .WhereInScope(scopes)
            .SingleOrDefaultAsync(a => a.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return account is null ? null : ToView(account);
    }

    public async Task<AccountUpdateResult> UpdateAsync(
        DataScopeSet scopes,
        Guid id,
        UpdateAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(request);

        // Loaded through the scope predicate: an account outside the caller's scope cannot be edited, and
        // is reported as not-found rather than forbidden (same non-leaking deny as GetAsync).
        var account = await _db.Accounts
            .WhereInScope(scopes)
            .SingleOrDefaultAsync(a => a.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return new AccountUpdateResult(AccountUpdateStatus.NotFound, null);
        }

        // The client's optimistic check: if the row moved on since they read it, their edit is against a
        // stale view — reject it rather than clobber the change they never saw. The EF xmin token below
        // then guards the remaining window between this load and the commit (a concurrent writer in the
        // same instant), so no update is lost on either path.
        if (account.Version != request.ExpectedVersion)
        {
            return new AccountUpdateResult(AccountUpdateStatus.Conflict, null);
        }

        account.Update(
            new UserId(request.OwnerUserId),
            request.Name,
            request.CreditLimit,
            request.PaymentTermsDays,
            request.RegionId,
            request.TeamId);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new AccountUpdateResult(AccountUpdateStatus.Conflict, null);
        }

        return new AccountUpdateResult(AccountUpdateStatus.Updated, ToView(account));
    }

    public async Task<AccountsPage> ListAsync(
        DataScopeSet scopes,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var pageSize = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
        var (hasCursor, afterCreatedAt, afterId) = DecodeCursor(cursor);

        // Tenant and scope are enforced structurally, not by a literal predicate in this SQL: `scopes`
        // carries the tenant id, and ScopedConnection runs the read as the RLS reader role whose policy
        // re-asserts tenant_id (and the scope union) on every row below the string. One extra row tells us
        // whether a further page exists without a second count query.
        var rows = await _reader.QueryAsync<AccountGridRow>(
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

        return new AccountsPage(items, next);
    }

    // The raw-read target for the grid. A mutable class mapped by property injection (not a positional
    // record's constructor), because the reader returns created_at as a System.DateTime and constructor
    // matching rejects a DateTimeOffset parameter over that — property injection tolerates it, and we
    // convert to the DateTimeOffset the view carries in ToView below. The scope columns keep their
    // snake_case names so the belt fragment resolves; underscore matching maps them to these properties.
    private sealed class AccountGridRow
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }

        public Guid OwnerUserId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string TaxId { get; set; } = string.Empty;

        public decimal CreditLimit { get; set; }

        public int PaymentTermsDays { get; set; }

        public Guid? RegionId { get; set; }

        public Guid? TeamId { get; set; }

        public Guid AccountId { get; set; }

        public DateTime CreatedAt { get; set; }

        public uint Version { get; set; }
    }

    private static AccountView ToView(AccountGridRow r) =>
        new(
            r.Id,
            r.TenantId,
            r.OwnerUserId,
            r.Name,
            r.TaxId,
            r.CreditLimit,
            r.PaymentTermsDays,
            r.RegionId,
            r.TeamId,
            r.AccountId,
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
            r.Version);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };

    private static AccountView ToView(Account a) =>
        new(
            a.Id,
            a.TenantId.Value,
            a.OwnerUserId.Value,
            a.Name,
            a.TaxId,
            a.CreditLimit,
            a.PaymentTermsDays,
            a.RegionId,
            a.TeamId,
            a.AccountId!.Value,
            a.CreatedAt,
            a.Version);

    // The cursor is the last row's (created_at, id), the same tuple the keyset WHERE compares. Opaque to
    // the client (base64) so it is treated as a token to echo back, not a field to hand-craft.
    private static string EncodeCursor(AccountView last) =>
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
