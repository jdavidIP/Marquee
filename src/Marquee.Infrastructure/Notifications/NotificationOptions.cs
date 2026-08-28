namespace Marquee.Infrastructure.Notifications;

/// <summary>
/// Email delivery settings (CLAUDE.md §7 — tunable, not hard-coded). SmtpHost being set is what
/// selects EmailNotificationDispatcher over DevNotificationDispatcher in AddMarqueeInfrastructure,
/// the same way TmdbOptions.ApiKey selects between TmdbClient and StubTmdbClient.
/// </summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Email";

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
    public string FromAddress { get; set; } = "no-reply@marquee.local";
    public string FromName { get; set; } = "Marquee";
}
