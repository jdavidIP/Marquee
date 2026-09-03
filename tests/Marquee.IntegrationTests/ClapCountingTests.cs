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
/// The clap hot path, against a real Redis and a real Postgres.
///
/// These are the assertions Iteration 2 was built to make true, re-checked here through the public
/// HTTP endpoint rather than against the counter directly — because what matters is that a caller
/// hammering the API cannot produce a lost update, a cap overrun, or two opens, not that a Lua
/// script works in isolation.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ClapCountingTests(MarqueeAppFactory factory)
{
    private sealed record ClapBody(
        Guid PremiereId, string Status, int TotalClaps, int Threshold,
        int MyClaps, int MyCap, bool CapReached, bool Opened);

    private sealed record PremiereBody(Guid Id, string Status, int Threshold, int TotalClaps);

    private async Task<HttpClient> AuthedClientAsync()
    {
        var token = await factory.AdminTokenAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<PremiereBody> CreatePremiereAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/premieres", new { durationMinutes = 60 });
        if (!response.IsSuccessStatusCode)
        {
            // A bare status code here is a poor bug report: 401 and 403 mean very different things
            // about what is broken, and the body usually says which.
            var body = await response.Content.ReadAsStringAsync();
            var challenge = response.Headers.WwwAuthenticate.ToString();
            throw new InvalidOperationException(
                $"Creating a Premiere failed with {(int)response.StatusCode} {response.StatusCode}. " +
                $"WWW-Authenticate: '{challenge}'. Body: {body}");
        }

        return (await response.Content.ReadFromJsonAsync<PremiereBody>())!;
    }

    [Fact]
    public async Task Concurrent_claps_are_counted_exactly_and_open_the_Premiere_once()
    {
        factory.Tmdb.IsDown = false;
        var client = await AuthedClientAsync();
        var premiere = await CreatePremiereAsync(client);

        // With a near-empty user table the participant cap works out to the whole threshold, so one
        // account can drive a Premiere to its open. Overshooting deliberately: the surplus claps are
        // what prove the cap holds under contention rather than merely being checked.
        var attempts = premiere.Threshold + 25;

        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async _ =>
        {
            var response = await client.PostAsync($"/api/premieres/{premiere.Id}/clap", null);
            return (response.StatusCode, Body: await ReadClapAsync(response));
        }));

        var accepted = responses.Where(r => r.StatusCode == HttpStatusCode.OK && r.Body is not null).ToList();
        var counted = accepted.Where(r => !r.Body!.CapReached || r.Body.MyClaps <= premiere.Threshold).ToList();

        // Exactly one caller may see opened=true. This is the exactly-once open: the increment is
        // atomic, so only the clap whose INCR landed on the threshold triggers it.
        accepted.Count(r => r.Body!.Opened).Should().Be(1, "only one clap can cross the threshold");

        // The durable record must agree with what the counter handed out. A lost update would show
        // up here as TotalClaps below the threshold despite the Premiere having opened.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var stored = await db.Premieres.AsNoTracking().FirstAsync(p => p.Id == premiere.Id);

        stored.Status.Should().Be(PremiereStatus.Opened);
        stored.TotalClaps.Should().Be(premiere.Threshold,
            "the cap stops the surplus claps, so the final count is exactly the threshold");
        counted.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Repeating_an_Idempotency_Key_counts_one_clap()
    {
        factory.Tmdb.IsDown = false;
        var client = await AuthedClientAsync();
        var premiere = await CreatePremiereAsync(client);

        var key = Guid.NewGuid().ToString();

        var first = await ClapWithKeyAsync(client, premiere.Id, key);
        var second = await ClapWithKeyAsync(client, premiere.Id, key);

        first.Should().NotBeNull();
        second.Should().NotBeNull();

        // The retry replays the stored response rather than counting again, so both report the same
        // per-participant total. Anything else means a network retry inflates a user's contribution.
        second!.MyClaps.Should().Be(first!.MyClaps);
        second.TotalClaps.Should().Be(first.TotalClaps);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var stored = await db.Premieres.AsNoTracking().FirstAsync(p => p.Id == premiere.Id);
        stored.Status.Should().Be(PremiereStatus.Active, "one clap is nowhere near the threshold");
    }

    private static async Task<ClapBody?> ClapWithKeyAsync(HttpClient client, Guid premiereId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/premieres/{premiereId}/clap");
        request.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await ReadClapAsync(response);
    }

    private static async Task<ClapBody?> ReadClapAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            return null;
        try
        {
            return await response.Content.ReadFromJsonAsync<ClapBody>();
        }
        catch
        {
            return null;
        }
    }

    private sealed record PremiereDetailBody(Guid Id, string Status, int? MyEmblemTier);

    /// <summary>
    /// MyEmblemTier (added for the Premiere screen's reveal banner) is null while a Premiere is
    /// Active — nothing has been assigned yet, §4.3 — and reads back once the Worker assigns it.
    /// Integration tests point MassTransit at a closed broker port on purpose (see
    /// MarqueeAppFactory's class comment), so the Worker's PremiereOpenedConsumer never actually
    /// runs here; seeding the assignment directly is how every other emblem-tier test in this suite
    /// (LibraryQueryTests) verifies the read side without depending on that consumer.
    /// </summary>
    [Fact]
    public async Task My_emblem_tier_is_null_while_active_and_reads_back_once_assigned()
    {
        factory.Tmdb.IsDown = false;
        var client = await AuthedClientAsync();
        var premiere = await CreatePremiereAsync(client);

        var clapResponse = await client.PostAsync($"/api/premieres/{premiere.Id}/clap", null);
        clapResponse.EnsureSuccessStatusCode();

        var whileActive = await client.GetFromJsonAsync<PremiereDetailBody>($"/api/premieres/{premiere.Id}");
        whileActive!.MyEmblemTier.Should().BeNull("nothing is assigned until the Premiere opens");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var admin = await db.Users.AsNoTracking().FirstAsync(u => u.Username == MarqueeAppFactory.AdminUsername);

        var stored = await db.Premieres.FirstAsync(p => p.Id == premiere.Id);
        stored.Status = PremiereStatus.Opened;
        stored.OpenedAt = DateTime.UtcNow;

        // The Worker's PremiereOpenedConsumer is what would normally write this row (it never runs
        // in these tests — see the class comment above), so seed the outcome it would have produced.
        db.Contributions.Add(new Contribution
        {
            PremiereId = premiere.Id, UserId = admin.Id, ClapCount = 1, EmblemTier = 3
        });
        await db.SaveChangesAsync();

        var afterOpen = await client.GetFromJsonAsync<PremiereDetailBody>($"/api/premieres/{premiere.Id}");
        afterOpen!.MyEmblemTier.Should().Be(3);

        // An anonymous caller never earns a tier at all (§4.3) — not merely "not yet assigned".
        var anonymous = factory.CreateClient();
        var anonSession = await anonymous.PostAsJsonAsync("/api/sessions/anonymous", new { });
        anonSession.EnsureSuccessStatusCode();
        var session = await anonSession.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        anonymous.DefaultRequestHeaders.Add("X-Anon-Session", session!["token"]);

        var anonView = await anonymous.GetFromJsonAsync<PremiereDetailBody>($"/api/premieres/{premiere.Id}");
        anonView!.MyEmblemTier.Should().BeNull();
    }
}
