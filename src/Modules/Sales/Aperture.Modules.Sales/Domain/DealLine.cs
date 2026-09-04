using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Sales.Domain;

/// <summary>
/// A single line on a <see cref="Deal"/> (DOMAIN.md §2): a product, a unit price, a quantity, and the
/// price-list version it was priced against. Lines are a child of the deal aggregate — created only
/// through <see cref="Deal.AddLine"/>, loaded and saved with the deal — because the domain rules a line
/// exists to satisfy (a won deal needs at least one priced line; a quote freezes the price-list version)
/// are evaluated over the deal and its lines together.
/// <para>
/// A line is <see cref="ITenantOwned"/> so the tenant global query filter covers it, but it is <em>not</em>
/// <see cref="Aperture.SharedKernel.Authorization.IScopedResource"/>: it carries no denormalised scope
/// columns and is never read through the reader-role grid on its own. Its scope is its deal's — reached
/// only by loading the parent, which is itself scope-filtered.
/// </para>
/// </summary>
public sealed class DealLine : ITenantOwned
{
    private DealLine()
    {
    }

    internal DealLine(
        Guid id,
        Deal deal,
        string productRef,
        decimal unitPrice,
        int quantity,
        string? priceListVersion)
    {
        ArgumentNullException.ThrowIfNull(deal);

        Id = id;
        TenantId = deal.TenantId;
        DealId = deal.Id;
        ProductRef = Require(productRef, nameof(productRef));
        UnitPrice = NonNegative(unitPrice, nameof(unitPrice));
        Quantity = Positive(quantity, nameof(quantity));
        PriceListVersion = Trimmed(priceListVersion);
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    /// <summary>The parent deal. Immutable; the line belongs to exactly one deal.</summary>
    public Guid DealId { get; private set; }

    public string ProductRef { get; private set; } = string.Empty;

    public decimal UnitPrice { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>The price-list version this line was priced against. Null until a price list is named;
    /// P5's <c>quoted</c> transition freezes it onto the line (DOMAIN.md §2 rule 2).</summary>
    public string? PriceListVersion { get; private set; }

    private static string Require(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value.Trim();

    private static decimal NonNegative(decimal value, string paramName) =>
        value < 0 ? throw new ArgumentOutOfRangeException(paramName, value, "Must not be negative.") : value;

    private static int Positive(int value, string paramName) =>
        value <= 0 ? throw new ArgumentOutOfRangeException(paramName, value, "Must be greater than zero.") : value;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
