using System.ComponentModel.DataAnnotations;

namespace Marquee.Api.Dtos;

/// <summary>
/// A profile as seen by someone entitled to the whole thing: the user themselves, an admin, an
/// accepted friend, or anyone at all when the profile is public.
/// </summary>
public sealed record FullProfileDto(
    Guid Id,
    string Username,
    string? Bio,
    bool IsPrivate,
    DateTime CreatedAt,
    int MoviesCollected,
    int PremieresAttended,
    int FriendCount,
    /// <summary>Pending / Accepted / null — the viewer's own relationship to this profile.</summary>
    string? FriendshipStatus,
    /// <summary>True when the viewer sent the pending request, false when they received it.</summary>
    bool? FriendRequestOutgoing);

/// <summary>
/// A private profile seen by a stranger. MARQUEE_PLAN.md is explicit that the other fields are
/// "omitted from the payload entirely, not returned as nulls" — hence a separate type rather than a
/// nulled-out <see cref="FullProfileDto"/>. A null field still tells the reader the field exists and
/// leaks its shape; an absent one says nothing at all.
///
/// The profile still resolves rather than 404ing: privacy restricts detail, not existence.
/// </summary>
public sealed record LimitedProfileDto(string Username, string? Bio);

/// <summary>
/// A search hit. Deliberately identical for public and private users — private profiles stay
/// discoverable (MARQUEE_PLAN.md), and these are the same two fields a stranger may see anyway.
/// </summary>
public sealed record UserSearchResultDto(Guid Id, string Username, string? Bio, bool IsPrivate);

/// <summary>
/// Partial update of the signed-in user's own profile. Both fields are optional so a client can
/// change one without having to send — and therefore risk clobbering — the other.
/// </summary>
public sealed record UpdateProfileRequest([MaxLength(500)] string? Bio, bool? IsPrivate);

public sealed record SendFriendRequestRequest([Required, MaxLength(50)] string Username);

public sealed record FriendRequestDto(
    Guid Id,
    Guid UserId,
    string Username,
    string Status,
    /// <summary>True when the signed-in user sent this request rather than received it.</summary>
    bool Outgoing,
    DateTime CreatedAt);

public sealed record FriendDto(Guid UserId, string Username, string? Bio, bool IsPrivate, DateTime FriendsSince);

/// <summary>
/// Which of the viewer's friends clapped for a Premiere. Answered per request, per viewer, and
/// never broadcast — see <c>PremieresController.Friends</c>.
/// </summary>
public sealed record FriendContributorDto(Guid UserId, string Username);

public sealed record FriendContributorsResponse(Guid PremiereId, IReadOnlyList<FriendContributorDto> Friends);
