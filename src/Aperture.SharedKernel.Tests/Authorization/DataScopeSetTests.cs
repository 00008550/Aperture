using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.SharedKernel.Tests.Authorization;

/// <summary>
/// Test names map one-to-one onto docs/plans/001-authorization-spine.md § Edge cases.
/// </summary>
public sealed class DataScopeSetTests
{
    private static readonly TenantId Acme = TenantId.New();
    private static readonly TenantId Globex = TenantId.New();
    private static readonly UserId Ivanov = UserId.New();
    private static readonly UserId Petrova = UserId.New();
    private static readonly Guid TeamA = Guid.NewGuid();
    private static readonly Guid TeamB = Guid.NewGuid();
    private static readonly Guid North = Guid.NewGuid();
    private static readonly Guid South = Guid.NewGuid();

    private sealed record Deal(
        TenantId TenantId,
        UserId OwnerUserId,
        Guid? TeamId = null,
        Guid? RegionId = null,
        Guid? AccountId = null) : IScopedResource;

    // 1. Given a user with no scopes, when any row is evaluated, then it does not match.
    //    (DOMAIN.md §5.1 — the region leak.)
    [Fact]
    public void Empty_scope_set_admits_nothing()
    {
        var scopes = DataScopeSet.None(Acme);
        var ownRow = new Deal(Acme, Ivanov, TeamA, North);

        Assert.True(scopes.IsEmpty);
        Assert.False(scopes.Admits(ownRow));
    }

    // 2. Given a user with AllTenant, when a row from another tenant is evaluated,
    //    then it does not match.
    [Fact]
    public void All_tenant_scope_does_not_cross_the_tenant_boundary()
    {
        var scopes = DataScopeSet.Of(Acme, new DataScope.AllTenant());

        Assert.True(scopes.Admits(new Deal(Acme, Petrova, TeamB, South)));
        Assert.False(scopes.Admits(new Deal(Globex, Petrova, TeamB, South)));
    }

    // 3. Given a user with Self, when a row owned by another user is evaluated, then it does
    //    not match; and their own row matches.
    [Fact]
    public void Self_scope_admits_only_rows_the_user_owns()
    {
        var scopes = DataScopeSet.Of(Acme, new DataScope.Self(Ivanov));

        Assert.True(scopes.Admits(new Deal(Acme, Ivanov)));
        Assert.False(scopes.Admits(new Deal(Acme, Petrova)));
    }

    // 4. Given a user with Team(A) and Region(North), when a row in Team B / North is
    //    evaluated, then it matches — union, not intersection.
    [Fact]
    public void Scopes_compose_as_a_union_not_an_intersection()
    {
        var scopes = DataScopeSet.Of(Acme, new DataScope.Team(TeamA), new DataScope.Region(North));

        Assert.True(scopes.Admits(new Deal(Acme, Petrova, TeamB, North)));
        Assert.True(scopes.Admits(new Deal(Acme, Petrova, TeamA, South)));
        Assert.False(scopes.Admits(new Deal(Acme, Petrova, TeamB, South)));
    }

    // 5. Given a user with Team(A), when a row whose team is null is evaluated, then it does
    //    not match. (Absent data must narrow, never widen.)
    [Fact]
    public void Absent_ownership_data_does_not_widen_a_scope()
    {
        var scopes = DataScopeSet.Of(Acme, new DataScope.Team(TeamA));

        Assert.False(scopes.Admits(new Deal(Acme, Petrova, TeamId: null)));
        Assert.False(DataScopeSet.Of(Acme, new DataScope.Region(North))
            .Admits(new Deal(Acme, Petrova, RegionId: null)));
        Assert.False(DataScopeSet.Of(Acme, new DataScope.Account(Guid.NewGuid()))
            .Admits(new Deal(Acme, Petrova, AccountId: null)));
    }

    // 6. Given two scope sets containing the same scopes in a different order, then they are
    //    equal. (So caching and comparison are safe.)
    [Fact]
    public void Scope_sets_are_equal_regardless_of_order_and_duplication()
    {
        var first = DataScopeSet.Of(Acme, new DataScope.Team(TeamA), new DataScope.Region(North));
        var second = DataScopeSet.Of(
            Acme,
            new DataScope.Region(North),
            new DataScope.Team(TeamA),
            new DataScope.Team(TeamA));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void Scope_sets_for_different_tenants_are_not_equal()
    {
        var acme = DataScopeSet.Of(Acme, new DataScope.Team(TeamA));
        var globex = DataScopeSet.Of(Globex, new DataScope.Team(TeamA));

        Assert.NotEqual(acme, globex);
    }
}
