using System.Net;
using System.Net.Http.Headers;
using Amazon.CognitoIdentityProvider;
using Amazon.Runtime;
using FluentAssertions;
using Marquee.Api.Auth;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Marquee.IntegrationTests;

/// <summary>
/// Issue #110 / DEPLOYMENT.md § Phase 2 decision 5: the startup seeder creates the admin in Cognito
/// first and gives its Postgres row the Cognito <c>sub</c> as its id. The factory's host has already
/// run it against cognito-local by the time any test here starts.
///
/// Named to sort after FixtureSanityTests, with the other suites that touch user rows.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SeededAdminTests(MarqueeAppFactory factory)
{
    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    private Task<int> CountUsersAsync() =>
        WithScopeAsync(sp => sp.GetRequiredService<MarqueeDbContext>().Users.CountAsync());

    [Fact]
    public async Task The_admin_is_confirmed_in_cognito_and_its_row_carries_the_same_id()
    {
        var (status, sub) = await factory.Cognito.GetUserAsync(MarqueeAppFactory.AdminUsername);

        status.Should().Be("CONFIRMED", "the password is set as permanent, so it never waits on a forced change");
        var row = await WithScopeAsync(sp => sp.GetRequiredService<MarqueeDbContext>().Users
            .AsNoTracking().SingleAsync(u => u.Username == MarqueeAppFactory.AdminUsername));
        row.Id.Should().Be(sub);
        row.Role.Should().Be(UserRole.Admin);
        row.EmailConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task The_admin_signs_in_through_cognito_and_reaches_admin_endpoints()
    {
        var tokens = await factory.Cognito.SignInAsync(MarqueeAppFactory.AdminUsername, MarqueeAppFactory.AdminPassword);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        (await client.GetAsync("/api/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Seeding_again_changes_nothing()
    {
        var before = await CountUsersAsync();
        var (_, sub) = await factory.Cognito.GetUserAsync(MarqueeAppFactory.AdminUsername);

        await WithScopeAsync(async sp =>
        {
            await sp.GetRequiredService<AdminSeeder>().SeedAsync(CancellationToken.None);
            return true;
        });

        (await CountUsersAsync()).Should().Be(before);
        (await factory.Cognito.GetUserAsync(MarqueeAppFactory.AdminUsername)).Should().Be(("CONFIRMED", sub));
        // Still the same password: an existing admin's is never reset.
        await factory.Cognito.SignInAsync(MarqueeAppFactory.AdminUsername, MarqueeAppFactory.AdminPassword);
    }

    [Fact]
    public async Task An_unreachable_pool_is_logged_and_does_not_stop_startup()
    {
        var before = await CountUsersAsync();
        using var unreachable = new AmazonCognitoIdentityProviderClient(
            new BasicAWSCredentials("local", "local"),
            new AmazonCognitoIdentityProviderConfig
            {
                ServiceURL = "http://127.0.0.1:1",
                AuthenticationRegion = "local",
                MaxErrorRetry = 0,
                Timeout = TimeSpan.FromSeconds(2),
            });

        var errors = await SeedWithAsync(admin: null, unreachable);

        errors.Should().ContainSingle().Which.Should().Contain("Could not seed admin");
        (await CountUsersAsync()).Should().Be(before);
    }

    [Fact]
    public async Task A_start_that_died_after_creating_the_pool_user_is_finished_by_the_next()
    {
        // Created in the pool, never given a password, no row: what a start that crashed between
        // AdminCreateUser and AdminSetUserPassword leaves behind. The pool's email also differs from
        // the configured one, as it would after Admin:Email changed — the row must follow the pool.
        var username = $"half_{Guid.NewGuid():n}"[..20];
        await factory.Cognito.AdminCreateUserAsync(username, $"{username}@pool.example.test");

        var errors = await SeedWithAsync(new()
        {
            ["Admin:Username"] = username,
            ["Admin:Email"] = $"{username}@config.example.test",
            ["Admin:Password"] = Password,
        });

        errors.Should().BeEmpty();
        var (status, sub) = await factory.Cognito.GetUserAsync(username);
        status.Should().Be("CONFIRMED");
        await factory.Cognito.SignInAsync(username, Password);
        var row = await WithScopeAsync(sp => sp.GetRequiredService<MarqueeDbContext>().Users
            .AsNoTracking().SingleAsync(u => u.Id == sub));
        row.Role.Should().Be(UserRole.Admin);
        row.Email.Should().Be($"{username}@pool.example.test");
    }

    [Fact]
    public async Task A_row_from_before_cognito_is_reported_not_overwritten()
    {
        var username = $"old_{Guid.NewGuid():n}"[..20];
        var oldId = Guid.NewGuid();
        await WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MarqueeDbContext>();
            db.Users.Add(new User { Id = oldId, Username = username, Email = $"{username}@example.test" });
            return await db.SaveChangesAsync();
        });

        var errors = await SeedWithAsync(new()
        {
            ["Admin:Username"] = username,
            ["Admin:Email"] = $"{username}@example.test",
            ["Admin:Password"] = Password,
        });

        errors.Should().ContainSingle().Which.Should().Contain("docker compose down -v");
        var (_, sub) = await factory.Cognito.GetUserAsync(username);
        (await WithScopeAsync(sp => sp.GetRequiredService<MarqueeDbContext>().Users.AnyAsync(u => u.Id == sub)))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_username_the_table_cannot_hold_is_refused_before_cognito_is_touched()
    {
        var username = $"long_{Guid.NewGuid():n}{Guid.NewGuid():n}"[..(User.UsernameMaxLength + 1)];

        var errors = await SeedWithAsync(new()
        {
            ["Admin:Username"] = username,
            ["Admin:Email"] = "long@example.test",
            ["Admin:Password"] = Password,
        });

        errors.Should().ContainSingle().Which.Should().Contain("longer than the users table allows");
        await factory.Invoking(f => f.Cognito.GetUserAsync(username)).Should()
            .ThrowAsync<InvalidOperationException>("no pool user may be left behind");
    }

    private const string Password = "seeded-admin-test-1";

    /// <summary>
    /// Runs a seeder against the test pool — or <paramref name="cognito"/> — with the given Admin:*
    /// settings in place of the factory's, and returns the errors it logged.
    /// </summary>
    private Task<List<string>> SeedWithAsync(
        Dictionary<string, string?>? admin, IAmazonCognitoIdentityProvider? cognito = null) =>
        WithScopeAsync(async sp =>
        {
            var config = admin is null
                ? sp.GetRequiredService<IConfiguration>()
                : new ConfigurationBuilder().AddInMemoryCollection(admin).Build();
            var logger = new ErrorLog();
            var seeder = new AdminSeeder(
                cognito ?? sp.GetRequiredService<IAmazonCognitoIdentityProvider>(),
                sp.GetRequiredService<MarqueeDbContext>(),
                sp.GetRequiredService<IOptions<CognitoOptions>>(),
                config,
                logger);

            await seeder.Invoking(s => s.SeedAsync(CancellationToken.None)).Should().NotThrowAsync();
            return logger.Errors;
        });

    private sealed class ErrorLog : ILogger<AdminSeeder>
    {
        public List<string> Errors { get; } = [];

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format)
        {
            if (level >= LogLevel.Error)
                Errors.Add(format(state, exception));
        }

        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
