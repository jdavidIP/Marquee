using System.ComponentModel.DataAnnotations;
using Marquee.Domain.Entities;

namespace Marquee.Api.Dtos;

/// <summary>
/// Note what is <em>not</em> annotated: <c>Password</c> carries no length attribute at all. Every
/// rule about a password's content belongs to <c>PasswordPolicy</c> in Marquee.Domain (issue #27),
/// and a second copy here would be one more thing to remember when a threshold is retuned in
/// configuration — and one that password reset (#31) could not share.
/// </summary>
public sealed record RegisterRequest(
    [Required, MinLength(3), MaxLength(50)] string Username,
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required] string Password,
    [Required] string ConfirmPassword);

/// <summary>One rejected rule, so a client can list the reasons rather than concatenate them.</summary>
public sealed record PasswordProblemDto(string Rule, string Message);

/// <summary>
/// What the server will accept, so a form can say so before it is submitted. Served from the bound
/// options rather than restated in the client, which is the only way the hint stays true after a
/// threshold is retuned. Deliberately describes only the countable rules — publishing the blocklist
/// would be handing over a dictionary.
/// </summary>
public sealed record PasswordRulesDto(
    int MinLength,
    int MaxLength,
    bool RequireLetter,
    bool RequireDigit);

public sealed record LoginRequest(
    [Required] string UsernameOrEmail,
    [Required] string Password);

public sealed record AuthResponse(string Token, UserDto User);

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
    string Role)
{
    public static UserDto From(User u) =>
        new(u.Id, u.Username, u.Email, u.Bio, u.IsPrivate, u.Role.ToString());
}
