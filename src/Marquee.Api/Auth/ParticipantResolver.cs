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
        // A signed-in user always wins over a leftover anonymous session header — otherwise a user
        // could clap once as themselves and again as their pre-login session. But which participant
        // kind they win *as* depends on confirmation (issue #29): an unconfirmed account is treated
        // fully as an anonymous session, not as a registered user with a flag. The session id is
        // derived rather than random because it has to stay the same across every clap this account
        // makes for the anonymous cap (§4.2) to mean anything — a fresh id per request would let an
        // unconfirmed signed-in user clap past it by never repeating an identity.
        if (context.User.GetUserId() is Guid userId)
        {
            return context.User.IsEmailConfirmed()
                ? Participant.Registered(userId)
                : Participant.Anonymous(UnconfirmedSessionId(userId));
        }

        var token = context.Request.Headers[_headerName].ToString();
        return sessions.TryValidate(token, out var sessionId)
            ? Participant.Anonymous(sessionId)
            : null;
    }

    /// <summary>
    /// Stable per-account id an unconfirmed user's claps accumulate against. Prefixed so it can never
    /// collide with a genuine, randomly-issued AnonymousSessionService id, and embeds the user id
    /// directly rather than hashing it — there is nothing to hide here, the server already knows
    /// exactly who this is from the JWT that got them here.
    /// </summary>
    private static string UnconfirmedSessionId(Guid userId) => $"unconfirmed:{userId:N}";
}
