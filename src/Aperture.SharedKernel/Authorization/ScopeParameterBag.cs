namespace Aperture.SharedKernel.Authorization;

/// <summary>
/// Collects the bound parameters for one <see cref="ScopeSql.ToSqlFragment"/> call (009-P2). Each
/// scope value is registered here and referenced in the SQL only by its <c>@name</c>, so no value
/// is ever inlined as a literal — an injection property and a plan-cache property at once.
/// <para>
/// Parameter names are prefixed with the table alias (<c>__scope_{alias}_…</c>). The alias is a
/// validated identifier and is unique per scoped table in a query, so two fragments <c>AND</c>-ed
/// into the same statement cannot collide on a parameter name.
/// </para>
/// </summary>
internal sealed class ScopeParameterBag
{
    private readonly string _prefix;
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private int _ordinal;

    internal ScopeParameterBag(string alias) => _prefix = $"__scope_{alias}";

    /// <summary>Registers the tenant value and returns its <c>@name</c> placeholder.</summary>
    internal string AddTenant(Guid value)
    {
        var name = $"{_prefix}_tenant";
        _values[name] = value;
        return "@" + name;
    }

    /// <summary>Registers a grant value and returns its <c>@name</c> placeholder.</summary>
    internal string Add(object? value)
    {
        var name = $"{_prefix}_p{_ordinal++}";
        _values[name] = value;
        return "@" + name;
    }

    /// <summary>An immutable copy of the collected parameters, keyed by name (without the <c>@</c>).</summary>
    internal IReadOnlyDictionary<string, object?> Snapshot() =>
        new Dictionary<string, object?>(_values, StringComparer.Ordinal);
}
