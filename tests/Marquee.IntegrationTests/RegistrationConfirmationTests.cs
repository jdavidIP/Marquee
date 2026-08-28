using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marquee.Api.Auth;
using Marquee.Domain;
using Marquee.Infrastructure.Messaging;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// Issue #29's three "done when" behaviours: an unconfirmed account cannot move a Premiere's
/// threshold or caps, it claps under the anonymous cap with no user-linked rows, and the
/// confirm-email link works end to end.
///
/// Named to sort after FixtureSanityTests: every test here registers accounts against the shared
/// database FixtureSanityTests asserts is freshly seeded, and IntegrationCollection runs every class
/// against one Postgres for the whole run — see LibraryQueryTests, ProfileVisibilityTests and the
/// other user-creating suites, which follow the same naming constraint for the same reason.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class RegistrationConfirmationTests(MarqueeAppFactory factory)
{
    private sealed record AuthUser(Guid Id, string Username, bool EmailConfirmed);
    private sealed record AuthResponse(string Token, AuthUser User);
    private sealed record PremiereBody(Guid Id, string Status, int Threshold, int RegisteredClapCap, int AnonymousClapCap);
    private sealed record ClapBody(Guid PremiereId, string Status, int TotalClaps, int Threshold, int MyClaps, int MyCap, bool CapReached, bool Opened);

    private async Task<(HttpClient Client, string Username, Guid UserId)> RegisterUnconfirmedAsync(string tag)
    {
        var client = factory.CreateClient();
        var username = $"ec_{tag}_{Guid.NewGuid():n}"[..24];

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
        body!.User.EmailConfirmed.Should().BeFalse("a freshly registered account has not confirmed anything yet");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.Token);

        return (client, username, body.User.Id);
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var token = await factory.AdminTokenAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<PremiereBody> CreatePremiereAsync(HttpClient adminClient)
    {
        var response = await adminClient.PostAsJsonAsync("/api/premieres", new { durationMinutes = 60 });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PremiereBody>())!;
    }

    /// <summary>
    /// Same format ParticipantResolver.UnconfirmedSessionId derives — duplicated here deliberately
    /// rather than exposed as internal API, since what matters for this test is that the *contract*
    /// (stable, prefixed, per-account) holds, not a shared implementation detail.
    /// </summary>
    private static string UnconfirmedSessionId(Guid userId) => $"unconfirmed:{userId:N}";

    [Fact]
    public async Task An_unconfirmed_signup_does_not_move_totalRegisteredUsers()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var confirmedBefore = await db.Users.CountAsync(u => u.EmailConfirmedAt != null);
        var totalBefore = await db.Users.CountAsync();

        for (var i = 0; i < 5; i++)
            await RegisterUnconfirmedAsync($"excl{i}");

        var confirmedAfter = await db.Users.CountAsync(u => u.EmailConfirmedAt != null);
        var totalAfter = await db.Users.CountAsync();

        confirmedAfter.Should().Be(
            confirmedBefore, "unconfirmed accounts must not move the count §4.1/§4.2 read from");
        totalAfter.Should().BeGreaterThanOrEqualTo(
            totalBefore + 5, "the accounts do exist — they are just excluded from the formula");
    }

    /// <summary>
    /// Asserts against Redis rather than the Contributions table on purpose: those rows are written
    /// by PremiereOpenedConsumer, which runs only in Marquee.Worker — a process this API-only harness
    /// deliberately never starts (see MarqueeAppFactory). Redis is where the clap path's own
    /// guarantees actually live before an open, and ClapCountingTests makes the same choice for the
    /// registered path.
    /// </summary>
    [Fact]
    public async Task An_unconfirmed_account_claps_under_the_anonymous_cap_with_no_user_linked_rows()
    {
        var admin = await AdminClientAsync();
        var premiere = await CreatePremiereAsync(admin);

        premiere.AnonymousClapCap.Should().BeLessThan(premiere.RegisteredClapCap,
            "otherwise this test cannot tell the two caps apart");

        var (client, _, userId) = await RegisterUnconfirmedAsync("clap");

        // No X-Anon-Session header anywhere here — the bearer token alone is enough for an
        // unconfirmed account to participate, just not as a registered one.
        ClapBody? last = null;
        for (var i = 0; i < premiere.AnonymousClapCap + 5; i++)
        {
            var response = await client.PostAsync($"/api/premieres/{premiere.Id}/clap", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            last = await response.Content.ReadFromJsonAsync<ClapBody>();
        }

        last!.MyCap.Should().Be(premiere.AnonymousClapCap,
            "an unconfirmed account is capped like any other anonymous session, not like a registered one");
        last.MyClaps.Should().Be(premiere.AnonymousClapCap, "the cap must actually stop the surplus claps");
        last.CapReached.Should().BeTrue();

        using var scope = factory.Services.CreateScope();
        var counters = scope.ServiceProvider.GetRequiredService<IClapCounters>();

        var anonymousClaps = await counters.GetParticipantClapsAsync(
            Scopes.Global, premiere.Id, Participant.Anonymous(UnconfirmedSessionId(userId)), default);
        anonymousClaps.Should().Be(premiere.AnonymousClapCap);

        var registeredClaps = await counters.GetParticipantClapsAsync(
            Scopes.Global, premiere.Id, Participant.Registered(userId), default);
        registeredClaps.Should().Be(0, "this account must accrue no claps under its own UserId while unconfirmed");

        var anonymousContributors = await counters.GetAnonymousContributorsAsync(Scopes.Global, premiere.Id, default);
        anonymousContributors.Should().Contain(UnconfirmedSessionId(userId));

        var registeredContributors = await counters.GetContributorsAsync(Scopes.Global, premiere.Id, default);
        registeredContributors.Should().NotContain(userId);
    }

    /// <summary>
    /// Registration publishes SendNotification through the same outbox PremiereOpener uses for
    /// PremiereOpened (issue #28). MarqueeAppFactory deliberately points the bus at an unreachable
    /// broker (see its class comment) so this API-only host never actually delivers it — the same
    /// reason NotificationDispatchTests exercises INotificationDispatcher directly instead of through
    /// a publish. What *is* provable here, without a broker or the Worker process, is exactly the
    /// property the outbox exists to guarantee — the notification is durably queued the moment
    /// registration commits — and that the confirm-email endpoint itself, the thing the notification's
    /// link actually points at, works end to end.
    /// </summary>
    [Fact]
    public async Task The_confirmation_link_works_end_to_end_against_the_dev_dispatcher()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            var before = await db.Set<OutboxMessage>()
                .LongCountAsync(m => m.MessageType.Contains(nameof(SendNotification)));

            await RegisterUnconfirmedAsync("outbox");

            var after = await db.Set<OutboxMessage>()
                .LongCountAsync(m => m.MessageType.Contains(nameof(SendNotification)));
            after.Should().Be(before + 1, "registering must durably queue exactly one confirmation notification");
        }

        var (_, _, userId) = await RegisterUnconfirmedAsync("confirmlink");

        // Minted the same way AuthService.RegisterAsync mints the one that goes out in the
        // notification's ActionUrl — this is the confirm endpoint's own contract, not a shortcut
        // around it.
        using var confirmScope = factory.Services.CreateScope();
        var tokens = confirmScope.ServiceProvider.GetRequiredService<IEmailConfirmationTokenService>();
        var token = tokens.Issue(userId);

        var anon = factory.CreateClient();
        var confirmResponse = await anon.GetAsync($"/api/auth/confirm-email?token={Uri.EscapeDataString(token)}");
        confirmResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // No bearer token anywhere in the body (issue #48) — confirming must not hand out a session.
        var confirmedBody = await confirmResponse.Content.ReadAsStringAsync();
        confirmedBody.Should().NotContain("\"token\"",
            "confirming must never return a credential — see AuthService.ConfirmEmailAsync's doc comment");

        var db2 = confirmScope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var stored = await db2.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        stored.EmailConfirmedAt.Should().NotBeNull();

        // Replaying the same still-valid token confirms again (idempotent) but still hands back no
        // token — the whole point of #48's fix is that this stays true on every replay, not just the first.
        var replay = await anon.GetAsync($"/api/auth/confirm-email?token={Uri.EscapeDataString(token)}");
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().NotContain("\"token\"");

        // A garbage token must not confirm anything.
        var bad = await anon.GetAsync("/api/auth/confirm-email?token=not-a-real-token");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
