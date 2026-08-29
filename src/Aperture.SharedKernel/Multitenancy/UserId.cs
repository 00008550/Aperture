namespace Aperture.SharedKernel.Multitenancy;

/// <summary>The acting user. Strongly typed for the same reason as <see cref="TenantId"/>.</summary>
public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
