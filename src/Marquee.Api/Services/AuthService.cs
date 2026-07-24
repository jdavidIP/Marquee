using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Marquee.Api.Services;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct);
    Task<AuthResponse?> LoginAsync(LoginRequest request, CancellationToken ct);
}

/// <summary>Result of a failed registration surfaced as a domain exception the controller maps to 409.</summary>
public sealed class RegistrationConflictException(string message) : Exception(message);

public sealed class AuthService(
    MarqueeDbContext db,
    IPasswordHasherService passwordHasher,
    IJwtTokenService tokens) : IAuthService
{
    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var username = request.Username.Trim();
        var email = request.Email.Trim().ToLowerInvariant();

        var clash = await db.Users
            .AnyAsync(u => u.Username == username || u.Email == email, ct);
        if (clash)
            throw new RegistrationConflictException("Username or email is already taken.");

        var user = new User
        {
            Username = username,
            Email = email,
            Role = UserRole.User
        };
        user.PasswordHash = passwordHasher.Hash(user, request.Password);

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race on the unique index between the check and the insert.
            throw new RegistrationConflictException("Username or email is already taken.");
        }

        return new AuthResponse(tokens.CreateToken(user), UserDto.From(user));
    }

    public async Task<AuthResponse?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var id = request.UsernameOrEmail.Trim();
        var idLower = id.ToLowerInvariant();

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Username == id || u.Email == idLower, ct);
        if (user is null || user.IsBlocked)
            return null;

        if (!passwordHasher.Verify(user, user.PasswordHash, request.Password))
            return null;

        return new AuthResponse(tokens.CreateToken(user), UserDto.From(user));
    }
}
