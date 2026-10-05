using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Marquee.IntegrationTests;

/// <summary>
/// The same cognito-local image and seed docker-compose runs (DEPLOYMENT.md §2b), as a throwaway
/// container: real Cognito-shaped access tokens, a real JWKS and a real GetUser, so the API's Cognito
/// path is tested the way it runs rather than against hand-minted tokens.
///
/// The emulator stamps <c>iss</c> from its configured issuer domain, so the host port is chosen before
/// start and the issuer written to match it — the API then validates these tokens exactly as it does
/// locally, with no test-only configuration of its own. (Its discovery document hard-codes
/// localhost:9229 whatever the issuer, which is one reason the API reads keys from
/// <c>{issuer}/.well-known/jwks.json</c> rather than from discovery.)
/// </summary>
public sealed class CognitoLocal : IAsyncDisposable
{
    public const string PoolId = "local_marquee";
    public const string ClientId = "marquee-local-web";

    /// <summary>Every confirmation and reset code, as in docker-compose.</summary>
    public const string Code = "123456";

    private const string Image =
        "jagregory/cognito-local@sha256:a5ad30d01da5016a38535a717f6e1642d1b37f886a7b17e90b67f6e5ad134831";

    private static readonly HttpClient Http = new();
    private readonly int _port = FreePort();
    private readonly IContainer _container;

    public CognitoLocal()
    {
        var seed = Path.Combine(RepoRoot(), "docker", "cognito-local", "db");
        var config = JsonSerializer.SerializeToUtf8Bytes(new
        {
            UserPoolDefaults = new { UsernameAttributes = Array.Empty<string>() },
            TokenConfig = new { IssuerDomain = Endpoint },
        });

        _container = new ContainerBuilder(Image)
            .WithPortBinding(_port, 9229)
            .WithEnvironment("CODE", Code)
            .WithResourceMapping(config, "/app/.cognito/config.json")
            .WithResourceMapping(new FileInfo(Path.Combine(seed, "clients.json")), "/app/.cognito/db/")
            .WithResourceMapping(new FileInfo(Path.Combine(seed, $"{PoolId}.json")), "/app/.cognito/db/")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9229).ForPath("/health")))
            .Build();
    }

    public string Endpoint => $"http://localhost:{_port}";
    public string Issuer => $"{Endpoint}/{PoolId}";

    public Task StartAsync() => _container.StartAsync();
    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Signs up and confirms a user, then signs in through <paramref name="clientId"/>.</summary>
    public async Task<Tokens> CreateUserAsync(string username, string password, string? clientId = null)
    {
        await CallAsync("SignUp", new
        {
            ClientId,
            Username = username,
            Password = password,
            UserAttributes = new[] { new { Name = "email", Value = $"{username}@example.test" } },
        });
        await CallAsync("ConfirmSignUp", new { ClientId, Username = username, ConfirmationCode = Code });
        return await SignInAsync(username, password, clientId ?? ClientId);
    }

    public async Task<Tokens> SignInAsync(string username, string password, string clientId = ClientId)
    {
        var result = await CallAsync("InitiateAuth", new
        {
            ClientId = clientId,
            AuthFlow = "USER_PASSWORD_AUTH",
            AuthParameters = new { USERNAME = username, PASSWORD = password },
        });
        var tokens = result["AuthenticationResult"]!;
        return new Tokens(tokens["AccessToken"]!.GetValue<string>(), tokens["IdToken"]!.GetValue<string>());
    }

    /// <summary>A second app client in the same pool, for proving a token issued to it is refused.</summary>
    public async Task<string> CreateClientAsync(string name)
    {
        var result = await CallAsync("CreateUserPoolClient", new
        {
            UserPoolId = PoolId,
            ClientName = name,
            ExplicitAuthFlows = new[] { "ALLOW_USER_PASSWORD_AUTH", "ALLOW_REFRESH_TOKEN_AUTH" },
        });
        return result["UserPoolClient"]!["ClientId"]!.GetValue<string>();
    }

    private async Task<JsonNode> CallAsync(string operation, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.1");
        request.Headers.Add("X-Amz-Target", $"AWSCognitoIdentityProviderService.{operation}");

        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"cognito-local {operation} failed: {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text.Length == 0 ? "{}" : text)!;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Marquee.sln")))
                return dir.FullName;
        throw new InvalidOperationException("Could not find the repository root (Marquee.sln) above the test output.");
    }

    public sealed record Tokens(string AccessToken, string IdToken);
}
