using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Sales.Application;

/// <summary>
/// Resolves a child's parent-account name for the EF read paths (011-P4) — the counterpart of the grids'
/// <c>LEFT JOIN sales.accounts</c> under RLS. The account is loaded through the <em>caller's</em> scope and
/// the tenant global filter, never by a bare key lookup, so the name is returned exactly when
/// <c>GET /api/accounts/{id}</c> would return the account; otherwise <c>null</c>. An empty scope set yields
/// <c>1=0</c> and therefore <c>null</c> — fail closed on the label.
/// </summary>
internal static class AccountNames
{
    public static async Task<string?> ResolveAsync(
        SalesDbContext db,
        DataScopeSet scopes,
        Guid? accountId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scopes);

        if (accountId is not { } id)
        {
            return null;
        }

        return await db.Accounts
            .AsNoTracking()
            .WhereInScope(scopes)
            .Where(a => a.Id == id)
            .Select(a => a.Name)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
