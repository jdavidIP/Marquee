namespace Marquee.Api.Auth;

/// <summary>
/// The Cognito user pool the API accepts access tokens from (DEPLOYMENT.md § Phase 2, decision 9).
/// Locally this is cognito-local (docker-compose.yml); in production, MarqueeAuthStack's outputs.
/// </summary>
public sealed class CognitoOptions
{
    public const string SectionName = "Cognito";

    /// <summary>
    /// The pool's issuer, e.g. https://cognito-idp.ca-central-1.amazonaws.com/ca-central-1_xxx. Tokens
    /// must carry exactly this <c>iss</c>, and the signing keys are discovered under it.
    /// </summary>
    public string Issuer { get; set; } = "";

    /// <summary>
    /// The web app client. Access tokens carry no <c>aud</c>, so this is checked against their
    /// <c>client_id</c> instead: a token the pool issued to any other client is refused.
    /// </summary>
    public string ClientId { get; set; } = "";

    // Both derive from the issuer rather than being configured beside it, so they cannot disagree
    // with it. Its origin is the cognito-idp endpoint, for the real service and cognito-local alike,
    // and its path is the pool id.
    public string ServiceUrl => new Uri(Issuer).GetLeftPart(UriPartial.Authority);
    public string UserPoolId => new Uri(Issuer).AbsolutePath.Trim('/');
}
