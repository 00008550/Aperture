using Aperture.SharedKernel.Authorization;

namespace Aperture.SharedKernel.Tests.Authorization;

public sealed class PermissionSetTests
{
    // 9. Given a permission set, when an unknown permission string is checked, then it denies.
    [Theory]
    [InlineData("deals.delete")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("*")]
    public void Unknown_permissions_are_denied(string? permission)
    {
        var set = PermissionSet.Of(Permissions.DealsRead, Permissions.OrdersRead);

        Assert.False(set.Allows(permission));
    }

    [Fact]
    public void Undeclared_permissions_are_not_stored()
    {
        var set = PermissionSet.Of(Permissions.DealsRead, "deals.delete");

        Assert.Equal(1, set.Count);
        Assert.True(set.Allows(Permissions.DealsRead));
    }

    // 10. Given a permission set, when a permission differing only in case is checked, then it
    //     denies — permissions are exact, ordinal strings.
    [Fact]
    public void Permission_matching_is_ordinal_not_case_insensitive()
    {
        var set = PermissionSet.Of(Permissions.OrdersConfirm);

        Assert.True(set.Allows("orders.confirm"));
        Assert.False(set.Allows("Orders.Confirm"));
        Assert.False(set.Allows("ORDERS.CONFIRM"));
    }

    [Fact]
    public void The_empty_set_allows_nothing_it_was_asked_about()
    {
        Assert.Equal(0, PermissionSet.None.Count);

        foreach (var permission in Permissions.All)
        {
            Assert.False(PermissionSet.None.Allows(permission));
        }
    }

    [Fact]
    public void Credit_override_is_not_implied_by_confirm()
    {
        // DOMAIN.md §2: finance overrides a credit limit, and that is a separate grant from
        // confirming an order. A hierarchy here would silently give every seller the override.
        var seller = PermissionSet.Of(Permissions.OrdersRead, Permissions.OrdersConfirm);

        Assert.True(seller.Allows(Permissions.OrdersConfirm));
        Assert.False(seller.Allows(Permissions.OrdersCreditOverride));
    }

    // 11. Given the registry, when it is enumerated, then every declared permission is unique
    //     and non-empty.
    [Fact]
    public void Every_declared_permission_is_unique_and_non_empty()
    {
        var declared = typeof(Permissions)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType: { } t } && t == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(declared);
        Assert.All(declared, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        Assert.Equal(declared.Count, declared.Distinct(StringComparer.Ordinal).Count());

        // The constants and the All set must not drift apart — a constant missing from All is
        // a permission nothing can be granted, and one in All with no constant is a typo.
        Assert.Equal(
            declared.OrderBy(p => p, StringComparer.Ordinal),
            Permissions.All.OrderBy(p => p, StringComparer.Ordinal));
    }
}
