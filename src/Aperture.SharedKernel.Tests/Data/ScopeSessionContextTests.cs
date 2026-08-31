using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.SharedKernel.Tests.Data;

/// <summary>
/// 009-P3, the builder half: a <see cref="DataScopeSet"/> becomes the six <c>set_config</c> settings
/// the row-security policy reads. That those settings then admit exactly the in-scope rows is proven
/// at the DBMS by the Access module's <c>ScopeRlsTests</c>; here we pin the invariants that are
/// properties of the emitted SQL, not of any row — chiefly that no scope value is ever a literal.
/// </summary>
public sealed class ScopeSessionContextTests
{
    private static readonly TenantId Acme = TenantId.New();
    private static readonly UserId Ivanov = UserId.New();
    private static readonly Guid TeamA = Guid.NewGuid();

    [Fact]
    public void It_emits_a_set_config_for_each_of_the_six_settings()
    {
        var session = ScopeSessionContext.Build(
            DataScopeSet.Of(Acme, new DataScope.Self(Ivanov), new DataScope.AllTenant()));

        foreach (var setting in new[]
                 {
                     ScopeSessionContext.TenantIdSetting,
                     ScopeSessionContext.UserIdSetting,
                     ScopeSessionContext.TeamsSetting,
                     ScopeSessionContext.RegionsSetting,
                     ScopeSessionContext.AccountsSetting,
                     ScopeSessionContext.AllTenantSetting,
                 })
        {
            Assert.Contains($"set_config('{setting}'", session.Sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_scope_value_appears_as_a_literal_in_the_sql()
    {
        var team = Guid.NewGuid();
        var session = ScopeSessionContext.Build(
            DataScopeSet.Of(Acme, new DataScope.Self(Ivanov), new DataScope.Team(team)));

        // Every value is carried by a bound parameter — the injection property. The SQL text must
        // not contain the guid or tenant strings anywhere.
        Assert.DoesNotContain(Acme.Value.ToString(), session.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Ivanov.Value.ToString(), session.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(team.ToString(), session.Sql, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(Acme.Value.ToString(), session.Parameters["tenant"]);
        Assert.Equal(Ivanov.Value.ToString(), session.Parameters["user"]);
        Assert.Equal(team.ToString(), session.Parameters["teams"]);
    }

    [Fact]
    public void The_empty_set_still_carries_the_tenant_but_no_grants()
    {
        var session = ScopeSessionContext.Build(DataScopeSet.None(Acme));

        Assert.Equal(Acme.Value.ToString(), session.Parameters["tenant"]);
        Assert.Equal(string.Empty, session.Parameters["user"]);
        Assert.Equal(string.Empty, session.Parameters["teams"]);
        Assert.Equal(string.Empty, session.Parameters["regions"]);
        Assert.Equal(string.Empty, session.Parameters["accounts"]);
        Assert.Equal("false", session.Parameters["all_tenant"]);
    }

    [Fact]
    public void All_tenant_sets_the_flag_true()
    {
        var session = ScopeSessionContext.Build(DataScopeSet.Of(Acme, new DataScope.AllTenant()));

        Assert.Equal("true", session.Parameters["all_tenant"]);
    }

    [Fact]
    public void Multiple_teams_are_carried_as_one_comma_separated_setting()
    {
        var teamB = Guid.NewGuid();
        var session = ScopeSessionContext.Build(
            DataScopeSet.Of(Acme, new DataScope.Team(TeamA), new DataScope.Team(teamB)));

        var teams = (string)session.Parameters["teams"]!;
        Assert.Contains(TeamA.ToString(), teams, StringComparison.Ordinal);
        Assert.Contains(teamB.ToString(), teams, StringComparison.Ordinal);
        Assert.Contains(",", teams, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_distinct_self_grants_cannot_be_expressed_and_fail_loud()
    {
        // The singular app.user_id setting cannot represent two principals; dropping one would widen
        // the deny, so it throws rather than fails open. (In practice Self is the principal, so a set
        // holds at most one — this guards the degenerate case.)
        var scopes = DataScopeSet.Of(Acme, new DataScope.Self(Ivanov), new DataScope.Self(UserId.New()));

        Assert.Throws<ArgumentException>(() => ScopeSessionContext.Build(scopes));
    }

    [Fact]
    public void It_rejects_a_null_scope_set()
    {
        Assert.Throws<ArgumentNullException>(() => ScopeSessionContext.Build(null!));
    }
}
