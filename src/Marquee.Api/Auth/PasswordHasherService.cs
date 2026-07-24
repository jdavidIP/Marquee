using Marquee.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Marquee.Api.Auth;

public interface IPasswordHasherService
{
    string Hash(User user, string password);
    bool Verify(User user, string hash, string password);
}

/// <summary>
/// Wraps ASP.NET Core Identity's PBKDF2 <see cref="PasswordHasher{TUser}"/> (CLAUDE.md iteration 1
/// allows the Identity hasher or Argon2). Kept behind an interface so the algorithm can change later.
/// </summary>
public sealed class PasswordHasherService : IPasswordHasherService
{
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    public bool Verify(User user, string hash, string password) =>
        _hasher.VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed;
}
