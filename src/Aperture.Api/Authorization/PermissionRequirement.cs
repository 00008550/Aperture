using Microsoft.AspNetCore.Authorization;

namespace Aperture.Api.Authorization;

/// <summary>One permission an endpoint demands. The string is exact and ordinal.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;

    public override string ToString() => $"Permission:{Permission}";
}
