using Marquee.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Marquee.UnitTests;

public class RequiredConfigurationTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Passes_when_every_key_is_set()
    {
        var config = Config(new() { ["Tmdb:ApiKey"] = "k", ["Admin:Password"] = "p" });

        config.RequireKeys("Tmdb:ApiKey", "Admin:Password");
    }

    [Fact]
    public void Names_every_missing_or_blank_key_with_its_env_var_form()
    {
        var config = Config(new() { ["Tmdb:ApiKey"] = "k", ["Admin:Password"] = "  " });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            config.RequireKeys("Tmdb:ApiKey", "Admin:Password", "EmailConfirmation:BaseUrl"));

        Assert.Contains("Admin:Password (env Admin__Password)", ex.Message);
        Assert.Contains("EmailConfirmation:BaseUrl (env EmailConfirmation__BaseUrl)", ex.Message);
        Assert.DoesNotContain("Tmdb", ex.Message);
    }
}
