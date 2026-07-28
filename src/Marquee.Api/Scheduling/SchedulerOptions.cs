namespace Marquee.Api.Scheduling;

public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";

    /// <summary>
    /// Lets the load tests and the integration suite run without the scheduler activating or
    /// auto-opening Premieres underneath them.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>When to draw the coming day's Premiere times. Quartz cron, in server local time.</summary>
    public string DailyGenerationCron { get; set; } = "0 5 0 * * ?";

    /// <summary>
    /// How often to look for a Premiere that is due to activate or has run out its 60 minutes.
    /// Premiere times have minute granularity, so a sub-minute tick keeps the lag imperceptible
    /// without the job doing anything expensive — both queries are indexed status lookups.
    /// </summary>
    public int TickIntervalSeconds { get; set; } = 20;

    /// <summary>Small delay so startup migrations and seeding finish before the first tick.</summary>
    public int StartupDelaySeconds { get; set; } = 10;

    /// <summary>How many days ahead to keep generated, including today.</summary>
    public int GenerateDaysAhead { get; set; } = 2;
}
