using System.Security.Claims;
using System.Text;
using Marquee.Api.Realtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Marquee.Api.Auth;

/// <summary>
/// Bearer authentication during the move to Cognito (DEPLOYMENT.md § Phase 2).
///
/// Production accepts one thing: a Cognito access token from our app client (decision 9). Outside
/// Production the API's own HMAC tokens are accepted too — the "test issuer" the integration tests
/// mint, and what the local frontend signs in with until it moves to Cognito (#111). A request is
/// routed to one scheme by its token's unvalidated <c>iss</c>; routing grants nothing, because the
/// chosen scheme then validates signature, issuer and lifetime as usual.
/// </summary>
public static class AuthenticationRegistration
{
    public const string CognitoScheme = "Cognito";
    public const string LegacyScheme = "Legacy";
    private const string SelectorScheme = "Bearer";

    public static IServiceCollection AddMarqueeAuthentication(
        this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var cognito = configuration.GetSection(CognitoOptions.SectionName).Get<CognitoOptions>() ?? new CognitoOptions();
        var acceptLegacy = !environment.IsProduction();

        var auth = services
            .AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, SelectorScheme, options =>
                options.ForwardDefaultSelector = context =>
                    acceptLegacy && IssuerOf(ReadToken(context)) != cognito.Issuer ? LegacyScheme : CognitoScheme)
            .AddJwtBearer(CognitoScheme, options =>
            {
                options.Authority = cognito.Issuer;
                // cognito-local serves its discovery document over plain HTTP.
                options.RequireHttpsMetadata = environment.IsProduction();
                // Kept for the first-request user-row creation, which calls GetUser with this token.
                options.SaveToken = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = cognito.Issuer,
                    // Access tokens have no aud; client_id stands in for it below.
                    ValidateAudience = false,
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = AcceptHubQueryToken,
                    OnTokenValidated = context =>
                    {
                        // token_use refuses an ID token presented in place of an access token; client_id
                        // refuses a token the pool issued to any other client.
                        var principal = context.Principal!;
                        if (principal.FindFirstValue("token_use") != "access")
                            context.Fail("Not an access token.");
                        else if (principal.FindFirstValue("client_id") != cognito.ClientId)
                            context.Fail("Token was issued to another client.");
                        return Task.CompletedTask;
                    },
                };
            });

        if (acceptLegacy)
        {
            var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
            auth.AddJwtBearer(LegacyScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                };
                options.Events = new JwtBearerEvents { OnMessageReceived = AcceptHubQueryToken };
            });
        }

        return services;
    }

    // WebSocket and server-sent-event connections cannot carry an Authorization header, so the SignalR
    // client passes the token as a query string parameter on the hub URL. Accept it only for hub paths —
    // everywhere else the header remains the only way in.
    private static Task AcceptHubQueryToken(MessageReceivedContext context)
    {
        if (HubQueryToken(context.HttpContext) is { } token)
            context.Token = token;
        return Task.CompletedTask;
    }

    private static string? HubQueryToken(HttpContext context)
    {
        var token = context.Request.Query["access_token"].ToString();
        return token.Length > 0 && context.Request.Path.StartsWithSegments(HubRoutes.Premieres) ? token : null;
    }

    private static string? ReadToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : HubQueryToken(context);
    }

    private static string? IssuerOf(string? token)
    {
        if (token is null)
            return null;
        try
        {
            return new JsonWebToken(token).Issuer;
        }
        catch (ArgumentException)
        {
            // Not a JWT at all; whichever scheme it lands on will reject it.
            return null;
        }
    }
}
