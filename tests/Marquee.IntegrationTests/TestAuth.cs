using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Marquee.IntegrationTests;

/// <summary>
/// Signs test accounts in the way the product does: through the user pool (cognito-local), then with
/// the access token it issues. There is no test-only token path in the API to reach for.
/// </summary>
public static class TestAuth
{
    private sealed record Me(Guid Id);

    /// <summary>
    /// A confirmed account with a user row, and a client that is signed in as it. The pool only issues
    /// tokens to confirmed accounts, and the row is created by the account's first request (§ Phase 2,
    /// decision 2) — made here so the caller has the id.
    /// </summary>
    public static async Task<(HttpClient Client, string Username, Guid UserId)> NewUserAsync(
        MarqueeAppFactory factory, string usernamePrefix)
    {
        var username = $"{usernamePrefix}_{Guid.NewGuid():n}"[..24];
        var tokens = await factory.Cognito.CreateUserAsync(username, TestPasswords.Valid);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetFromJsonAsync<Me>("/api/auth/me");
        return (client, username, me!.Id);
    }
}
