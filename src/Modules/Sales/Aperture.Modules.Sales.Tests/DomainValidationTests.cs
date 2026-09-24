using Aperture.Modules.Sales.Domain;
using Aperture.SharedKernel.Domain;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Sales.Tests;

/// <summary>
/// Plan 011-P1: every caller-input guard in the four Sales aggregates raises a
/// <see cref="DomainValidationException"/> naming the field as the JSON request spells it — the host maps
/// that field straight into <c>errors.&lt;field&gt;</c>, so a wrong name here is a wrong API contract. The
/// programming-error guards (a null account) stay <see cref="ArgumentNullException"/> and are asserted
/// elsewhere (<c>A_deal_cannot_be_constructed_without_an_account</c>).
/// </summary>
public sealed class DomainValidationTests
{
    private static Account NewAccount(
        string name = "Acme", string taxId = "TX-1", decimal creditLimit = 0m, int paymentTermsDays = 0) =>
        new(Guid.NewGuid(), TenantId.New(), UserId.New(), name, taxId, creditLimit, paymentTermsDays, null, null);

    private static string FieldOf(Action act) => Assert.Throws<DomainValidationException>(act).Field;

    // ---- Account: create and update ---------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Account_create_with_a_blank_name_names_the_name_field(string name) =>
        Assert.Equal("name", FieldOf(() => NewAccount(name: name)));

    [Fact]
    public void Account_create_with_a_blank_tax_id_names_the_taxId_field() =>
        Assert.Equal("taxId", FieldOf(() => NewAccount(taxId: " ")));

    [Fact]
    public void Account_create_with_a_negative_credit_limit_names_the_creditLimit_field() =>
        Assert.Equal("creditLimit", FieldOf(() => NewAccount(creditLimit: -1m)));

    [Fact]
    public void Account_create_with_negative_payment_terms_names_the_paymentTermsDays_field() =>
        Assert.Equal("paymentTermsDays", FieldOf(() => NewAccount(paymentTermsDays: -1)));

    [Fact]
    public void Account_create_accepts_zero_credit_limit_and_zero_payment_terms()
    {
        var account = NewAccount(creditLimit: 0m, paymentTermsDays: 0);
        Assert.Equal(0m, account.CreditLimit);
        Assert.Equal(0, account.PaymentTermsDays);
    }

    [Theory]
    [InlineData(" ", 1, 1, "name")]
    [InlineData("ok", -1, 1, "creditLimit")]
    [InlineData("ok", 1, -1, "paymentTermsDays")]
    public void Account_update_guards_name_the_field(string name, int creditLimit, int terms, string field)
    {
        var account = NewAccount();
        Assert.Equal(field, FieldOf(() => account.Update(account.OwnerUserId, name, creditLimit, terms, null, null)));
    }

    // ---- Contact --------------------------------------------------------------------------------

    [Fact]
    public void Contact_with_a_blank_name_names_the_name_field() =>
        Assert.Equal("name", FieldOf(() => new Contact(Guid.NewGuid(), NewAccount(), "  ", null, null, null)));

    // ---- Deal -----------------------------------------------------------------------------------

    [Fact]
    public void Deal_with_a_blank_name_names_the_name_field() =>
        Assert.Equal("name", FieldOf(() => new Deal(Guid.NewGuid(), NewAccount(), " ", 1m, 0m)));

    [Fact]
    public void Deal_with_a_negative_amount_names_the_amount_field() =>
        Assert.Equal("amount", FieldOf(() => new Deal(Guid.NewGuid(), NewAccount(), "d", -1m, 0m)));

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    public void Deal_with_a_discount_outside_0_to_100_names_the_discountPct_field(string discount) =>
        Assert.Equal("discountPct", FieldOf(
            () => new Deal(Guid.NewGuid(), NewAccount(), "d", 1m, decimal.Parse(discount, System.Globalization.CultureInfo.InvariantCulture))));

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Deal_discount_bounds_are_inclusive(int discount)
    {
        var deal = new Deal(Guid.NewGuid(), NewAccount(), "d", 0m, discount);
        Assert.Equal(discount, deal.DiscountPct);
    }

    // ---- DealLine (through the aggregate root) ------------------------------------------------------

    [Theory]
    [InlineData(" ", 1, 1, "productRef")]
    [InlineData("SKU", -1, 1, "unitPrice")]
    [InlineData("SKU", 1, 0, "quantity")]
    [InlineData("SKU", 1, -3, "quantity")]
    public void Line_guards_name_the_field(string productRef, int unitPrice, int quantity, string field)
    {
        var deal = new Deal(Guid.NewGuid(), NewAccount(), "d", 1m, 0m);
        Assert.Equal(field, FieldOf(() => deal.AddLine(productRef, unitPrice, quantity, null)));
        Assert.Empty(deal.Lines);
    }

    [Fact]
    public void Line_accepts_a_zero_unit_price_and_a_quantity_of_one()
    {
        var deal = new Deal(Guid.NewGuid(), NewAccount(), "d", 1m, 0m);
        var line = deal.AddLine("SKU", 0m, 1, null).Line!;
        Assert.Equal(0m, line.UnitPrice);
        Assert.Equal(1, line.Quantity);
    }
}
