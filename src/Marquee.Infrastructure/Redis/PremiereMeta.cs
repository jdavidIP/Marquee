using Marquee.Domain.Enums;

namespace Marquee.Infrastructure.Redis;

/// <summary>
/// The small, immutable-ish slice of a Premiere the clap hot path needs (threshold, caps, scope,
/// status). Cached in Redis so a clap never reads Postgres to decide whether/how to count — Redis
/// is the hot path, Postgres the durable record (CLAUDE.md §7).
/// </summary>
public sealed record PremiereMeta(
    Guid PremiereId,
    string ScopeId,
    PremiereStatus Status,
    int Threshold,
    int RegisteredCap,
    int AnonymousCap,
    Guid MovieId,
    DateTime? ExpiresAt);
