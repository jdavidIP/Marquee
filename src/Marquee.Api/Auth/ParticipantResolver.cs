using Marquee.Domain;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Auth;

/// <summary>
/// Works out who is making a request: a signed-in user, a visitor holding a valid anonymous
/// session, or nobody. Several places need this answer for the same request — the rate limiter
/// partitions on it, the clap endpoint counts against it — so the result is cached in
/// <see cref="HttpContext.Items"/> rather than re-verifying the token's HMAC each time.
/// </summary>
public interface IParticipantResolver
{
    /// <summary>The participant behind this request, or null if it is neither authenticated nor carrying a valid session.</summary>
    Participant? Resolve(HttpContext context);
}

public sealed class ParticipantResolver(
    IAnonymousSessionService sessions,
    IOptions<AnonymousSessionOptions> options) : IParticipantResolver
{
    private const string CacheKey = "marquee.participant";
    private readonly string _headerName = options.Value.HeaderName;

    public Participant? Resolve(HttpContext context)
    {
        if (context.Items.TryGetValue(CacheKey, out var cached))
            return (Participant?)cached;

        var resolved = ResolveCore(context);
        context.Items[CacheKey] = resolved;
        return resolved;
    }

    private Participant? ResolveCore(HttpContext context)
    {
        // A signed-in user always wins. If a browser happens to still be holding an anonymous
        // session from before it logged in, the account is the identity that matters — otherwise a
        // user could clap once as themselves and again as their leftover session.
        if (context.User.GetUserId() is Guid userId)
            return Participant.Registered(userId);

        var token = context.Request.Headers[_headerName].ToString();
        return sessions.TryValidate(token, out var sessionId)
            ? Participant.Anonymous(sessionId)
            : null;
    }
}
