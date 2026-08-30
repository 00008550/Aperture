using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.SharedKernel.Tests.Authorization;

/// <summary>
/// 001-P4, the semantic half: the predicate says the same thing as
/// <see cref="DataScopeSet.Admits"/>. The other half — that it is evaluated by the database and
/// not by the client — cannot be shown here, and is asserted against real SQL in
/// <c>Aperture.Modules.Access.Tests.ScopePredicateSqlTests</c>.
/// </summary>
public sealed class ScopeQueryingTests
{
    private static readonly TenantId Acme = TenantId.New();
    private static readonly TenantId Globex = TenantId.New();
    private static readonly UserId Ivanov = UserId.New();
    private static readonly UserId Petrova = UserId.New();
    private static readonly Guid TeamA = Guid.NewGuid();
    private static readonly Guid TeamB = Guid.NewGuid();
    private static readonly Guid North = Guid.NewGuid();
    private static readonly Guid South = Guid.NewGuid();
    private static readonly Guid KeyAccount = Guid.NewGuid();
    private static readonly Guid OtherAccount = Guid.NewGuid();

    private sealed class Deal : IScopedResource
    {
        public TenantId TenantId { get; init; }

        public UserId OwnerUserId { get; init; }

        public Guid? TeamId { get; init; }

        public Guid? RegionId { get; init; }

        public Guid? AccountId { get; init; }
    }

    private static Func<Deal, bool> Compile(DataScopeSet scopes) =>
        scopes.ToPredicate<Deal>().Compile();

    private static Deal Row(
        TenantId tenant,
        UserId? owner = null,
        Guid? team = null,
        Guid? region = null,
        Guid? account = null) =>
        new()
        {
            TenantId = tenant,
            OwnerUserId = owner ?? Petrova,
            TeamId = team,
            RegionId = region,
            AccountId = account,
        };

    // The empty set producing a predicate that matches nothing. (DOMAIN.md §5.1, in SQL form.)
    [Fact]
    public void The_empty_scope_set_produces_a_predicate_that_matches_nothing()
    {
        var predicate = Compile(DataScopeSet.None(Acme));

        Assert.False(predicate(Row(Acme, Ivanov, TeamA, North, KeyAccount)));
        Assert.False(predicate(Row(Acme)));
        Assert.False(predicate(Row(Globex)));
    }

    [Fact]
    public void The_self_scope_matches_only_rows_the_user_owns()
    {
        var predicate = Compile(DataScopeSet.Of(Acme, new DataScope.Self(Ivanov)));

        Assert.True(predicate(Row(Acme, Ivanov)));
        Assert.False(predicate(Row(Acme, Petrova)));
    }

    [Fact]
    public void The_team_scope_matches_only_its_own_team_and_never_a_null_team()
    {
        var predicate = Compile(DataScopeSet.Of(Acme, new DataScope.Team(TeamA)));

        Assert.True(predicate(Row(Acme, team: TeamA)));
        Assert.False(predicate(Row(Acme, team: TeamB)));
        Assert.False(predicate(Row(Acme, team: null)));
    }

    [Fact]
    public void The_region_scope_matches_only_its_own_region_and_never_a_null_region()
    {
        var predicate = Compile(DataScopeSet.Of(Acme, new DataScope.Region(North)));

        Assert.True(predicate(Row(Acme, region: North)));
        Assert.False(predicate(Row(Acme, region: South)));
        Assert.False(predicate(Row(Acme, region: null)));
    }

    [Fact]
    public void The_account_scope_matches_only_its_own_account_and_never_a_null_account()
    {
        var predicate = Compile(DataScopeSet.Of(Acme, new DataScope.Account(KeyAccount)));

        Assert.True(predicate(Row(Acme, account: KeyAccount)));
        Assert.False(predicate(Row(Acme, account: OtherAccount)));
        Assert.False(predicate(Row(Acme, account: null)));
    }

    [Fact]
    public void The_all_tenant_scope_matches_every_row_in_the_tenant_and_no_row_outside_it()
    {
        var predicate = Compile(DataScopeSet.Of(Acme, new DataScope.AllTenant()));

        Assert.True(predicate(Row(Acme)));
        Assert.True(predicate(Row(Acme, Ivanov, TeamB, South, OtherAccount)));
        Assert.False(predicate(Row(Globex)));
    }

    [Fact]
    public void Scopes_compose_as_a_union_in_the_predicate_too()
    {
        var predicate = Compile(
            DataScopeSet.Of(Acme, new DataScope.Team(TeamA), new DataScope.Region(North)));

        Assert.True(predicate(Row(Acme, team: TeamA, region: South)));
        Assert.True(predicate(Row(Acme, team: TeamB, region: North)));
        Assert.False(predicate(Row(Acme, team: TeamB, region: South)));
    }

    [Fact]
    public void The_tenant_boundary_is_not_reachable_past_by_any_scope()
    {
        var predicate = Compile(
            DataScopeSet.Of(
                Acme,
                new DataScope.AllTenant(),
                new DataScope.Self(Ivanov),
                new DataScope.Team(TeamA)));

        Assert.False(predicate(Row(Globex, Ivanov, TeamA)));
    }

    // The predicate and the in-memory rule are one rule with two forms. If they can disagree on
    // any row, one of the two call sites in the codebase is wrong, and it is unknowable which.
    [Fact]
    public void The_predicate_agrees_with_the_in_memory_rule_on_every_combination()
    {
        DataScopeSet[] sets =
        [
            DataScopeSet.None(Acme),
            DataScopeSet.Of(Acme, new DataScope.Self(Ivanov)),
            DataScopeSet.Of(Acme, new DataScope.Team(TeamA)),
            DataScopeSet.Of(Acme, new DataScope.Region(North)),
            DataScopeSet.Of(Acme, new DataScope.Account(KeyAccount)),
            DataScopeSet.Of(Acme, new DataScope.AllTenant()),
            DataScopeSet.Of(Acme, new DataScope.Team(TeamA), new DataScope.Region(North)),
        ];

        TenantId[] tenants = [Acme, Globex];
        UserId[] owners = [Ivanov, Petrova];
        Guid?[] teams = [null, TeamA, TeamB];
        Guid?[] regions = [null, North, South];
        Guid?[] accounts = [null, KeyAccount, OtherAccount];

        foreach (var set in sets)
        {
            var predicate = Compile(set);

            foreach (var tenant in tenants)
            foreach (var owner in owners)
            foreach (var team in teams)
            foreach (var region in regions)
            foreach (var account in accounts)
            {
                var row = Row(tenant, owner, team, region, account);
                Assert.Equal(set.Admits(row), predicate(row));
            }
        }
    }

    [Fact]
    public void A_type_without_the_scope_properties_fails_loudly_rather_than_filtering_nothing()
    {
        // Explicit implementations are invisible to a query provider, so they are rejected here
        // rather than silently evaluated on the client.
        var error = Assert.Throws<InvalidOperationException>(
            () => DataScopeSet.Of(Acme, new DataScope.Self(Ivanov)).ToPredicate<HiddenDeal>());

        Assert.Contains("IScopedResource", error.Message, StringComparison.Ordinal);
    }

    private sealed class HiddenDeal : IScopedResource
    {
        TenantId IScopedResource.TenantId => Acme;

        UserId IScopedResource.OwnerUserId => Ivanov;

        Guid? IScopedResource.TeamId => null;

        Guid? IScopedResource.RegionId => null;

        Guid? IScopedResource.AccountId => null;
    }
}
