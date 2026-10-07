using System.Security.Claims;
using Amazon.CognitoIdentityProvider;
using Amazon.Runtime;
using Marquee.Api.Realtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Marquee.Api.Auth;

/// <summary>
/// Bearer authentication (DEPLOYMENT.md § Phase 2): the API accepts one thing, a Cognito access token
/// from our app client (decision 9) — the real pool in production, cognito-local everywhere else. The
/// API mints no tokens of its own.
/// </summary>
public static class AuthenticationRegistration
{
    public const string CognitoScheme = "Cognito";

    public static IServiceCollection AddMarqueeAuthentication(
        this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var cognito = configuration.GetSection(CognitoOptions.SectionName).Get<CognitoOptions>() ?? new CognitoOptions();

        services
            .AddAuthentication(CognitoScheme)
            .AddJwtBearer(CognitoScheme, options =>
            {
                // Signing keys straight from {issuer}/.well-known/jwks.json, where Cognito documents
                // them, rather than via the discovery document: cognito-local's discovery hard-codes
                // localhost:9229 whatever its issuer. The manager still caches the keys and refetches
                // when a token names one it has not seen. Plain HTTP is for cognito-local only.
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    $"{cognito.Issuer}/.well-known/jwks.json",
                    new JwksRetriever(),
                    new HttpDocumentRetriever { RequireHttps = environment.IsProduction() });
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

        // The pool's admin API, for the startup seeder (#110). Production signs with the EC2 instance
        // role, from the SDK's default credential chain; cognito-local checks no signature, so anywhere
        // else gets placeholder credentials rather than whatever happens to be on the developer's machine.
        services.AddSingleton<IAmazonCognitoIdentityProvider>(_ =>
        {
            var clientConfig = new AmazonCognitoIdentityProviderConfig
            {
                ServiceURL = cognito.ServiceUrl,
                // The region is the pool id's prefix: ca-central-1_xxxx (cognito-local: local_xxxx).
                AuthenticationRegion = cognito.UserPoolId.Split('_')[0],
            };
            return environment.IsProduction()
                ? new AmazonCognitoIdentityProviderClient(clientConfig)
                : new AmazonCognitoIdentityProviderClient(new BasicAWSCredentials("local", "local"), clientConfig);
        });
        services.AddScoped<AdminSeeder>();

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

    /// <summary>Reads a bare JWKS as the configuration JwtBearer validates signatures against.</summary>
    private sealed class JwksRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
    {
        public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
            string address, IDocumentRetriever retriever, CancellationToken cancel)
        {
            var keys = new JsonWebKeySet(await retriever.GetDocumentAsync(address, cancel));
            var configuration = new OpenIdConnectConfiguration { JsonWebKeySet = keys };
            foreach (var key in keys.GetSigningKeys())
                configuration.SigningKeys.Add(key);
            return configuration;
        }
    }
}
