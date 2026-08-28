using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// GET /api/users/{username} shapes its payload by who is asking (MARQUEE_PLAN.md, Iteration 5).
/// This had no coverage at all until the DTO grew a viewer-specific field: FriendshipStatus and
/// FriendRequestOutgoing were added to the limited payload so a stranger's Add Friend button can
/// know whether a request already exists, rather than discovering it by rejection. Getting that
/// wrong the other way — leaking the account's own detail through the "viewer-specific" door — is
/// exactly the mistake worth a real HTTP-level test to rule out.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ProfileVisibilityTests(MarqueeAppFactory factory)
{
    private sealed record LimitedBody(string Username, string? Bio, string? FriendshipStatus, bool? FriendRequestOutgoing);
    private sealed record FullBody(
        Guid Id, string Username, string? Bio, bool IsPrivate, DateTime CreatedAt,
        int MoviesCollected, int PremieresAttended, int FriendCount,
        string? FriendshipStatus, bool? FriendRequestOutgoing);
    private sealed record AuthResponse(string Token, object User);

    private async Task<(HttpClient Client, string Username)> NewUserAsync(string tag)
    {
        var client = factory.CreateClient();
        var username = $"u_{tag}_{Guid.NewGuid():n}"[..24];

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

        // Some of these tests send friend requests, which issue #29 now refuses between unconfirmed
        // accounts.
        await TestAuth.ConfirmAsync(factory, client, username, TestPasswords.Valid);

        return (client, username);
    }

    private static Task<HttpResponseMessage> MakePrivateAsync(HttpClient client) =>
        client.PatchAsJsonAsync("/api/users/me", new { isPrivate = true });

    [Fact]
    public async Task A_stranger_sees_only_username_and_bio_on_a_private_profile()
    {
        var (owner, ownerName) = await NewUserAsync("owner");
        await owner.PatchAsJsonAsync("/api/users/me", new { bio = "hello", isPrivate = true });

        var (stranger, _) = await NewUserAsync("stranger");
        var response = await stranger.GetAsync($"/api/users/{ownerName}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "privacy restricts detail, not existence");

        var raw = await response.Content.ReadAsStringAsync();
        // The plan requires the account's own fields to be absent, not merely null — asserting on
        // the raw JSON is the only way to prove that, since a strongly-typed deserialise would
        // silently accept a field that should not be there at all.
        raw.Should().NotContain("moviesCollected").And.NotContain("isPrivate").And.NotContain("createdAt");

        var body = await response.Content.ReadFromJsonAsync<LimitedBody>();
        body!.Username.Should().Be(ownerName);
        body.Bio.Should().Be("hello");
    }

    [Fact]
    public async Task A_pending_request_is_visible_on_an_otherwise_limited_profile()
    {
        // This is the whole point of the change: without it, the frontend cannot offer a working
        // Add Friend button on a private stranger's profile, because it has no way to know a
        // request is already pending.
        var (owner, ownerName) = await NewUserAsync("owner");
        await MakePrivateAsync(owner);

        var (requester, requesterName) = await NewUserAsync("asker");
        var sent = await requester.PostAsJsonAsync("/api/friends/requests", new { username = ownerName });
        sent.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await requester.GetAsync($"/api/users/{ownerName}");
        var body = await response.Content.ReadFromJsonAsync<LimitedBody>();

        body!.FriendshipStatus.Should().Be("Pending");
        body.FriendRequestOutgoing.Should().Be(true, "the requester sent it, so it is outgoing from their side");
    }

    [Fact]
    public async Task An_accepted_friend_sees_the_full_profile_of_a_private_account()
    {
        // The trap the frontend's own type guard exists for: isPrivate stays true, but the payload
        // is the full shape because a friend is entitled to it. Branching on isPrivate instead of
        // payload shape would get this backwards.
        var (owner, ownerName) = await NewUserAsync("owner");
        await MakePrivateAsync(owner);

        var (friend, friendName) = await NewUserAsync("friend");
        var sent = await friend.PostAsJsonAsync("/api/friends/requests", new { username = ownerName });
        var request = await sent.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var requestId = ((System.Text.Json.JsonElement)request!["id"]).GetGuid();

        var accepted = await owner.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", new { });
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await friend.GetAsync($"/api/users/{ownerName}");
        var body = await response.Content.ReadFromJsonAsync<FullBody>();

        body!.IsPrivate.Should().BeTrue("privacy applies to strangers, not to friends");
        body.FriendshipStatus.Should().Be("Accepted");
        _ = friendName; // kept for readability at the call sites above; not asserted directly
    }

    [Fact]
    public async Task A_stranger_stays_findable_in_search_despite_being_private()
    {
        var (owner, ownerName) = await NewUserAsync("findme");
        await MakePrivateAsync(owner);

        var (stranger, _) = await NewUserAsync("searcher");
        var response = await stranger.GetAsync($"/api/users?query={ownerName[..8]}");
        var hits = await response.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();

        hits.Should().Contain(h => h["username"].ToString() == ownerName,
            "hiding a private account from search would make it unfindable, not merely private");
    }
}
