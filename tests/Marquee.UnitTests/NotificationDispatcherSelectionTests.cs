using FluentAssertions;
using Marquee.Infrastructure;
using Marquee.Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.UnitTests;

/// <summary>
/// AddMarqueeInfrastructure's choice of INotificationDispatcher (CLAUDE.md §6), exercised the same
/// way TmdbResilienceTests exercises the ITmdbClient selection: build a real ServiceCollection off a
/// real configuration and check what gets resolved, rather than trusting the wiring by inspection.
/// </summary>
public class NotificationDispatcherSelectionTests
{
    private static IServiceProvider BuildProvider(string? smtpHost)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused;Username=u;Password=p",
        };
        if (smtpHost is not null)
            settings["Email:SmtpHost"] = smtpHost;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMarqueeInfrastructure(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void No_SmtpHost_selects_the_dev_dispatcher()
    {
        var dispatcher = BuildProvider(smtpHost: null).GetRequiredService<INotificationDispatcher>();

        dispatcher.Should().BeOfType<DevNotificationDispatcher>(
            "development must run without a mail account, the same guarantee StubTmdbClient gives for TMDB");
    }

    [Fact]
    public void A_configured_SmtpHost_selects_the_email_dispatcher()
    {
        var dispatcher = BuildProvider(smtpHost: "smtp.example.com").GetRequiredService<INotificationDispatcher>();

        dispatcher.Should().BeOfType<EmailNotificationDispatcher>(
            "wiring a real mail account should be a configuration change, not new code");
    }
}
