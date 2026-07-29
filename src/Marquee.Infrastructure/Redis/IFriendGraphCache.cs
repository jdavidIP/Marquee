namespace Marquee.Infrastructure.Redis;

/// <summary>
/// The Redis mirror of the accepted-friendship graph (CLAUDE.md §3, <c>user:{userId}:friends</c>).
/// Postgres remains the record; this exists so "which of my friends contributed" is a single
/// server-side SINTER instead of a join against a set that grows with a Premiere's popularity.
///
/// Every write is applied to *both* users' sets: friendship is undirected once accepted, even
/// though the <c>Friendship</c> row keeps the direction it was created with.
/// </summary>
public interface IFriendGraphCache
{
    /// <summary>
    /// Whether this user's set has been populated from Postgres. False after a Redis restart, and
    /// false for a user whose set was never built — both mean "rebuild before trusting a read".
    /// </summary>
    Task<bool> IsLoadedAsync(Guid userId, CancellationToken ct);

    /// <summary>Replace the user's cached friends wholesale and mark the set loaded.</summary>
    Task LoadAsync(Guid userId, IReadOnlyCollection<Guid> friendIds, CancellationToken ct);

    /// <summary>Record an accepted friendship on both sides.</summary>
    Task LinkAsync(Guid a, Guid b, CancellationToken ct);

    /// <summary>Remove a friendship from both sides.</summary>
    Task UnlinkAsync(Guid a, Guid b, CancellationToken ct);

    Task<IReadOnlyList<Guid>> GetFriendsAsync(Guid userId, CancellationToken ct);
}
