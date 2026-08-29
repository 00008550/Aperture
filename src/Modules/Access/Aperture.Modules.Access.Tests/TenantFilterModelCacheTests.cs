using Aperture.Modules.Access.Persistence;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Access.Tests;

/// <summary>
/// Guards a property the whole tenancy design rests on, and which is not obvious from reading
/// the code.
/// <para>
/// EF caches the model per options instance, while the context is registered scoped. The tenant
/// filter is built once, at model-build time, closing over the <em>first</em> context ever
/// created — so the question "does request two still filter by request two's tenant?" has a
/// non-obvious answer. It does, because the captured node is a property read that EF
/// re-evaluates per query rather than a value baked into the model.
/// </para>
/// <para>
/// Two changes would break that silently and no other test would notice: capturing
/// <c>tenant.TenantId</c> as a value instead of a property, or registering
/// <see cref="ITenantContext"/> as scoped so the captured instance belongs to one request.
/// Either way every request after the first would filter by the first request's tenant. Hence
/// this test. (001-P2 review, finding 2.)
/// </para>
/// </summary>
public sealed class TenantFilterModelCacheTests
{
    private sealed class MutableTenantContext : ITenantContext
    {
        public TenantId Value { get; } = TenantId.New();

        public bool HasTenant => true;

        public TenantId TenantId => Value;
    }

    [Fact]
    public void Contexts_sharing_one_options_instance_each_filter_by_their_own_tenant()
    {
        // One options instance means one model cache — exactly what AddDbContext produces.
        var options = new DbContextOptionsBuilder<AccessDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=model-only")
            .Options;

        var firstTenant = new MutableTenantContext();
        var secondTenant = new MutableTenantContext();

        using var first = new AccessDbContext(options, firstTenant);
        var firstSql = first.Memberships.ToQueryString();

        using var second = new AccessDbContext(options, secondTenant);
        var secondSql = second.Memberships.ToQueryString();

        // Positive and negative, so the test cannot pass by the tenant simply being absent.
        Assert.Contains(firstTenant.Value.Value.ToString(), firstSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(secondTenant.Value.Value.ToString(), secondSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(firstTenant.Value.Value.ToString(), secondSql, StringComparison.OrdinalIgnoreCase);
    }
}
