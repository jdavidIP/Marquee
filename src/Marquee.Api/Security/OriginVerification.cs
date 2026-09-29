using System.Security.Cryptography;
using System.Text;

namespace Marquee.Api.Security;

/// <summary>
/// Refuses requests that did not come through Marquee's own CloudFront distribution.
///
/// The EC2 security group admits CloudFront's origin-facing prefix list, but that list covers every
/// distribution in every account: anyone can point their own at the host's public DNS name. So the
/// distribution adds a secret header to each origin request (MarqueeStack), and this checks it.
/// It is also what keeps <see cref="ForwardedHeadersRegistration"/>'s trust in X-Forwarded-For honest.
/// </summary>
public static class OriginVerification
{
    public const string SecretKey = "OriginVerify:Secret";
    public const string HeaderName = "X-Origin-Verify";

    /// <summary>
    /// Off unless a secret is configured, so local development is unaffected. Must run before anything
    /// that trusts the request, including the forwarded-headers middleware.
    /// </summary>
    public static IApplicationBuilder UseMarqueeOriginVerification(this IApplicationBuilder app, IConfiguration configuration)
    {
        var secret = configuration[SecretKey];
        if (string.IsNullOrEmpty(secret))
            return app;

        var expected = Encoding.UTF8.GetBytes(secret);
        return app.Use(async (context, next) =>
        {
            // The container's own healthcheck calls localhost directly, never through CloudFront.
            if (context.Request.Path.StartsWithSegments("/health")
                || CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(context.Request.Headers[HeaderName].ToString()), expected))
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        });
    }
}
