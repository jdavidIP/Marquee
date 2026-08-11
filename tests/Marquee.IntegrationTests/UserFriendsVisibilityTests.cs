using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;

namespace Marquee.IntegrationTests;

/// <summary>
/// GET /api/users/{username}/friends (issue #39) — viewing and searching someone else's friend
/// list, shaped by the same self/admin/friend/public entitlement rule as the profile itself
/// (UserProfileService.ResolveEntitlementAsync), rather than a second copy of that rule.
///
/// Unlike the profile endpoint, a denied viewer gets 403 rather than a restricted 200: the
/// account's existence is already public through the profile and through search, so there is
/// nothing to protect by hiding that this route resolves — only the list's content is private.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class UserFriendsVisibilityTests(MarqueeAppFactory factory)
{
    private sealed record FriendBody(Guid UserId, string Username, string? Bio, bool IsPrivate, DateTime FriendsSince);
    private sealed record AuthResponse(string Token, object User);

    private async Task<(HttpClient Client, string Username)> NewUserAsync(string tag)
    {
        var client = factory.CreateClient();
        var username = $"uf_{tag}_{Guid.NewGuid():n}"[..24];

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new
            {
                username,
                email = $"{username}@marquee.test",
                password = TestPasswords.Valid,
                confirmPassword = TestPasswords.Valid,
            });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return (client, username);
    }

    private static Task<HttpResponseMessage> MakePrivateAsync(HttpClient client) =>
        client.PatchAsJsonAsync("/api/users/me", new { isPrivate = true });

    /// <summary>Sends a request from <paramref name="a"/> to <paramref name="b"/> and has b accept it.</summary>
    private static async Task BefriendAsync(HttpClient a, string bUsername, HttpClient b)
    {
        var sent = await a.PostAsJsonAsync("/api/friends/requests", new { username = bUsername });
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());

        var request = await sent.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var requestId = ((System.Text.Json.JsonElement)request!["id"]).GetGuid();

        var accepted = await b.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", new { });
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_strangers_friend_list_is_open_when_the_account_is_public()
    {
        var (owner, ownerName) = await NewUserAsync("owner");
        var (theirFriend, friendName) = await NewUserAsync("theirfriend");
        await BefriendAsync(owner, friendName, theirFriend);

        var (stranger, _) = await NewUserAsync("stranger");
        var response = await stranger.GetAsync($"/api/users/{ownerName}/friends");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var friends = await response.Content.ReadFromJsonAsync<List<FriendBody>>();
        friends.Should().ContainSingle(f => f.Username == friendName);
    }

    [Fact]
    public async Task A_strangers_friend_list_is_forbidden_when_the_account_is_private()
    {
        var (owner, ownerName) = await NewUserAsync("owner");
        await MakePrivateAsync(owner);

        var (stranger, _) = await NewUserAsync("stranger");
        var response = await stranger.GetAsync($"/api/users/{ownerName}/friends");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the account's existence is already public through the profile and search — only the list's content is private");
    }

    [Fact]
    public async Task An_accepted_friend_sees_the_list_of_a_private_account()
    {
        var (owner, ownerName) = await NewUserAsync("owner");
        await MakePrivateAsync(owner);

        var (friend, friendName) = await NewUserAsync("friend");
        await BefriendAsync(friend, ownerName, owner);

        var response = await friend.GetAsync($"/api/users/{ownerName}/friends");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "privacy applies to strangers, not to friends — the same rule the profile itself follows");
        var friends = await response.Content.ReadFromJsonAsync<List<FriendBody>>();
        friends.Should().ContainSingle(f => f.Username == friendName, "the viewer themselves is in the list");
    }

    [Fact]
    public async Task The_owner_always_sees_their_own_private_friend_list()
    {
        var (owner, ownerName) = await NewUserAsync("owner");
        await MakePrivateAsync(owner);

        var response = await owner.GetAsync($"/api/users/{ownerName}/friends");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Search_narrows_the_list_to_a_matching_username()
    {
        var (owner, ownerName) = await NewUserAsync("owner");
        var (alice, aliceName) = await NewUserAsync("alice");
        var (bob, bobName) = await NewUserAsync("bob");
        await BefriendAsync(owner, aliceName, alice);
        await BefriendAsync(owner, bobName, bob);

        var response = await owner.GetAsync($"/api/users/{ownerName}/friends?search=alice");

        var friends = await response.Content.ReadFromJsonAsync<List<FriendBody>>();
        friends.Should().ContainSingle(f => f.Username == aliceName);
        friends.Should().NotContain(f => f.Username == bobName);
    }

    [Fact]
    public async Task Search_is_case_insensitive_and_matches_a_substring()
    {
        var (owner, ownerName) = await NewUserAsync("owner");
        var (friend, friendName) = await NewUserAsync("FindableFriend");
        await BefriendAsync(owner, friendName, friend);

        var response = await owner.GetAsync($"/api/users/{ownerName}/friends?search=INDABLEFRI");

        var friends = await response.Content.ReadFromJsonAsync<List<FriendBody>>();
        friends.Should().ContainSingle(f => f.Username == friendName);
    }

    [Fact]
    public async Task A_nonexistent_username_is_not_found_rather_than_forbidden()
    {
        var (client, _) = await NewUserAsync("caller");

        var response = await client.GetAsync($"/api/users/nobody_{Guid.NewGuid():n}/friends");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_view_anyones_friend_list()
    {
        var (owner, ownerName) = await NewUserAsync("owner");
        var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/users/{ownerName}/friends");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
