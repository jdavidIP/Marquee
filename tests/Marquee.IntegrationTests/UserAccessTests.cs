using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Marquee.IntegrationTests;

/// <summary>
/// Issue #109 / DEPLOYMENT.md § Phase 2 decisions 2 and 7: a Cognito account's first request creates
/// its user row, and what any request may do — blocked or not, which permissions — is read from that
/// row on every request rather than from the token.
///
/// Named to sort after FixtureSanityTests: every test here creates user rows.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class UserAccessTests(MarqueeAppFactory factory)
{
    private const string Password = "access-tests-password-1";

    private sealed record Me(Guid Id, string Username, string Email, string Role, bool EmailConfirmed, List<string> Permissions);

    private static string NewUsername() => $"ua_{Guid.NewGuid():n}"[..20];

    private HttpClient ClientWith(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>Changes a row the way an admin edit would, including dropping the cached access.</summary>
    private Task UpdateUserAsync(Guid userId, Action<User> change) => WithScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<MarqueeDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        change(user);
        await db.SaveChangesAsync();
        await sp.GetRequiredService<IUserAccessCache>().InvalidateAsync(userId, CancellationToken.None);
        return true;
    });

    [Fact]
    public async Task A_cognito_accounts_first_request_creates_its_confirmed_row_keyed_by_sub()
    {
        var username = NewUsername();
        var tokens = await factory.Cognito.CreateUserAsync(username, Password);
        var sub = Guid.Parse(new JsonWebToken(tokens.AccessToken).Subject);

        var response = await ClientWith(tokens.AccessToken).GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var me = await response.Content.ReadFromJsonAsync<Me>();
        me!.Id.Should().Be(sub);
        me.Username.Should().Be(username);
        me.Email.Should().Be($"{username}@example.test", "it comes from GetUser — access tokens carry no email");
        me.Role.Should().Be(nameof(UserRole.User));
        me.EmailConfirmed.Should().BeTrue("Cognito only issues tokens to confirmed accounts");
        me.Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task Parallel_first_requests_create_exactly_one_row()
    {
        var tokens = await factory.Cognito.CreateUserAsync(NewUsername(), Password);
        var sub = Guid.Parse(new JsonWebToken(tokens.AccessToken).Subject);

        // A page load fires several requests at once; every one of them must succeed, and only one
        // may insert.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => ClientWith(tokens.AccessToken).GetAsync("/api/friends")));

        responses.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        (await WithScopeAsync(sp => sp.GetRequiredService<MarqueeDbContext>().Users.CountAsync(u => u.Id == sub)))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_username_already_held_by_another_row_is_refused_not_merged()
    {
        var username = NewUsername();
        var otherId = Guid.NewGuid();
        await WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MarqueeDbContext>();
            db.Users.Add(new User { Id = otherId, Username = username, Email = $"other-{username}@example.test", PasswordHash = "" });
            return await db.SaveChangesAsync();
        });
        var tokens = await factory.Cognito.CreateUserAsync(username, Password);
        var sub = Guid.Parse(new JsonWebToken(tokens.AccessToken).Subject);

        var response = await ClientWith(tokens.AccessToken).GetAsync("/api/friends");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await WithScopeAsync(sp => sp.GetRequiredService<MarqueeDbContext>().Users.AnyAsync(u => u.Id == sub)))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Permissions_come_from_the_current_role_not_the_token()
    {
        var tokens = await factory.Cognito.CreateUserAsync(NewUsername(), Password);
        var sub = Guid.Parse(new JsonWebToken(tokens.AccessToken).Subject);
        var client = ClientWith(tokens.AccessToken);
        (await client.GetAsync("/api/admin/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await UpdateUserAsync(sub, u => u.Role = UserRole.Admin);

        // Same token, next request.
        (await client.GetAsync("/api/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);
        var me = await client.GetFromJsonAsync<Me>("/api/auth/me");
        me!.Permissions.Should().Contain("users:view");
    }

    [Fact]
    public async Task Permissions_stamped_into_a_legacy_token_are_not_trusted()
    {
        var username = NewUsername();
        var anonymous = factory.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            email = $"{username}@example.test",
            password = Password,
            confirmPassword = Password,
        })).EnsureSuccessStatusCode();
        var userId = await WithScopeAsync(sp =>
            sp.GetRequiredService<MarqueeDbContext>().Users.Where(u => u.Username == username).Select(u => u.Id).SingleAsync());

        // Signed in while an admin, so the token carries admin permission claims...
        await UpdateUserAsync(userId, u => u.Role = UserRole.Admin);
        var login = await anonymous.PostAsJsonAsync("/api/auth/login", new { usernameOrEmail = username, password = Password });
        var token = (await login.Content.ReadFromJsonAsync<LoginBody>())!.Token;
        var client = ClientWith(token);
        (await client.GetAsync("/api/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);

        // ...which stop counting the moment the role is taken away.
        await UpdateUserAsync(userId, u => u.Role = UserRole.User);
        (await client.GetAsync("/api/admin/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Blocking_a_cognito_account_refuses_its_next_request()
    {
        var tokens = await factory.Cognito.CreateUserAsync(NewUsername(), Password);
        var sub = Guid.Parse(new JsonWebToken(tokens.AccessToken).Subject);
        var client = ClientWith(tokens.AccessToken);
        (await client.GetAsync("/api/friends")).StatusCode.Should().Be(HttpStatusCode.OK);

        var admin = ClientWith(await factory.AdminTokenAsync());
        (await admin.PostAsJsonAsync($"/api/admin/users/{sub}/block", new { reason = "test" })).EnsureSuccessStatusCode();

        (await client.GetAsync("/api/friends")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed record LoginBody(string Token);
}
