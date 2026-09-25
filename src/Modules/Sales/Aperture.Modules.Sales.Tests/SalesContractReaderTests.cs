using System.Reflection;
using Aperture.Contracts.Sales;
using Aperture.Modules.Sales.Application;
using Aperture.Modules.Sales.Domain;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aperture.Modules.Sales.Tests;

/// <summary>
/// Plan 003-P1, against a real PostgreSQL: the won-deal and credit-limit reads Sales offers other modules
/// through <c>Aperture.Contracts</c>. A won deal yields a snapshot; a visible non-won deal is distinguishable
/// (<see cref="WonDealLookupStatus.NotWon"/>); unknown, cross-tenant, out-of-scope and empty-scope lookups are
/// all the same <see cref="WonDealLookupStatus.NotFound"/>. The credit read is scope-filtered the same way and
/// reads the limit live. The contract surface names no module type.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SalesContractReaderTests(PostgresFixture postgres)
{
    private ScopedConnection Reader() =>
        new(NpgsqlDataSource.Create(postgres.ReaderConnectionString), NullLogger<ScopedConnection>.Instance);

    private AccountService AccountsFor(TenantId tenant) => new(postgres.CreateContext(tenant), Reader());

    private DealService DealsFor(TenantId tenant) =>
        new(postgres.CreateContext(tenant), Reader(), new ConfiguredDiscountThresholdProvider(100m));

    private SalesContractReader ContractsFor(TenantId tenant) => new(postgres.CreateContext(tenant));

    private async Task<AccountView> NewAccountAsync(
        TenantId tenant,
        UserId owner,
        string name = "Initech",
        decimal creditLimit = 1000m,
        Guid? region = null,
        Guid? team = null)
    {
        var result = await AccountsFor(tenant).CreateAsync(
            tenant,
            owner,
            new CreateAccountRequest(name, $"TX-{Guid.NewGuid():N}", creditLimit, 30, region, team));
        Assert.Equal(AccountCreateStatus.Created, result.Status);
        return result.Account!;
    }

    /// <summary>A deal under <paramref name="accountId"/> with two priced lines, walked forward to
    /// <paramref name="targetStage"/> (<c>new</c> through <c>won</c>).</summary>
    private async Task<Guid> DealAtAsync(TenantId tenant, DataScopeSet scopes, Guid accountId, string targetStage)
    {
        var created = await DealsFor(tenant).CreateAsync(
            scopes, new CreateDealRequest(accountId, "Initech rollout", 5000m, 5m));
        Assert.Equal(DealCreateStatus.Created, created.Status);
        var dealId = created.Deal!.Id;

        Assert.Equal(DealLineAddStatus.Added, (await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-B", 25m, 4, "v1"))).Status);
        Assert.Equal(DealLineAddStatus.Added, (await DealsFor(tenant).AddLineAsync(
            scopes, dealId, new AddDealLineRequest("SKU-A", 100m, 2, "v1"))).Status);

        if (targetStage == Deal.Stages.New)
        {
            return dealId;
        }

        string[] path = [Deal.Stages.Qualified, Deal.Stages.Quoted, Deal.Stages.Negotiation, Deal.Stages.Won];
        foreach (var stage in path)
        {
            var version = stage == Deal.Stages.Quoted ? "v1" : null;
            var step = await DealsFor(tenant).TransitionAsync(
                scopes, dealId, new TransitionDealRequest(stage, PriceListVersion: version));
            Assert.Equal(DealTransitionOutcome.Transitioned, step.Outcome);

            if (stage == targetStage)
            {
                break;
            }
        }

        return dealId;
    }

    // ---- Won deal -> snapshot -----------------------------------------------------------------------

    [Fact]
    public async Task A_won_deal_yields_a_snapshot_with_ids_tenant_scope_facts_account_name_and_lines()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var team = Guid.NewGuid();
        var region = Guid.NewGuid();
        var account = await NewAccountAsync(tenant, owner, "Initech", region: region, team: team);
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));
        var dealId = await DealAtAsync(tenant, scopes, account.Id, Deal.Stages.Won);

        var lookup = await ContractsFor(tenant).GetWonDealAsync(scopes, dealId);

        Assert.Equal(WonDealLookupStatus.Won, lookup.Status);
        var snapshot = lookup.Deal!;
        Assert.Equal(dealId, snapshot.DealId);
        Assert.Equal(tenant, snapshot.TenantId);
        Assert.Equal(account.Id, snapshot.AccountId);
        Assert.Equal(owner, snapshot.OwnerUserId);
        Assert.Equal(team, snapshot.TeamId);
        Assert.Equal(region, snapshot.RegionId);
        Assert.Equal("Initech", snapshot.AccountName);
        Assert.Equal("Initech rollout", snapshot.DealName);
        Assert.Equal("v1", snapshot.FrozenPriceListVersion);
        Assert.Equal(
            [new WonDealLine("SKU-A", 100m, 2), new WonDealLine("SKU-B", 25m, 4)],
            snapshot.Lines);
    }

    [Fact]
    public async Task A_won_deal_is_visible_through_a_team_grant_as_well_as_the_owners_self_grant()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var team = Guid.NewGuid();
        var account = await NewAccountAsync(tenant, owner, team: team);
        var dealId = await DealAtAsync(
            tenant, DataScopeSet.Of(tenant, new DataScope.Self(owner)), account.Id, Deal.Stages.Won);

        var lookup = await ContractsFor(tenant).GetWonDealAsync(
            DataScopeSet.Of(tenant, new DataScope.Team(team)), dealId);

        Assert.Equal(WonDealLookupStatus.Won, lookup.Status);
    }

    // ---- Non-won is distinguishable from absent -----------------------------------------------------

    [Theory]
    [InlineData(Deal.Stages.New)]
    [InlineData(Deal.Stages.Quoted)]
    [InlineData(Deal.Stages.Negotiation)]
    public async Task A_visible_deal_that_is_not_won_is_NotWon_with_no_snapshot(string stage)
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner);
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));
        var dealId = await DealAtAsync(tenant, scopes, account.Id, stage);

        var lookup = await ContractsFor(tenant).GetWonDealAsync(scopes, dealId);

        Assert.Equal(WonDealLookupStatus.NotWon, lookup.Status);
        Assert.Null(lookup.Deal);
    }

    [Fact]
    public async Task A_lost_deal_is_NotWon()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner);
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));
        var dealId = await DealAtAsync(tenant, scopes, account.Id, Deal.Stages.Negotiation);
        var lost = await DealsFor(tenant).TransitionAsync(
            scopes, dealId, new TransitionDealRequest(Deal.Stages.Lost, Reason: "price"));
        Assert.Equal(DealTransitionOutcome.Transitioned, lost.Outcome);

        var lookup = await ContractsFor(tenant).GetWonDealAsync(scopes, dealId);

        Assert.Equal(WonDealLookupStatus.NotWon, lookup.Status);
    }

    // ---- Unknown / out-of-scope / cross-tenant / empty scope -> absent -------------------------------

    [Fact]
    public async Task An_unknown_deal_is_NotFound()
    {
        var tenant = TenantId.New();

        var lookup = await ContractsFor(tenant).GetWonDealAsync(
            DataScopeSet.Of(tenant, new DataScope.AllTenant()), Guid.NewGuid());

        Assert.Equal(WonDealLookupStatus.NotFound, lookup.Status);
        Assert.Null(lookup.Deal);
    }

    [Fact]
    public async Task A_won_deal_outside_the_callers_scope_is_NotFound()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner);
        var dealId = await DealAtAsync(
            tenant, DataScopeSet.Of(tenant, new DataScope.Self(owner)), account.Id, Deal.Stages.Won);

        var lookup = await ContractsFor(tenant).GetWonDealAsync(
            DataScopeSet.Of(tenant, new DataScope.Self(UserId.New())), dealId);

        Assert.Equal(WonDealLookupStatus.NotFound, lookup.Status);
    }

    [Fact]
    public async Task A_non_won_deal_outside_the_callers_scope_is_NotFound_not_NotWon()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner);
        var dealId = await DealAtAsync(
            tenant, DataScopeSet.Of(tenant, new DataScope.Self(owner)), account.Id, Deal.Stages.Negotiation);

        var lookup = await ContractsFor(tenant).GetWonDealAsync(
            DataScopeSet.Of(tenant, new DataScope.Self(UserId.New())), dealId);

        Assert.Equal(WonDealLookupStatus.NotFound, lookup.Status);
    }

    [Fact]
    public async Task A_won_deal_in_another_tenant_is_NotFound_even_with_AllTenant()
    {
        var home = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(home, owner);
        var dealId = await DealAtAsync(
            home, DataScopeSet.Of(home, new DataScope.Self(owner)), account.Id, Deal.Stages.Won);

        var other = TenantId.New();
        var lookup = await ContractsFor(other).GetWonDealAsync(
            DataScopeSet.Of(other, new DataScope.AllTenant()), dealId);

        Assert.Equal(WonDealLookupStatus.NotFound, lookup.Status);
    }

    [Fact]
    public async Task An_empty_scope_set_is_NotFound_never_everything()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner);
        var dealId = await DealAtAsync(
            tenant, DataScopeSet.Of(tenant, new DataScope.Self(owner)), account.Id, Deal.Stages.Won);

        var lookup = await ContractsFor(tenant).GetWonDealAsync(DataScopeSet.None(tenant), dealId);

        Assert.Equal(WonDealLookupStatus.NotFound, lookup.Status);
    }

    // ---- Credit limit: live and scope-filtered ------------------------------------------------------

    [Fact]
    public async Task The_credit_limit_of_an_account_in_scope_is_returned_and_read_live()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner, creditLimit: 1500m);
        var scopes = DataScopeSet.Of(tenant, new DataScope.Self(owner));

        Assert.Equal(1500m, await ContractsFor(tenant).GetCreditLimitAsync(scopes, account.Id));

        var updated = await AccountsFor(tenant).UpdateAsync(
            scopes,
            account.Id,
            new UpdateAccountRequest(owner.Value, account.Name, 2500m, 30, null, null, account.Version));
        Assert.Equal(AccountUpdateStatus.Updated, updated.Status);

        Assert.Equal(2500m, await ContractsFor(tenant).GetCreditLimitAsync(scopes, account.Id));
    }

    [Fact]
    public async Task The_credit_limit_outside_scope_cross_tenant_unknown_or_with_empty_scopes_is_null()
    {
        var tenant = TenantId.New();
        var owner = UserId.New();
        var account = await NewAccountAsync(tenant, owner, creditLimit: 1500m);
        var reader = ContractsFor(tenant);

        Assert.Null(await reader.GetCreditLimitAsync(
            DataScopeSet.Of(tenant, new DataScope.Self(UserId.New())), account.Id));
        Assert.Null(await reader.GetCreditLimitAsync(DataScopeSet.None(tenant), account.Id));
        Assert.Null(await reader.GetCreditLimitAsync(
            DataScopeSet.Of(tenant, new DataScope.AllTenant()), Guid.NewGuid()));

        var other = TenantId.New();
        Assert.Null(await ContractsFor(other).GetCreditLimitAsync(
            DataScopeSet.Of(other, new DataScope.AllTenant()), account.Id));
    }

    // ---- Wiring and the contract surface ------------------------------------------------------------

    [Fact]
    public void The_module_registers_both_contract_reads()
    {
        var services = new ServiceCollection();
        services.AddSalesModule(postgres.ConnectionString);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<SalesContractReader>(scope.ServiceProvider.GetRequiredService<IWonDealSource>());
        Assert.IsType<SalesContractReader>(scope.ServiceProvider.GetRequiredService<IAccountCreditReader>());
    }

    [Fact]
    public void No_module_type_is_reachable_from_the_contracts_assembly()
    {
        var contracts = typeof(IWonDealSource).Assembly;

        Assert.DoesNotContain(
            contracts.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("Aperture.Modules", StringComparison.Ordinal));

        var offending = new List<string>();
        foreach (var type in contracts.GetExportedTypes())
        {
            foreach (var reached in SurfaceTypes(type))
            {
                if (reached.Assembly.GetName().Name!.StartsWith("Aperture.Modules", StringComparison.Ordinal))
                {
                    offending.Add($"{type.FullName} -> {reached.FullName}");
                }
            }
        }

        Assert.Empty(offending);
    }

    private static IEnumerable<Type> SurfaceTypes(Type type)
    {
        const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var direct = new List<Type>();
        if (type.BaseType is not null)
        {
            direct.Add(type.BaseType);
        }

        direct.AddRange(type.GetInterfaces());
        direct.AddRange(type.GetProperties(Flags).Select(p => p.PropertyType));
        direct.AddRange(type.GetFields(Flags).Select(f => f.FieldType));
        foreach (var method in type.GetMethods(Flags))
        {
            direct.Add(method.ReturnType);
            direct.AddRange(method.GetParameters().Select(p => p.ParameterType));
        }

        foreach (var ctor in type.GetConstructors())
        {
            direct.AddRange(ctor.GetParameters().Select(p => p.ParameterType));
        }

        return direct.SelectMany(Expand).Distinct();
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.HasElementType)
        {
            return Expand(type.GetElementType()!);
        }

        return type.IsGenericType
            ? new[] { type }.Concat(type.GetGenericArguments().SelectMany(Expand))
            : [type];
    }
}
