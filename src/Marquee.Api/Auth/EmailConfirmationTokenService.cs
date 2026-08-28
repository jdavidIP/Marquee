using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Auth;

/// <summary>
/// Issues and validates the signed, stateless tokens a confirm-email link carries (issue #29).
///
/// Same shape as <see cref="AnonymousSessionService"/> and for the same reason: the token carries its
/// own subject and expiry and proves both with an HMAC, so there is nothing to store and nothing to
/// clean up. The payload here is a user id rather than a session id, and the domain-separation label
/// is its own — a token minted for one purpose must never validate for the other.
/// </summary>
public interface IEmailConfirmationTokenService
{
    string Issue(Guid userId);

    /// <summary>Validate a presented token and recover the user id it names. False for anything tampered with, malformed, or expired.</summary>
    bool TryValidate(string? token, out Guid userId);
}

public sealed class EmailConfirmationTokenService : IEmailConfirmationTokenService
{
    private const string DerivationLabel = "marquee-email-confirmation-v1";

    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;

    public EmailConfirmationTokenService(IOptions<EmailConfirmationOptions> options, IOptions<JwtOptions> jwtOptions)
    {
        var config = options.Value;
        _lifetime = TimeSpan.FromHours(config.TokenLifetimeHours);

        _key = string.IsNullOrWhiteSpace(config.SigningKey)
            ? HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(jwtOptions.Value.Key),
                Encoding.UTF8.GetBytes(DerivationLabel))
            : Encoding.UTF8.GetBytes(config.SigningKey);
    }

    public string Issue(Guid userId)
    {
        var expiryUnix = new DateTimeOffset(DateTime.UtcNow.Add(_lifetime)).ToUnixTimeSeconds();
        var payload = $"{userId:N}.{expiryUnix}";
        return $"{payload}.{Sign(payload)}";
    }

    public bool TryValidate(string? token, out Guid userId)
    {
        userId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var parts = token.Split('.');
        if (parts.Length != 3)
            return false;

        var payload = $"{parts[0]}.{parts[1]}";

        // Fixed-time comparison, same reasoning as AnonymousSessionService.TryValidate: a byte-by-byte
        // early exit would leak how much of a forged signature was correct.
        var expected = Encoding.UTF8.GetBytes(Sign(payload));
        var presented = Encoding.UTF8.GetBytes(parts[2]);
        if (!CryptographicOperations.FixedTimeEquals(expected, presented))
            return false;

        if (!long.TryParse(parts[1], out var expiryUnix))
            return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expiryUnix) <= DateTimeOffset.UtcNow)
            return false;

        return Guid.TryParse(parts[0], out userId);
    }

    private string Sign(string payload) =>
        Base64Url(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
