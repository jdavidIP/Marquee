using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;

namespace Marquee.IntegrationTests;

/// <summary>
/// Issue #109 / DEPLOYMENT.md § Phase 2 decision 9: the API accepts a Cognito access token from our
/// app client and nothing else the pool issues. Tokens come from a real cognito-local, so signature,
/// issuer and claim shapes are the pool's, not a hand-built imitation.
///
/// Named to sort after FixtureSanityTests: signing in with Cognito creates a user row (decision 2).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class TokenAuthenticationTests(MarqueeAppFactory factory)
{
    private const string Password = "token-tests-password-1";
    private const string AttackerKey = "a-key-the-pool-has-never-heard-of-32-chars";

    private static string NewUsername() => $"cg_{Guid.NewGuid():n}"[..20];

    private async Task<HttpStatusCode> GetFriendsAsync(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.GetAsync("/api/friends")).StatusCode;
    }

    [Fact]
    public async Task A_cognito_access_token_authenticates_with_its_sub_as_the_user_id()
    {
        var tokens = await factory.Cognito.CreateUserAsync(NewUsername(), Password);

        // 200 rather than 401 also proves the user id resolved: the endpoint refuses a principal
        // with no id, and a Cognito token carries it only as sub.
        (await GetFriendsAsync(tokens.AccessToken)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_id_token_is_refused()
    {
        var tokens = await factory.Cognito.CreateUserAsync(NewUsername(), Password);

        (await GetFriendsAsync(tokens.IdToken)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_access_token_issued_to_another_client_is_refused()
    {
        var otherClient = await factory.Cognito.CreateClientAsync($"other-{Guid.NewGuid():n}");
        var username = NewUsername();
        await factory.Cognito.CreateUserAsync(username, Password);
        var tokens = await factory.Cognito.SignInAsync(username, Password, otherClient);

        (await GetFriendsAsync(tokens.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_claiming_the_pools_issuer_but_signed_with_another_key_is_refused()
    {
        // A claimed issuer proves nothing: the signature has to verify against the pool's keys.
        var forged = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: factory.Cognito.Issuer,
            claims:
            [
                new Claim("sub", Guid.NewGuid().ToString()),
                new Claim("token_use", "access"),
                new Claim("client_id", CognitoLocal.ClientId),
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AttackerKey)),
                SecurityAlgorithms.HmacSha256)));

        (await GetFriendsAsync(forged)).Should().Be(HttpStatusCode.Unauthorized);
    }
}
