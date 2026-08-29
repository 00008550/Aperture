using Aperture.SharedKernel.Multitenancy;

namespace Aperture.SharedKernel.Authorization;

/// <summary>
/// What a <see cref="DataScope"/> is evaluated against: the ownership facts of one row.
/// <para>
/// The optional members are optional because the data genuinely is — a deal need not sit in a
/// region, an order need not belong to a team. Absent data must <em>narrow</em>, never widen:
/// a row with no team is not in anybody's team scope.
/// </para>
/// </summary>
public interface IScopedResource
{
    TenantId TenantId { get; }

    UserId OwnerUserId { get; }

    Guid? TeamId { get; }

    Guid? RegionId { get; }

    Guid? AccountId { get; }
}
