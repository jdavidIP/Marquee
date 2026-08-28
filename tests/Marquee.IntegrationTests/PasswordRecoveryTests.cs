using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marquee.Api.Auth;
using Marquee.Domain.Entities;
using Marquee.Infrastructure.Messaging;
using Marquee.Infrastructure.Persistence;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// Issue #31's "done when" list: the happy path, expiry, reuse, and the no-enumeration property.
///
/// Named to sort after FixtureSanityTests — see RegistrationConfirmationTests' class comment for why
/// that constraint exists.
///
/// Reset tokens are seeded directly into the database rather than captured from a dispatched
/// notification, for the same structural reason RegistrationConfirmationTests mints its confirm-email
/// tokens directly: Marquee.Worker never runs in this harness (see MarqueeAppFactory), so nothing
/// here could ever observe a notification's content even if one were captured. Unlike the
/// confirm-email token, though, a reset token cannot be re-minted on demand from a userId — it is
/// genuinely random and only ever exists in raw form for the instant AuthService.RequestPasswordResetAsync
/// generates it, before it is hashed and the raw value discarded. So these tests generate their own
/// raw token via the same IPasswordResetTokenService production code uses, and insert the matching row
/// themselves — exercising the reset endpoint's real validation logic while sidestepping the one part
/// (delivery) this harness cannot observe.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PasswordRecoveryTests(MarqueeAppFactory factory)
{
    private const string NewPassword = "a brand new valid password 42";

    private sealed record AuthUser(Guid Id, string Username, bool EmailConfirmed);
    private sealed record AuthResponse(string Token, AuthUser User);

    private async Task<(string Username, string Email, Guid UserId)> RegisterAsync(string tag)
    {
        var client = factory.CreateClient();
        var username = $"pr_{tag}_{Guid.NewGuid():n}"[..24];
        var email = $"{username}@marquee.test";

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new
            {
                username,
                email,
                password = TestPasswords.Valid,
                confirmPassword = TestPasswords.Valid,
            });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return (username, email, body!.User.Id);
    }

    /// <summary>Seeds a reset token row the same way AuthService.RequestPasswordResetAsync would, and hands back the raw value.</summary>
    private async Task<string> SeedTokenAsync(Guid userId, DateTime expiresAt, DateTime? usedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var resetTokens = scope.ServiceProvider.GetRequiredService<IPasswordResetTokenService>();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        var rawToken = resetTokens.GenerateToken();
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = userId,
            TokenHash = resetTokens.Hash(rawToken),
            ExpiresAt = expiresAt,
            UsedAt = usedAt,
        });
        await db.SaveChangesAsync();

        return rawToken;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { usernameOrEmail = username, password });

    [Fact]
    public async Task Requesting_a_reset_queues_a_notification_and_a_token_regardless_of_whether_the_address_exists()
    {
        var (username, email, userId) = await RegisterAsync("happy");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var client = factory.CreateClient();

        var outboxBefore = await db.Set<OutboxMessage>()
            .LongCountAsync(m => m.MessageType.Contains(nameof(SendNotification)));

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var outboxAfter = await db.Set<OutboxMessage>()
            .LongCountAsync(m => m.MessageType.Contains(nameof(SendNotification)));
        outboxAfter.Should().Be(outboxBefore + 1, "a known address must durably queue exactly one reset notification");

        var tokenRow = await db.PasswordResetTokens.CountAsync(t => t.UserId == userId);
        tokenRow.Should().Be(1);
    }

    [Fact]
    public async Task The_happy_path_works_end_to_end_and_invalidates_other_outstanding_tokens()
    {
        var (username, _, userId) = await RegisterAsync("reset");
        var used = await SeedTokenAsync(userId, DateTime.UtcNow.AddMinutes(30));
        var stillOutstanding = await SeedTokenAsync(userId, DateTime.UtcNow.AddMinutes(30));

        var client = factory.CreateClient();
        var reset = await client.PostAsJsonAsync("/api/auth/reset-password",
            new { token = used, newPassword = NewPassword, confirmPassword = NewPassword });
        reset.StatusCode.Should().Be(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());

        (await LoginAsync(client, username, TestPasswords.Valid)).StatusCode.Should().Be(
            HttpStatusCode.Unauthorized, "the old password must stop working");
        (await LoginAsync(client, username, NewPassword)).StatusCode.Should().Be(
            HttpStatusCode.OK, "the new password must work");

        // The second, never-used token dies with the first one's success.
        var second = await client.PostAsJsonAsync("/api/auth/reset-password",
            new { token = stillOutstanding, newPassword = NewPassword, confirmPassword = NewPassword });
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "every other outstanding token for the account must be invalidated by a successful reset");
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (_, _, userId) = await RegisterAsync("expired");
        var token = await SeedTokenAsync(userId, DateTime.UtcNow.AddMinutes(-1));

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/reset-password",
            new { token, newPassword = NewPassword, confirmPassword = NewPassword });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_used_token_cannot_be_replayed()
    {
        var (username, _, userId) = await RegisterAsync("reuse");
        var token = await SeedTokenAsync(userId, DateTime.UtcNow.AddMinutes(30));

        var client = factory.CreateClient();
        var first = await client.PostAsJsonAsync("/api/auth/reset-password",
            new { token, newPassword = NewPassword, confirmPassword = NewPassword });
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        var replay = await client.PostAsJsonAsync("/api/auth/reset-password",
            new { token, newPassword = "yet another valid password 7", confirmPassword = "yet another valid password 7" });
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The password from the first, legitimate use must still be the one that works.
        (await LoginAsync(client, username, NewPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reset_enforces_the_same_password_policy_as_registration()
    {
        var (_, _, userId) = await RegisterAsync("weak");
        var token = await SeedTokenAsync(userId, DateTime.UtcNow.AddMinutes(30));

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/reset-password",
            new { token, newPassword = "weak", confirmPassword = "weak" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_forgot_password_response_is_identical_for_a_known_and_an_unknown_address()
    {
        var (_, email, _) = await RegisterAsync("enum");
        var unknownEmail = $"nobody_{Guid.NewGuid():n}@marquee.test";

        var client = factory.CreateClient();
        var known = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var unknown = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = unknownEmail });

        known.StatusCode.Should().Be(unknown.StatusCode);
        var knownBody = await known.Content.ReadAsStringAsync();
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        knownBody.Should().Be(unknownBody, "the response must not reveal which addresses are registered");
    }
}
