using Aperture.Modules.Access.Domain;

namespace Aperture.Api.Authorization;

/// <summary>
/// Whether the request in flight is a human's or the assistant's, for the audit trail.
/// <para>
/// The mark rides on <see cref="HttpContext.Items"/> rather than on the caller's token or
/// principal, and that is deliberate. The request principal is rebuilt from the database and
/// nothing else (001-P3), so a claim could not survive to be read here; and letting a token name
/// its own actor kind would let a human's token forge "assistant" and mislabel the trail. The
/// assistant host (plan 007) invokes the domain in-process and sets the item when it does, which
/// is the one place that knows the truth.
/// </para>
/// </summary>
public static class AuditActor
{
    /// <summary>The item key the assistant host sets to mark its calls.</summary>
    public const string ItemKey = "aperture.actor-kind";

    /// <summary>Marks the current request as the assistant acting on the user's behalf.</summary>
    public static void MarkAsAssistant(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[ItemKey] = ActorKind.Assistant;
    }

    /// <summary>
    /// The actor kind for this request — <see cref="ActorKind.Assistant"/> only when the item
    /// was explicitly set, <see cref="ActorKind.Human"/> otherwise. Defaulting to human means an
    /// unmarked request is never mislabelled as the assistant.
    /// </summary>
    public static ActorKind KindFor(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var value) && value is ActorKind kind
            ? kind
            : ActorKind.Human;
    }
}
