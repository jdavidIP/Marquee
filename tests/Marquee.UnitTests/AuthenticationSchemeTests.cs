using FluentAssertions;
using Marquee.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Marquee.UnitTests;

/// <summary>
/// Issue #109: the API's own HMAC tokens are accepted outside Production only — in Production the
/// only way in is a Cognito access token (DEPLOYMENT.md § Phase 2, decision 9). The integration
/// tests run as Development, so they cannot show the Production half; this pins it.
/// </summary>
public class AuthenticationSchemeTests
{
    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    public async Task The_legacy_scheme_is_registered_only_outside_production(string environment, bool registered)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cognito:Issuer"] = "https://cognito-idp.ca-central-1.amazonaws.com/ca-central-1_example",
                ["Cognito:ClientId"] = "client",
                ["Jwt:Key"] = new string('k', 32),
            })
            .Build();
        var services = new ServiceCollection().AddLogging();

        services.AddMarqueeAuthentication(configuration, new Environment(environment));
        var schemes = services.BuildServiceProvider().GetRequiredService<IAuthenticationSchemeProvider>();

        (await schemes.GetSchemeAsync(AuthenticationRegistration.CognitoScheme)).Should().NotBeNull();
        (await schemes.GetSchemeAsync(AuthenticationRegistration.LegacyScheme) is not null).Should().Be(registered);
    }

    private sealed class Environment(string name) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Marquee.Api";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
