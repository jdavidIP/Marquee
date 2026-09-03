using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    private sealed record LimitedBody(string Username, string? Bio, string? FriendshipStatus, bool? FriendRequestOutgoing, int? SharedPremieresAttended);
    private sealed record FullBody(
        Guid Id, string Username, string? Bio, bool IsPrivate, DateTime CreatedAt,
        int MoviesCollected, int PremieresAttended, int FriendCount,
        string? FriendshipStatus, bool? FriendRequestOutgoing, int? SharedPremieresAttended);
    private sealed record AuthResponse(string Token, UserBody User);
    private sealed record UserBody(Guid Id);

    private async Task<(HttpClient Client, string Username, Guid UserId)> NewUserAsync(string tag)
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

        return (client, username, body.User.Id);
    }

    private static Task<HttpResponseMessage> MakePrivateAsync(HttpClient client) =>
        client.PatchAsJsonAsync("/api/users/me", new { isPrivate = true });

    /// <summary>Plants a Premiere both users clapped for — the fixture for SharedPremieresAttended.</summary>
    private async Task SeedSharedPremiereAsync(Guid userA, Guid userB)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var now = DateTime.UtcNow;

        var movie = new Movie
        {
            TmdbId = Random.Shared.Next(1_000_000, 9_999_999),
            Title = "Shared Premiere Movie",
            PosterPath = "/poster.jpg",
            ReleaseYear = 2001,
            Overview = "Seeded by ProfileVisibilityTests.",
            VoteAverage = 7.0,
            VoteCount = 1000,
            CachedAt = now
        };
        db.Movies.Add(movie);

        var premiere = new Premiere
        {
            ScopeId = "global",
            ScheduledFor = now.AddDays(-1),
            OpensAt = now.AddDays(-1),
            ExpiresAt = now.AddDays(-1).AddHours(1),
            OpenedAt = now.AddDays(-1),
            Threshold = 10,
            RegisteredClapCap = 5,
            AnonymousClapCap = 2,
            Status = PremiereStatus.Opened,
            Movie = movie,
            TotalClaps = 10
        };
        db.Premieres.Add(premiere);

        db.Contributions.Add(new Contribution { Premiere = premiere, UserId = userA, ClapCount = 3 });
        db.Contributions.Add(new Contribution { Premiere = premiere, UserId = userB, ClapCount = 4 });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_stranger_sees_only_username_and_bio_on_a_private_profile()
    {
        var (owner, ownerName, _) = await NewUserAsync("owner");
        await owner.PatchAsJsonAsync("/api/users/me", new { bio = "hello", isPrivate = true });

        var (stranger, _, _) = await NewUserAsync("stranger");
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
        var (owner, ownerName, _) = await NewUserAsync("owner");
        await MakePrivateAsync(owner);

        var (requester, requesterName, _) = await NewUserAsync("asker");
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
        var (owner, ownerName, _) = await NewUserAsync("owner");
        await MakePrivateAsync(owner);

        var (friend, friendName, _) = await NewUserAsync("friend");
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
        var (owner, ownerName, _) = await NewUserAsync("findme");
        await MakePrivateAsync(owner);

        var (stranger, _, _) = await NewUserAsync("searcher");
        var response = await stranger.GetAsync($"/api/users?query={ownerName[..8]}");
        var hits = await response.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();

        hits.Should().Contain(h => h["username"].ToString() == ownerName,
            "hiding a private account from search would make it unfindable, not merely private");
    }

    [Fact]
    public async Task Shared_premieres_count_only_the_two_users_overlap()
    {
        var (viewer, _, viewerId) = await NewUserAsync("viewer");
        var (target, targetName, targetId) = await NewUserAsync("target");
        var (bystander, bystanderName, bystanderId) = await NewUserAsync("bystander");

        // viewer & target clapped the same Premiere; bystander clapped a separate one with target,
        // which must not leak into the viewer's count.
        await SeedSharedPremiereAsync(viewerId, targetId);
        await SeedSharedPremiereAsync(bystanderId, targetId);

        var response = await viewer.GetAsync($"/api/users/{targetName}");
        var body = await response.Content.ReadFromJsonAsync<FullBody>();
        body!.SharedPremieresAttended.Should().Be(1);

        // Symmetric: bystander's own overlap with target is its own separate Premiere.
        var bystanderView = await bystander.GetAsync($"/api/users/{targetName}");
        (await bystanderView.Content.ReadFromJsonAsync<FullBody>())!.SharedPremieresAttended.Should().Be(1);

        // A stranger who never clapped anything with target sees zero, not null — there is a
        // viewer to share with, it's just an empty overlap.
        var (stranger, _, _) = await NewUserAsync("nooverlap");
        var strangerView = await stranger.GetAsync($"/api/users/{targetName}");
        (await strangerView.Content.ReadFromJsonAsync<FullBody>())!.SharedPremieresAttended.Should().Be(0);

        // Viewing your own profile has no "other" account to share with.
        var selfView = await target.GetAsync($"/api/users/{targetName}");
        (await selfView.Content.ReadFromJsonAsync<FullBody>())!.SharedPremieresAttended.Should().BeNull();

        // Anonymous has no viewer identity to intersect against either.
        var anon = factory.CreateClient();
        var anonView = await anon.GetAsync($"/api/users/{targetName}");
        (await anonView.Content.ReadFromJsonAsync<FullBody>())!.SharedPremieresAttended.Should().BeNull();

        _ = bystanderName;
    }
}
