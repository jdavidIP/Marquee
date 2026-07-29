using Marquee.Domain.Enums;

namespace Marquee.Domain.Entities;

/// <summary>
/// A directed friend request that becomes an undirected friendship once accepted (CLAUDE.md §3).
/// The row keeps its original direction forever — <see cref="RequesterId"/> is whoever asked — so
/// "who initiated" stays answerable, but an <see cref="FriendshipStatus.Accepted"/> row means the
/// two users are friends regardless of which column each sits in. Every read that asks "are these
/// two friends" therefore has to check both orderings; <see cref="Pair"/> exists so callers do that
/// consistently rather than each inventing their own comparison.
/// </summary>
public class Friendship : AuditableEntity
{
    public Guid RequesterId { get; set; }
    public User Requester { get; set; } = null!;

    public Guid AddresseeId { get; set; }
    public User Addressee { get; set; } = null!;

    public FriendshipStatus Status { get; set; } = FriendshipStatus.Pending;

    /// <summary>
    /// The two participants in a stable (ordered) form, so a caller can compare two friendships or
    /// build a lookup key without caring who sent the request.
    /// </summary>
    public (Guid Low, Guid High) Pair => Order(RequesterId, AddresseeId);

    public static (Guid Low, Guid High) Order(Guid a, Guid b) =>
        a.CompareTo(b) <= 0 ? (a, b) : (b, a);

    /// <summary>The other party, from the point of view of <paramref name="viewerId"/>.</summary>
    public Guid OtherThan(Guid viewerId) => viewerId == RequesterId ? AddresseeId : RequesterId;
}
