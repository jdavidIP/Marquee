namespace Marquee.Domain;

public enum ParticipantKind
{
    Registered = 0,
    Anonymous = 1
}

/// <summary>
/// Whoever is clapping — a signed-in user, or a visitor holding a short-lived anonymous session
/// (MARQUEE_PLAN.md, Iteration 5). Both contribute to a Premiere's threshold and both are
/// cap-enforced, so everything on the counting path wants to treat them uniformly; only the
/// *rewards* differ (§4.3: anonymous participants earn nothing).
///
/// An anonymous session is deliberately not a user: it is never persisted to the users table and
/// never linked to an account. It exists only so a visitor can be counted, capped, throttled, and
/// recorded as a <c>Contribution</c> row.
/// </summary>
public readonly record struct Participant
{
    private Participant(ParticipantKind kind, Guid userId, string? anonymousSessionId)
    {
        Kind = kind;
        UserId = userId;
        AnonymousSessionId = anonymousSessionId;
    }

    public ParticipantKind Kind { get; }

    /// <summary>Set only when <see cref="Kind"/> is Registered; otherwise <see cref="Guid.Empty"/>.</summary>
    public Guid UserId { get; }

    /// <summary>Set only when <see cref="Kind"/> is Anonymous.</summary>
    public string? AnonymousSessionId { get; }

    public bool IsAnonymous => Kind == ParticipantKind.Anonymous;

    public static Participant Registered(Guid userId) =>
        new(ParticipantKind.Registered, userId, null);

    public static Participant Anonymous(string sessionId) =>
        string.IsNullOrWhiteSpace(sessionId)
            ? throw new ArgumentException("An anonymous session id is required.", nameof(sessionId))
            : new Participant(ParticipantKind.Anonymous, Guid.Empty, sessionId);

    /// <summary>
    /// A stable, collision-free identity fragment for keys that partition by participant — rate
    /// limiting, debouncing, idempotency. The prefix keeps the two namespaces apart so a session id
    /// can never be confused with a user id.
    /// </summary>
    public string KeyPart => Kind == ParticipantKind.Registered ? $"u:{UserId}" : $"a:{AnonymousSessionId}";

    public override string ToString() => KeyPart;
}
