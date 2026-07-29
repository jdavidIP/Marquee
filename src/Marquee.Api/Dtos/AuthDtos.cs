using System.ComponentModel.DataAnnotations;
using Marquee.Domain.Entities;

namespace Marquee.Api.Dtos;

public sealed record RegisterRequest(
    [Required, MinLength(3), MaxLength(50)] string Username,
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(8), MaxLength(128)] string Password);

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
