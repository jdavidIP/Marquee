namespace Marquee.Infrastructure.Messaging;

/// <summary>One registered participant's contribution to a Premiere, as carried on the wire.</summary>
public sealed record ContributorClaps(Guid UserId, int Claps);

/// <summary>
/// One anonymous participant's contribution (Iteration 5). Carries a session id, never a user id —
/// an anonymous participant is not an account and is never linked to one. They count towards the
/// threshold and are persisted as a Contribution row, but earn no emblem and no library entry
/// (CLAUDE.md §4.3).
/// </summary>
public sealed record AnonymousContributorClaps(string SessionId, int Claps);

/// <summary>
/// Published by the API the moment a Premiere is marked opened, and consumed by
/// <c>Marquee.Worker</c>, which does the expensive fan-out (Iteration 4).
///
/// The event carries the **full clap snapshot** rather than a pointer to it. That is deliberate:
/// the API deletes the Premiere's Redis keys once the open commits, so a message that referenced
/// Redis would become unprocessable the moment those keys went. A self-contained event can be
/// replayed at any point in the future and produce the identical outcome — which is exactly the
/// property the idempotency acceptance criteria demand.
///
/// The tradeoff is message size: a Premiere with 10k contributors serialises to a few hundred KB.
/// Well inside RabbitMQ's limits, but if scopes ever grow past that, the fix is to page the
/// fan-out into several events keyed by contributor range, not to reintroduce a Redis dependency.
/// </summary>
public sealed record PremiereOpened(
    Guid PremiereId,
    string ScopeId,
    Guid MovieId,
    /// <summary>Opened or AutoOpened — the worker records the emblem the same way either (§4.5).</summary>
    string Status,
    int Threshold,
    /// <summary>Per-participant cap, the denominator of the emblem calculation (§4.2, §4.3).</summary>
    int RegisteredClapCap,
    /// <summary>Registered and anonymous claps together — what the Premiere actually drew.</summary>
    int TotalClaps,
    DateTime OpenedAt,
    IReadOnlyList<ContributorClaps> Contributors,
    /// <summary>
    /// Added in Iteration 5. Nullable on the wire on purpose: an event published by a pre-Iteration-5
    /// API and still sitting in the outbox or the queue during a deploy has no such field, and must
    /// deserialise and fan out rather than dead-letter. Consumers coalesce it to empty.
    /// </summary>
    IReadOnlyList<AnonymousContributorClaps>? AnonymousContributors = null);

/// <summary>
/// Published by the worker once the fan-out is durably committed, and consumed by the API, which
/// owns the SignalR connections and turns it into the reveal broadcast.
///
/// The hop back exists because v1 runs SignalR without a Redis backplane (CLAUDE.md §6): only the
/// API process can reach the connected clients. Routing the reveal through the queue also preserves
/// the invariant the inline version had — nobody is told a Premiere opened until the movie is
/// durably theirs.
/// </summary>
public sealed record PremiereRevealReady(
    Guid PremiereId,
    string ScopeId,
    Guid MovieId,
    string Status,
    int TotalClaps,
    int Threshold,
    int Contributors,
    DateTime OpenedAt);
