using Marquee.Api.Auth;
using Marquee.Domain.Entities;

namespace Marquee.Api.Dtos;

/// <summary>
/// What the user pool will accept in a password, so a form can say so before it is submitted. Served
/// from the bound options rather than restated in the client, so the hint has one place to change.
/// </summary>
public sealed record PasswordRulesDto(int MinLength, bool RequireDigit);

/// <summary>
/// An issued anonymous session (Iteration 5). <c>SessionId</c> is returned alongside the token
/// purely so a client can show "you are clapping as a guest" — the token is the credential.
/// </summary>
public sealed record AnonymousSessionResponse(string SessionId, string Token, DateTime ExpiresAtUtc);

public sealed record UserDto(
    Guid Id,
    string Username,
    string Email,
    string? Bio,
    bool IsPrivate,
    string Role,
    /// <summary>Null for anyone who has not set a picture — the client draws a monogram instead.</summary>
    string? AvatarUrl,
    /// <summary>
    /// What the account may do, from its role now — the same answer the API authorises against. The
    /// client reads these instead of decoding its token, which carries none (DEPLOYMENT.md § Phase 2,
    /// decision 7).
    /// </summary>
    IReadOnlyList<string> Permissions)
{
    public static UserDto From(User u) =>
        new(u.Id, u.Username, u.Email, u.Bio, u.IsPrivate, u.Role.ToString(), u.AvatarUrl,
            RolePermissions.For(u.Role));
}
