using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Sales.Application;

/// <summary>
/// Resolves the discount percentage above which a deal cannot advance to <c>won</c> on the agent's own
/// authority and must instead hold for a lead's approval (DOMAIN.md §2 rule 3). The threshold is a
/// <b>tenant-wide setting</b> — a single configurable percent per tenant (the granularity the user approved
/// for 002, open question 2) — so the resolution is keyed on the tenant. This interface is the seam a future
/// per-tenant store plugs into; 002 backs it with a single configured value shared by every tenant.
/// </summary>
public interface IDiscountThresholdProvider
{
    /// <summary>The discount percent (0–100) above which a deal in <paramref name="tenant"/> requires a
    /// lead's approval to be won. A deal whose discount is at or below this advances without approval.</summary>
    ValueTask<decimal> GetThresholdPctAsync(TenantId tenant, CancellationToken cancellationToken = default);
}

/// <summary>
/// The 002 implementation: one configured tenant-wide percent (bound in <see cref="SalesModule"/> from
/// <c>Sales:DiscountApprovalThresholdPct</c>). Every tenant resolves to the same value for now; the
/// per-tenant refinement is a later concern (open question 2's "later refinement"). The value is validated
/// as a percentage at construction so a mis-configured threshold fails fast rather than silently disabling
/// the guard.
/// </summary>
internal sealed class ConfiguredDiscountThresholdProvider : IDiscountThresholdProvider
{
    private readonly decimal _thresholdPct;

    public ConfiguredDiscountThresholdProvider(decimal thresholdPct)
    {
        if (thresholdPct is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(thresholdPct), thresholdPct, "The discount approval threshold must be a percentage between 0 and 100.");
        }

        _thresholdPct = thresholdPct;
    }

    public ValueTask<decimal> GetThresholdPctAsync(TenantId tenant, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_thresholdPct);
}
