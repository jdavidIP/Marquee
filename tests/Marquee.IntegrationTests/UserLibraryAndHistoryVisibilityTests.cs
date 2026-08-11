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
/// GET /api/users/{username}/library and GET /api/users/{username}/premieres (issue #38) — the
/// library and premiere-history counterparts to UserFriendsVisibilityTests, gated by the same
/// UserProfileService.ResolveEntitlementAsync rule and reusing LibraryController's own querying
/// (issue #26) rather than a second implementation.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class UserLibraryAndHistoryVisibilityTests(MarqueeAppFactory factory)
{
    private sealed record MovieBody(int TmdbId, string Title, string? PosterUrl, int? ReleaseYear);
    private sealed record LibraryEntryBody(Guid MovieId, MovieBody Movie, Guid PremiereId, DateTime AcquiredAt, int? EmblemTier);
    private sealed record LibraryPageBody(List<LibraryEntryBody> Items, int Total, int Page, int PageSize);
    private sealed record HistoryEntryBody(Guid PremiereId, MovieBody Movie, DateTime? OpenedAt, string Status, int ClapCount, int? EmblemTier);
    private sealed record HistoryPageBody(List<HistoryEntryBody> Items, int Total, int Page, int PageSize);
    private sealed record AuthBody(string Token, UserBody User);
    private sealed record UserBody(Guid Id);

    private async Task<(HttpClient Client, string Username, Guid UserId)> NewUserAsync(string tag)
    {
        var client = factory.CreateClient();
        var username = $"ulh_{tag}_{Guid.NewGuid():n}"[..24];

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new
            {
                username,
                email = $"{username}@marquee.test",
                password = TestPasswords.Valid,
                confirmPassword = TestPasswords.Valid,
            });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<AuthBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return (client, username, body.User.Id);
    }

    private static Task<HttpResponseMessage> MakePrivateAsync(HttpClient client) =>
        client.PatchAsJsonAsync("/api/users/me", new { isPrivate = true });

    private static async Task BefriendAsync(HttpClient a, string bUsername, HttpClient b)
    {
        var sent = await a.PostAsJsonAsync("/api/friends/requests", new { username = bUsername });
        var request = await sent.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var requestId = ((System.Text.Json.JsonElement)request!["id"]).GetGuid();

        var accepted = await b.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", new { });
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
    }

    /// <summary>Plants one collected movie and, separately, one contributed-to Premiere for a user.</summary>
    private async Task SeedAsync(Guid userId, string title, int emblemTier)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var now = DateTime.UtcNow;

        var movie = new Movie
        {
            TmdbId = Random.Shared.Next(1_000_000, 9_999_999),
            Title = title,
            PosterPath = "/poster.jpg",
            ReleaseYear = 2001,
            Overview = "Seeded by UserLibraryAndHistoryVisibilityTests.",
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

        db.LibraryEntries.Add(new LibraryEntry
        {
            UserId = userId, Movie = movie, Premiere = premiere, AcquiredAt = premiere.OpenedAt!.Value
        });
        db.Contributions.Add(new Contribution
        {
            Premiere = premiere, UserId = userId, ClapCount = 3, EmblemTier = emblemTier
        });

        await db.SaveChangesAsync();
    }

    // --- Library ---

    [Fact]
    public async Task A_strangers_library_is_open_when_the_account_is_public()
    {
        var (owner, ownerName, ownerId) = await NewUserAsync("owner");
        await SeedAsync(ownerId, "Public Owner's Film", emblemTier: 3);

        var (stranger, _, _) = await NewUserAsync("stranger");
        var response = await stranger.GetAsync($"/api/users/{ownerName}/library");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<LibraryPageBody>();
        page!.Items.Should().ContainSingle(i => i.Movie.Title == "Public Owner's Film");
    }

    [Fact]
    public async Task A_strangers_library_is_forbidden_when_the_account_is_private()
    {
        var (owner, ownerName, ownerId) = await NewUserAsync("owner");
        await SeedAsync(ownerId, "Private Owner's Film", emblemTier: 3);
        await MakePrivateAsync(owner);

        var (stranger, _, _) = await NewUserAsync("stranger");
        var response = await stranger.GetAsync($"/api/users/{ownerName}/library");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_accepted_friend_sees_the_library_of_a_private_account()
    {
        var (owner, ownerName, ownerId) = await NewUserAsync("owner");
        await SeedAsync(ownerId, "Shared With Friend", emblemTier: 3);
        await MakePrivateAsync(owner);

        var (friend, friendName, _) = await NewUserAsync("friend");
        await BefriendAsync(friend, ownerName, owner);

        var response = await friend.GetAsync($"/api/users/{ownerName}/library");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = friendName;
    }

    [Fact]
    public async Task The_library_endpoint_reuses_search_and_sort_from_issue_26()
    {
        var (owner, ownerName, ownerId) = await NewUserAsync("owner");
        await SeedAsync(ownerId, "Alpha Feature", emblemTier: 2);
        await SeedAsync(ownerId, "Beta Feature", emblemTier: 5);

        var (viewer, _, _) = await NewUserAsync("viewer");
        var response = await viewer.GetAsync($"/api/users/{ownerName}/library?search=Alpha");

        var page = await response.Content.ReadFromJsonAsync<LibraryPageBody>();
        page!.Items.Should().ContainSingle();
        page.Items[0].Movie.Title.Should().Be("Alpha Feature");
    }

    [Fact]
    public async Task The_library_filters_endpoint_describes_someone_elses_library()
    {
        var (owner, ownerName, ownerId) = await NewUserAsync("owner");
        await SeedAsync(ownerId, "Filterable Film", emblemTier: 3);

        var (viewer, _, _) = await NewUserAsync("viewer");
        var response = await viewer.GetAsync($"/api/users/{ownerName}/library/filters");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- Premiere history ---

    [Fact]
    public async Task A_premiere_history_shows_the_movie_status_claps_and_emblem()
    {
        var (owner, ownerName, ownerId) = await NewUserAsync("owner");
        await SeedAsync(ownerId, "Attended Film", emblemTier: 4);

        var response = await owner.GetAsync($"/api/users/{ownerName}/premieres");

        var page = await response.Content.ReadFromJsonAsync<HistoryPageBody>();
        var entry = page!.Items.Should().ContainSingle().Subject;
        entry.Movie.Title.Should().Be("Attended Film");
        entry.Status.Should().Be("Opened");
        entry.ClapCount.Should().Be(3);
        entry.EmblemTier.Should().Be(4);
    }

    [Fact]
    public async Task A_movie_premiered_and_attended_twice_appears_twice_in_history()
    {
        // The distinction this endpoint exists to preserve (issue #37): the library collapses a
        // repeat reveal into one entry, but a history is a record of events, not of distinct films.
        var (owner, ownerName, ownerId) = await NewUserAsync("owner");
        await SeedAsync(ownerId, "Re-premiered Film", emblemTier: 2);
        await SeedAsync(ownerId, "Re-premiered Film", emblemTier: 4);

        var response = await owner.GetAsync($"/api/users/{ownerName}/premieres");

        var page = await response.Content.ReadFromJsonAsync<HistoryPageBody>();
        page!.Items.Should().HaveCount(2);
        page.Items.Select(i => i.EmblemTier).Should().BeEquivalentTo([2, 4]);
    }

    [Fact]
    public async Task Premiere_history_search_matches_the_movie_title()
    {
        var (owner, ownerName, ownerId) = await NewUserAsync("owner");
        await SeedAsync(ownerId, "Searchable Attendance", emblemTier: 3);
        await SeedAsync(ownerId, "Something Else Entirely", emblemTier: 3);

        var response = await owner.GetAsync($"/api/users/{ownerName}/premieres?search=searchable");

        var page = await response.Content.ReadFromJsonAsync<HistoryPageBody>();
        page!.Items.Should().ContainSingle(i => i.Movie.Title == "Searchable Attendance");
    }

    [Fact]
    public async Task A_strangers_premiere_history_is_forbidden_when_the_account_is_private()
    {
        var (owner, ownerName, ownerId) = await NewUserAsync("owner");
        await SeedAsync(ownerId, "Private History Film", emblemTier: 3);
        await MakePrivateAsync(owner);

        var (stranger, _, _) = await NewUserAsync("stranger");
        var response = await stranger.GetAsync($"/api/users/{ownerName}/premieres");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_nonexistent_username_is_not_found_on_either_endpoint()
    {
        var (client, _, _) = await NewUserAsync("caller");
        var nobody = $"nobody_{Guid.NewGuid():n}";

        (await client.GetAsync($"/api/users/{nobody}/library")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/users/{nobody}/premieres")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_callers_are_rejected_on_both_endpoints()
    {
        var (owner, ownerName, _) = await NewUserAsync("owner");
        var anonymous = factory.CreateClient();

        (await anonymous.GetAsync($"/api/users/{ownerName}/library")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/users/{ownerName}/premieres")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
