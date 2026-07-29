using Marquee.Domain;

namespace Marquee.Infrastructure.Redis;

public enum IdempotencyState
{
    /// <summary>This caller reserved the key and owns the work. Nobody has run it before.</summary>
    Reserved,
    /// <summary>The work already completed; <c>Payload</c> is the original response, replay it verbatim.</summary>
    Replay,
    /// <summary>Another request with this key is still running. The caller should retry, not double-count.</summary>
    InFlight
}

/// <summary>
/// Result of trying to claim an idempotency key. <see cref="Payload"/> is set only for
/// <see cref="IdempotencyState.Replay"/>.
/// </summary>
public readonly record struct IdempotencyClaim(IdempotencyState State, string? Payload);

/// <summary>
/// The two anti-abuse guards that sit in front of counting a clap (MARQUEE_PLAN.md, Iteration 5).
/// Both are Redis-only by design: they are hot-path checks on the most contended endpoint in the
/// system, and neither is something Postgres should ever be asked about (CLAUDE.md §7).
///
/// They answer different questions and both are needed. Debouncing rejects a *second* clap that
/// arrived too soon — it is about pacing a real participant. Idempotency recognises a *retry of the
/// same* clap — a network hiccup, a double-fire, a client retry policy — and must not be counted
/// twice nor rejected as too fast, because logically it is one clap, not two.
/// </summary>
public interface IClapGuards
{
    /// <summary>
    /// Atomically claim the participant's debounce window (SET NX PX). Returns true when the clap
    /// may proceed, false when it arrived inside the minimum interval since the last one.
    /// </summary>
    Task<bool> TryPassDebounceAsync(
        string scopeId, Guid premiereId, Participant participant, TimeSpan minInterval, CancellationToken ct);

    /// <summary>
    /// Claim an idempotency key, or discover that it has already been used. The reservation is
    /// atomic (SET NX), so of two concurrent requests carrying the same key exactly one is told to
    /// do the work and the other is told to wait or replay — never both count.
    /// </summary>
    Task<IdempotencyClaim> ClaimIdempotencyAsync(
        string scopeId, Guid premiereId, Participant participant, string key, TimeSpan ttl, CancellationToken ct);

    /// <summary>Publish the result against a reserved key so later retries replay it.</summary>
    Task CompleteIdempotencyAsync(
        string scopeId, Guid premiereId, Participant participant, string key, string payload,
        TimeSpan ttl, CancellationToken ct);

    /// <summary>
    /// Drop a reservation whose work failed, so the caller's retry is allowed to run rather than
    /// being told "in flight" until the TTL expires.
    /// </summary>
    Task ReleaseIdempotencyAsync(
        string scopeId, Guid premiereId, Participant participant, string key, CancellationToken ct);
}
