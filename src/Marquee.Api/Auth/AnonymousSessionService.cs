using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Auth;

/// <summary>An issued anonymous session, as handed back to the client.</summary>
public sealed record AnonymousSession(string SessionId, string Token, DateTime ExpiresAtUtc);

/// <summary>
/// Issues and validates the short-lived tokens that let a visitor clap without an account
/// (MARQUEE_PLAN.md, Iteration 5).
///
/// ## Why a signed token rather than a random opaque one
///
/// The session is never stored server-side, so there is nothing to look up and nothing to clean up
/// — the token carries its own id and expiry and proves both with an HMAC. That matters because the
/// whole point of anonymous participation is that it costs the system almost nothing: a table of
/// anonymous sessions would be a write on every first page load, which is exactly the kind of work
/// this design is trying to avoid.
///
/// What the signature buys is that a session id cannot be *invented*. A visitor cannot mint a
/// thousand ids from a loop and spread their claps across them to defeat the per-participant cap
/// (§4.2) — each one has to be requested from the issuing endpoint, which is itself rate limited.
/// That is the honest limit of this defence: it raises the cost of Sybil behaviour rather than
/// removing it, since a determined bot can still collect tokens one at a time. Making that harder
/// (proof of work, attestation) is out of scope for v1.
/// </summary>
public interface IAnonymousSessionService
{
    AnonymousSession Issue();

    /// <summary>
    /// Validate a presented token and recover its session id. Returns false for anything tampered
    /// with, malformed, or expired.
    /// </summary>
    bool TryValidate(string? token, out string sessionId);
}

public sealed class AnonymousSessionService : IAnonymousSessionService
{
    /// <summary>
    /// Domain-separation label for the derived key. Signing anonymous sessions with the raw JWT key
    /// would mean one secret authenticating two different kinds of credential; deriving a subkey
    /// keeps them cryptographically independent, so a weakness in one cannot forge the other.
    /// </summary>
    private const string DerivationLabel = "marquee-anonymous-session-v1";

    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;

    public AnonymousSessionService(IOptions<AnonymousSessionOptions> options, IOptions<JwtOptions> jwtOptions)
    {
        var config = options.Value;
        _lifetime = TimeSpan.FromMinutes(config.LifetimeMinutes);

        _key = string.IsNullOrWhiteSpace(config.SigningKey)
            ? HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(jwtOptions.Value.Key),
                Encoding.UTF8.GetBytes(DerivationLabel))
            : Encoding.UTF8.GetBytes(config.SigningKey);
    }

    public AnonymousSession Issue()
    {
        var sessionId = Base64Url(RandomNumberGenerator.GetBytes(16));
        var expiresAt = DateTime.UtcNow.Add(_lifetime);
        var expiryUnix = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();

        var payload = $"{sessionId}.{expiryUnix}";
        return new AnonymousSession(sessionId, $"{payload}.{Sign(payload)}", expiresAt);
    }

    public bool TryValidate(string? token, out string sessionId)
    {
        sessionId = "";
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var parts = token.Split('.');
        if (parts.Length != 3)
            return false;

        var payload = $"{parts[0]}.{parts[1]}";

        // Fixed-time comparison: a byte-by-byte early exit would leak how much of a forged signature
        // was correct, which is enough to reconstruct one guess at a time.
        var expected = Encoding.UTF8.GetBytes(Sign(payload));
        var presented = Encoding.UTF8.GetBytes(parts[2]);
        if (!CryptographicOperations.FixedTimeEquals(expected, presented))
            return false;

        if (!long.TryParse(parts[1], out var expiryUnix))
            return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expiryUnix) <= DateTimeOffset.UtcNow)
            return false;

        sessionId = parts[0];
        return sessionId.Length > 0;
    }

    private string Sign(string payload) =>
        Base64Url(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));

    // Base64url so the token is safe in headers and URLs without escaping, and so no '.' can appear
    // inside a segment and break the three-part split above.
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
