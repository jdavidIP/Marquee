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
    /// <summary>Null for anyone who has not set a picture — the client draws a monogram instead.</summary>
    string? AvatarUrl,
    bool IsPrivate,
    DateTime CreatedAt,
    int MoviesCollected,
    int PremieresAttended,
    int FriendCount,
    /// <summary>Pending / Accepted / null — the viewer's own relationship to this profile.</summary>
    string? FriendshipStatus,
    /// <summary>True when the viewer sent the pending request, false when they received it.</summary>
    bool? FriendRequestOutgoing,
    /// <summary>
    /// Premieres both the viewer and this account contributed to. Null when there is no viewer to
    /// share with — anonymous, or the account's own profile — same as FriendshipStatus.
    /// </summary>
    int? SharedPremieresAttended);

/// <summary>
/// A private profile seen by a stranger. MARQUEE_PLAN.md is explicit that the *account's own*
/// fields — movie counts, join date, friend count — are "omitted from the payload entirely, not
/// returned as nulls" — hence a separate type rather than a nulled-out <see cref="FullProfileDto"/>.
/// A null field still tells the reader the field exists and leaks its shape; an absent one says
/// nothing at all.
///
/// FriendshipStatus and FriendRequestOutgoing are the one exception, added so a client can offer a
/// working "add friend" action here rather than only being able to say "you cannot see this
/// profile". They describe the *viewer's own relationship* to the account, not the account itself —
/// the same distinction the plan already draws by saying privacy restricts detail, not existence:
/// the account's own detail stays withheld; the viewer's own standing does not.
///
/// The profile still resolves rather than 404ing: privacy restricts detail, not existence.
/// </summary>
public sealed record LimitedProfileDto(
    string Username,
    /// <summary>
    /// Kept here: a picture is part of the public identity a private account still presents, the
    /// same as its name. Privacy restricts the account's own detail — counts, history, bio — not
    /// the face on the door. Bio itself is withheld now (the profile badge's "unissued" state
    /// prints name only), unlike AvatarUrl.
    /// </summary>
    string? AvatarUrl,
    /// <summary>
    /// Pending or null. Never Accepted — an accepted friend is entitled to the full profile, so
    /// this type is never the one returned for them.
    /// </summary>
    string? FriendshipStatus,
    /// <summary>True when the viewer sent the pending request, false when they received it.</summary>
    bool? FriendRequestOutgoing,
    /// <summary>
    /// Same viewer-relative exception as FriendshipStatus: describes the viewer's own overlap with
    /// this account, not the account's private detail, so it survives the privacy restriction —
    /// it's what a locked library's teaser line reads from. Null when there is no viewer to share
    /// with (anonymous).
    /// </summary>
    int? SharedPremieresAttended);

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
