using System.Collections.Frozen;

namespace Aperture.SharedKernel.Authorization;

/// <summary>
/// The permission registry — the one list the API endpoints, the React console and the AI
/// assistant's tool definitions all agree on (ARCHITECTURE.md §3).
/// <para>
/// Permissions answer <em>may this user perform this action</em>. They never answer
/// <em>which rows</em>; that is <see cref="DataScopeSet"/>. Conflating the two is what
/// produced the region leak recorded in DOMAIN.md §5.1.
/// </para>
/// <para>
/// Business logic checks permissions, never roles. A role is an administrative grouping owned
/// by the Access module, and nothing outside it needs to know roles exist.
/// </para>
/// </summary>
public static class Permissions
{
    public const string AccountsRead = "accounts.read";
    public const string AccountsWrite = "accounts.write";
    public const string ContactsRead = "contacts.read";
    public const string ContactsWrite = "contacts.write";
    public const string DealsRead = "deals.read";
    public const string DealsWrite = "deals.write";
    public const string DealsApproveDiscount = "deals.discount.approve";
    public const string OrdersRead = "orders.read";
    public const string OrdersWrite = "orders.write";
    public const string OrdersConfirm = "orders.confirm";

    /// <summary>
    /// Separate from <see cref="OrdersConfirm"/> on purpose: DOMAIN.md §2 requires a credit
    /// override to be recorded with who and why, which means it must be independently grantable.
    /// </summary>
    public const string OrdersCreditOverride = "orders.credit.override";

    public const string TimelineRead = "timeline.read";
    public const string TimelineWrite = "timeline.write";
    public const string AssistantUse = "assistant.use";
    public const string AdminUsers = "admin.users";
    public const string AdminIntegrations = "admin.integrations";
    public const string AuditRead = "audit.read";

    /// <summary>Every declared permission. Ordinal, because permissions are exact strings.</summary>
    public static readonly FrozenSet<string> All = new[]
    {
        AccountsRead,
        AccountsWrite,
        ContactsRead,
        ContactsWrite,
        DealsRead,
        DealsWrite,
        DealsApproveDiscount,
        OrdersRead,
        OrdersWrite,
        OrdersConfirm,
        OrdersCreditOverride,
        TimelineRead,
        TimelineWrite,
        AssistantUse,
        AdminUsers,
        AdminIntegrations,
        AuditRead,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>True when <paramref name="permission"/> is one this system actually declares.</summary>
    public static bool IsDeclared(string? permission) =>
        permission is not null && All.Contains(permission);
}
