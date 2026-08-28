using System.Security.Cryptography;
using System.Text;

namespace Marquee.Api.Auth;

/// <summary>
/// Generates and hashes password reset tokens (issue #31). Deliberately not a signed, self-verifying
/// token like AnonymousSessionService or EmailConfirmationTokenService — this one is opaque random
/// data, checked purely by looking up its hash in PasswordResetToken, because single-use and
/// invalidate-on-reset both require a server that remembers what it already handed out.
///
/// Hashing here is SHA-256, not the slow, salted PasswordHasherService used for account passwords.
/// That is deliberate, not a shortcut: PasswordHasherService exists to resist offline brute force
/// against a low-entropy human password. A reset token is already 256 bits of cryptographic
/// randomness — guessing it by brute force is infeasible regardless of hash speed — so the hash here
/// exists only to stop a leaked database backup from handing over working reset links, and a fast
/// cryptographic hash is the right tool for that.
/// </summary>
public interface IPasswordResetTokenService
{
    /// <summary>A fresh, high-entropy raw token. Callers persist Hash(token), never this value itself.</summary>
    string GenerateToken();

    /// <summary>The value actually stored at rest.</summary>
    string Hash(string token);
}

public sealed class PasswordResetTokenService : IPasswordResetTokenService
{
    public string GenerateToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
