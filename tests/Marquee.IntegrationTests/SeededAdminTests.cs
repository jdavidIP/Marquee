using System.Net;
using System.Net.Http.Headers;
using Amazon.CognitoIdentityProvider;
using Amazon.Runtime;
using FluentAssertions;
using Marquee.Api.Auth;
using Marquee.Domain.Enums;
using Marquee.Domain.Options;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

        await WithScopeAsync(async sp =>
        {
            var seeder = new AdminSeeder(
                unreachable,
                sp.GetRequiredService<MarqueeDbContext>(),
                sp.GetRequiredService<IPasswordHasherService>(),
                sp.GetRequiredService<IOptions<CognitoOptions>>(),
                sp.GetRequiredService<IOptions<PasswordPolicyOptions>>(),
                sp.GetRequiredService<IConfiguration>(),
                NullLogger<AdminSeeder>.Instance);

            await seeder.Invoking(s => s.SeedAsync(CancellationToken.None)).Should().NotThrowAsync();
            return true;
        });

        (await CountUsersAsync()).Should().Be(before);
    }
}
