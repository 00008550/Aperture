using Aperture.Contracts.Sales;
using Aperture.Modules.Sales.Domain;
using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Sales.Application;

/// <summary>
/// The Sales side of the <see cref="Aperture.Contracts"/> reads other modules make (plan 003-P1):
/// <see cref="IWonDealSource"/> and <see cref="IAccountCreditReader"/>. Both go through
/// <see cref="SalesDbContext"/> (tenant global filter) and <c>WhereInScope</c> (the caller's scope set,
/// <c>1=0</c> when empty), so a contract read sees exactly what the same caller sees on the Sales endpoints.
/// Results are mapped to contract records here; no Sales type leaves the module.
/// </summary>
internal sealed class SalesContractReader : IWonDealSource, IAccountCreditReader
{
    private readonly SalesDbContext _db;

    public SalesContractReader(SalesDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<WonDealLookup> GetWonDealAsync(
        DataScopeSet scopes,
        Guid dealId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var deal = await _db.Deals
            .AsNoTracking()
            .Include(d => d.Lines)
            .WhereInScope(scopes)
            .SingleOrDefaultAsync(d => d.Id == dealId, cancellationToken)
            .ConfigureAwait(false);

        if (deal is null)
        {
            return WonDealLookup.NotFound;
        }

        if (deal.Stage != Deal.Stages.Won)
        {
            return WonDealLookup.NotWon;
        }

        // A deal always carries its account id (inherited at create); a won deal without one is corrupt
        // data, and refusing is the fail-closed answer.
        var accountId = deal.AccountId
            ?? throw new InvalidOperationException($"Deal {deal.Id} has no account.");

        // The label under the same scope: an account invisible to the caller yields a null name, never a
        // leaked one (the 011-P4 read-model rule).
        var accountName = await _db.Accounts
            .AsNoTracking()
            .WhereInScope(scopes)
            .Where(a => a.Id == accountId)
            .Select(a => a.Name)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var lines = deal.Lines
            .OrderBy(l => l.ProductRef, StringComparer.Ordinal)
            .ThenBy(l => l.Id)
            .Select(l => new WonDealLine(l.ProductRef, l.UnitPrice, l.Quantity))
            .ToList();

        return WonDealLookup.Won(new WonDealSnapshot(
            deal.Id,
            deal.TenantId,
            accountId,
            deal.OwnerUserId,
            deal.TeamId,
            deal.RegionId,
            accountName,
            deal.Name,
            deal.FrozenPriceListVersion,
            lines));
    }

    public async Task<decimal?> GetCreditLimitAsync(
        DataScopeSet scopes,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        return await _db.Accounts
            .AsNoTracking()
            .WhereInScope(scopes)
            .Where(a => a.Id == accountId)
            .Select(a => (decimal?)a.CreditLimit)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
