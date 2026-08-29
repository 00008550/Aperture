using Aperture.SharedKernel.Multitenancy;

namespace Aperture.SharedKernel.Tests.Multitenancy;

public sealed class AmbientTenantContextTests
{
    // 7. Given no ambient tenant, when ITenantContext.TenantId is read, then it throws.
    [Fact]
    public void Reading_the_tenant_without_a_scope_throws()
    {
        var context = new AmbientTenantContext();

        Assert.False(context.HasTenant);
        Assert.Throws<TenantContextMissingException>(() => context.TenantId);
    }

    // 8. Given a nested tenant scope, when it is disposed, then the outer tenant is restored.
    [Fact]
    public void Disposing_a_nested_scope_restores_the_outer_tenant()
    {
        var context = new AmbientTenantContext();
        var outer = TenantId.New();
        var inner = TenantId.New();

        using (AmbientTenantContext.Begin(outer))
        {
            Assert.Equal(outer, context.TenantId);

            using (AmbientTenantContext.Begin(inner))
            {
                Assert.Equal(inner, context.TenantId);
            }

            Assert.Equal(outer, context.TenantId);
        }

        Assert.False(context.HasTenant);
    }

    [Fact]
    public async Task Concurrent_scopes_do_not_leak_into_each_other()
    {
        var context = new AmbientTenantContext();
        var first = TenantId.New();
        var second = TenantId.New();

        async Task<TenantId> ObserveAsync(TenantId tenantId)
        {
            using (AmbientTenantContext.Begin(tenantId))
            {
                await Task.Yield();
                await Task.Delay(10);
                return context.TenantId;
            }
        }

        var results = await Task.WhenAll(ObserveAsync(first), ObserveAsync(second));

        Assert.Equal(first, results[0]);
        Assert.Equal(second, results[1]);
        Assert.False(context.HasTenant);
    }
}
