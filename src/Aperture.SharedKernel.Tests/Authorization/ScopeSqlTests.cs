using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.SharedKernel.Tests.Authorization;

/// <summary>
/// 009-P2, the text half: the raw-SQL fragment says the same thing as
/// <see cref="ScopeQuerying.ToPredicate{T}"/>, and — the property that matters most for a
/// string-built <c>WHERE</c> — no scope value ever reaches the SQL as a literal.
/// <para>
/// These are unit tests over the emitted text and parameter bag; there is no database here. That
/// the fragment and the EF predicate select the identical rows against real PostgreSQL is 009-P4's
/// differential test.
/// </para>
/// </summary>
public sealed class ScopeSqlTests
{
    private static readonly TenantId Acme = TenantId.New();
    private static readonly UserId Ivanov = UserId.New();
    private static readonly Guid TeamA = Guid.NewGuid();
    private static readonly Guid North = Guid.NewGuid();

    // Edge case 1: a single Self grant translates to exactly the tenant term AND the owner term,
    // each a bound parameter, and the bag holds exactly those two values.
    [Fact]
    public void A_single_self_grant_emits_the_tenant_term_and_the_owner_term_as_parameters()
    {
        var fragment = DataScopeSet.Of(Acme, new DataScope.Self(Ivanov))
            .ToSqlFragment(ScopeColumns.For("a"));

        Assert.Equal(
            "(a.tenant_id = @__scope_a_tenant) AND (a.owner_user_id = @__scope_a_p0)",
            fragment.Sql);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["__scope_a_tenant"] = Acme.Value,
                ["__scope_a_p0"] = Ivanov.Value,
            },
            fragment.Parameters);
    }

    // Edge case 2: two grants are OR-ed inside a single parenthesised group that is AND-ed with the
    // tenant term. Precedence is where this class of bug lives, so the parentheses are the assertion.
    [Fact]
    public void Two_grants_are_ored_inside_one_group_conjoined_with_the_tenant_term()
    {
        var fragment = DataScopeSet
            .Of(Acme, new DataScope.Team(TeamA), new DataScope.Region(North))
            .ToSqlFragment(ScopeColumns.For("a"));

        // Structure: (<tenant>) AND (<grant> OR <grant>). The union is wrapped so the tenant term
        // cannot be reached past by the OR.
        Assert.StartsWith("(a.tenant_id = @__scope_a_tenant) AND (", fragment.Sql, StringComparison.Ordinal);
        Assert.EndsWith(")", fragment.Sql, StringComparison.Ordinal);

        var union = UnionGroup(fragment.Sql);
        Assert.Contains(" OR ", union, StringComparison.Ordinal);
        // Exactly one OR — two grants, one union — and no stray tenant term leaked into it.
        Assert.Equal(2, union.Split(" OR ", StringSplitOptions.None).Length);
        Assert.DoesNotContain("tenant_id", union, StringComparison.Ordinal);

        Assert.Contains("a.team_id = @", union, StringComparison.Ordinal);
        Assert.Contains("a.region_id = @", union, StringComparison.Ordinal);
    }

    // Edge case 3, the fragment-text half: the empty set yields a fragment that matches nothing —
    // 1 = 0 present, tenant term still present — and is neither null nor empty. (The execution half,
    // "returns zero rows even when rows exist", is 009-P4 against a real database.)
    [Fact]
    public void The_empty_scope_set_yields_a_fragment_that_matches_nothing()
    {
        var fragment = DataScopeSet.None(Acme).ToSqlFragment(ScopeColumns.For("a"));

        Assert.NotNull(fragment.Sql);
        Assert.NotEqual(string.Empty, fragment.Sql);
        Assert.Equal("(a.tenant_id = @__scope_a_tenant) AND (1 = 0)", fragment.Sql);
        Assert.DoesNotContain("TRUE", fragment.Sql, StringComparison.Ordinal);
    }

    // Edge case 6: duplicate grants collapse, and a set built in a different order translates to the
    // same parameter values — set semantics survive translation.
    [Fact]
    public void Duplicate_and_reordered_grants_produce_the_same_parameters()
    {
        var duplicated = DataScopeSet
            .Of(Acme, new DataScope.Team(TeamA), new DataScope.Team(TeamA))
            .ToSqlFragment(ScopeColumns.For("a"));
        var single = DataScopeSet.Of(Acme, new DataScope.Team(TeamA))
            .ToSqlFragment(ScopeColumns.For("a"));

        Assert.Equal(single.Sql, duplicated.Sql);
        Assert.Equal(single.Parameters, duplicated.Parameters);

        var oneOrder = DataScopeSet
            .Of(Acme, new DataScope.Team(TeamA), new DataScope.Region(North))
            .ToSqlFragment(ScopeColumns.For("a"));
        var otherOrder = DataScopeSet
            .Of(Acme, new DataScope.Region(North), new DataScope.Team(TeamA))
            .ToSqlFragment(ScopeColumns.For("a"));

        // The set is unordered, so compare the parameter values as a set, not by name-to-name order.
        Assert.Equal(
            oneOrder.Parameters.Values.OrderBy(v => v?.ToString(), StringComparer.Ordinal),
            otherOrder.Parameters.Values.OrderBy(v => v?.ToString(), StringComparer.Ordinal));
    }

    // Edge case 7: no scope value appears as a literal — every one is a parameter. Asserted by
    // searching the fragment text for each value's Guid string form. Injection and plan-cache both.
    [Fact]
    public void No_scope_value_appears_as_a_literal_in_the_fragment()
    {
        var fragment = DataScopeSet
            .Of(Acme, new DataScope.Self(Ivanov), new DataScope.Team(TeamA), new DataScope.Region(North))
            .ToSqlFragment(ScopeColumns.For("a"));

        foreach (var value in new[] { Acme.Value, Ivanov.Value, TeamA, North })
        {
            Assert.DoesNotContain(value.ToString(), fragment.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Edge case 8: two calls with different aliases, AND-ed into one query, have no colliding
    // parameter names — the prefix is caller-scoped by the alias.
    [Fact]
    public void Fragments_with_different_aliases_do_not_share_parameter_names()
    {
        var orders = DataScopeSet.Of(Acme, new DataScope.Self(Ivanov))
            .ToSqlFragment(ScopeColumns.For("o"));
        var lines = DataScopeSet.Of(Acme, new DataScope.Team(TeamA))
            .ToSqlFragment(ScopeColumns.For("l"));

        Assert.Empty(orders.Parameters.Keys.Intersect(lines.Parameters.Keys, StringComparer.Ordinal));
    }

    // Edge case 9: a non-identifier alias is an ArgumentException, never a sanitised string and
    // never a silent default. Column-name overloads are validated the same way.
    [Theory]
    [InlineData("o; DROP")]
    [InlineData("")]
    [InlineData("o.owner")]
    [InlineData("o owner")]
    [InlineData("\"o\"")]
    public void A_non_identifier_alias_is_rejected(string alias)
    {
        Assert.ThrowsAny<ArgumentException>(() => ScopeColumns.For(alias));
    }

    [Fact]
    public void A_null_alias_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => ScopeColumns.For(null!));
    }

    [Fact]
    public void A_non_identifier_column_name_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => ScopeColumns.For("o", "tenant_id; DROP", "owner_user_id", "team_id", "region_id", "account_id"));
    }

    // Edge case 15: the fragment is a WHERE clause only — no ordering, no paging, no statement
    // terminator. A translator that quietly appended ORDER BY would break the caller's keyset cursor.
    [Fact]
    public void The_fragment_carries_no_ordering_paging_or_terminator()
    {
        var fragments = new[]
        {
            DataScopeSet.None(Acme).ToSqlFragment(ScopeColumns.For("a")),
            DataScopeSet.Of(Acme, new DataScope.AllTenant()).ToSqlFragment(ScopeColumns.For("a")),
            DataScopeSet
                .Of(Acme, new DataScope.Self(Ivanov), new DataScope.Team(TeamA), new DataScope.Region(North))
                .ToSqlFragment(ScopeColumns.For("a")),
        };

        foreach (var fragment in fragments)
        {
            Assert.DoesNotContain("ORDER BY", fragment.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LIMIT", fragment.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(";", fragment.Sql, StringComparison.Ordinal);
        }
    }

    // AllTenant emits TRUE inside the union, leaving tenant_id = @t AND (TRUE): everything inside the
    // tenant, nothing outside it. The tenant term is still a bound parameter, not a literal.
    [Fact]
    public void All_tenant_emits_true_inside_the_tenant_bounded_union()
    {
        var fragment = DataScopeSet.Of(Acme, new DataScope.AllTenant())
            .ToSqlFragment(ScopeColumns.For("a"));

        Assert.Equal("(a.tenant_id = @__scope_a_tenant) AND (TRUE)", fragment.Sql);
        Assert.Equal(
            new Dictionary<string, object?> { ["__scope_a_tenant"] = Acme.Value },
            fragment.Parameters);
    }

    // Returns the text inside the final AND (...) group.
    private static string UnionGroup(string sql)
    {
        var marker = ") AND (";
        var start = sql.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return sql[start..^1];
    }
}
