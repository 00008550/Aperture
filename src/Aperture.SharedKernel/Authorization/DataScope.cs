using Aperture.SharedKernel.Multitenancy;

namespace Aperture.SharedKernel.Authorization;

/// <summary>
/// One grant of row-level access (ARCHITECTURE.md §3). A closed hierarchy — the private
/// constructor means the only cases are the ones declared here, so an exhaustive switch in a
/// future SQL translator (001-P4) stays exhaustive.
/// </summary>
public abstract record DataScope
{
    private DataScope()
    {
    }

    /// <summary>Whether this single grant admits <paramref name="resource"/>.</summary>
    public abstract bool Admits(IScopedResource resource);

    /// <summary>Rows the user owns.</summary>
    public sealed record Self(UserId UserId) : DataScope
    {
        public override bool Admits(IScopedResource resource) =>
            resource.OwnerUserId == UserId;
    }

    /// <summary>Rows owned by a team. A row with no team is not admitted.</summary>
    public sealed record Team(Guid TeamId) : DataScope
    {
        public override bool Admits(IScopedResource resource) =>
            resource.TeamId is { } team && team == TeamId;
    }

    /// <summary>Rows in a region. A row with no region is not admitted.</summary>
    public sealed record Region(Guid RegionId) : DataScope
    {
        public override bool Admits(IScopedResource resource) =>
            resource.RegionId is { } region && region == RegionId;
    }

    /// <summary>One named account — for a key-account handler.</summary>
    public sealed record Account(Guid AccountId) : DataScope
    {
        public override bool Admits(IScopedResource resource) =>
            resource.AccountId is { } account && account == AccountId;
    }

    /// <summary>
    /// Everything inside the tenant. Explicit and auditable, never implied by an absent filter —
    /// the difference between this and "no scopes" is the whole point of the design.
    /// The tenant boundary itself is enforced by <see cref="DataScopeSet"/>, not here.
    /// </summary>
    public sealed record AllTenant : DataScope
    {
        public override bool Admits(IScopedResource resource) => true;
    }
}
