using System.Collections.Frozen;

namespace Aperture.SharedKernel.Authorization;

/// <summary>
/// The permissions a principal holds, in one tenant. Immutable and ordinal.
/// <para>
/// Every unknown input denies: a null, an empty string, a permission this system does not
/// declare, or one differing only in case. Case-insensitive matching would make
/// <c>Orders.Confirm</c> grant <c>orders.confirm</c>, which turns a typo in a seed script into
/// a privilege grant.
/// </para>
/// </summary>
public sealed class PermissionSet
{
    /// <summary>Holds nothing and allows nothing. The default for an unresolved principal.</summary>
    public static readonly PermissionSet None = new(FrozenSet<string>.Empty);

    private readonly FrozenSet<string> _permissions;

    private PermissionSet(FrozenSet<string> permissions) => _permissions = permissions;

    public static PermissionSet Of(params string[] permissions) =>
        Of((IEnumerable<string>)permissions);

    public static PermissionSet Of(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        // Undeclared strings are dropped rather than stored: a permission that no longer
        // exists must not keep granting anything after it is removed from the registry.
        var declared = permissions
            .Where(Permissions.IsDeclared)
            .ToFrozenSet(StringComparer.Ordinal);

        return declared.Count == 0 ? None : new PermissionSet(declared);
    }

    public int Count => _permissions.Count;

    public IReadOnlyCollection<string> Values => _permissions;

    public bool Allows(string? permission) =>
        !string.IsNullOrEmpty(permission) && _permissions.Contains(permission);
}
