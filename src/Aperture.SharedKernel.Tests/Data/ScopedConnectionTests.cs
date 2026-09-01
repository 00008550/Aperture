using System.Data.Common;
using System.Reflection;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;

namespace Aperture.SharedKernel.Tests.Data;

/// <summary>
/// The public surface of <see cref="ScopedConnection"/>, asserted by reflection (009-P4). The
/// structural guarantee — a raw read cannot be issued unscoped — rests on the shape of this type's
/// API as much as on RLS: there must be no read overload that omits the <see cref="DataScopeSet"/>
/// or the <see cref="ScopeColumns"/>, no exposed raw <see cref="DbConnection"/>, and no write path.
/// The behavioural proof that RLS actually filters lives in the Access test project against a real
/// container (edges 16, 17, 18, 19); these guard the signature, which the plan calls expensive to
/// change later.
/// </summary>
public sealed class ScopedConnectionTests
{
    private static IEnumerable<MethodInfo> PublicInstanceMethods() =>
        typeof(ScopedConnection)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);

    [Fact]
    public void Every_read_method_requires_a_DataScopeSet_and_a_ScopeColumns()
    {
        foreach (var method in PublicInstanceMethods())
        {
            var parameterTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();

            Assert.True(
                parameterTypes.Contains(typeof(DataScopeSet)),
                $"{method.Name} is a public read method that does not take a DataScopeSet — an "
                + "unscoped raw read must be inexpressible.");
            Assert.True(
                parameterTypes.Contains(typeof(ScopeColumns)),
                $"{method.Name} is a public read method that does not take a ScopeColumns — the "
                + "scope columns must always be named.");
        }
    }

    [Fact]
    public void No_public_method_exposes_or_returns_a_raw_connection()
    {
        foreach (var method in PublicInstanceMethods())
        {
            Assert.False(
                typeof(DbConnection).IsAssignableFrom(UnwrapTask(method.ReturnType)),
                $"{method.Name} returns a DbConnection — the raw connection must never escape the "
                + "wrapper, or the scope guarantee is bypassable.");

            Assert.DoesNotContain(
                method.GetParameters(),
                p => typeof(DbConnection).IsAssignableFrom(p.ParameterType));
        }
    }

    [Fact]
    public void No_public_write_path_exists()
    {
        // Reads only (009 out-of-scope for writes). An Execute/ExecuteScalar overload would be a
        // write path, and adding one is a deliberate design change, not a drive-by.
        Assert.DoesNotContain(
            PublicInstanceMethods(),
            m => m.Name.StartsWith("Execute", StringComparison.Ordinal)
                 || m.Name.StartsWith("Insert", StringComparison.Ordinal)
                 || m.Name.StartsWith("Update", StringComparison.Ordinal)
                 || m.Name.StartsWith("Delete", StringComparison.Ordinal));
    }

    [Fact]
    public void The_only_public_methods_are_the_two_scoped_reads()
    {
        var names = PublicInstanceMethods().Select(m => m.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "QueryAsync", "QuerySingleOrDefaultAsync" }, names);
    }

    private static Type UnwrapTask(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)
            ? type.GetGenericArguments()[0]
            : type;
}
