namespace Aperture.SharedKernel.Multitenancy;

/// <summary>
/// The tenant a row belongs to. A struct rather than a raw <see cref="Guid"/> because the
/// cross-tenant defect class is "the right Guid passed as the wrong parameter", and the
/// compiler can eliminate it for the cost of one wrapper.
/// </summary>
public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
