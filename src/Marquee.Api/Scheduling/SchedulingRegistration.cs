using Marquee.Domain.Options;
using Quartz;

namespace Marquee.Api.Scheduling;

/// <summary>
/// Quartz.NET wiring.
///
/// <para><b>Quartz over Hangfire (CLAUDE.md §2 asks for the choice to be documented).</b> The work
/// here is time-triggered, not queued: "generate tomorrow's schedule at 00:05", "check every minute
/// for a Premiere that is due". Quartz is a scheduler and models exactly that, with cron triggers,
/// misfire policies, and <c>DisallowConcurrentExecution</c> built in. Hangfire is fundamentally a
/// background <i>job queue</i> with a persistent store and a dashboard — most of what it adds over
/// Quartz here is queueing, which is Iteration 4's job and belongs to RabbitMQ/MassTransit, not to
/// the scheduler. Using Hangfire too would mean two overlapping persistence stories for background
/// work. Quartz also runs purely in memory by default, which suits a single instance (§6 puts
/// multi-instance deployment out of scope) and keeps the schedule out of the application database.</para>
///
/// <para>The trade-off, stated plainly: an in-memory scheduler forgets its triggers on restart. That
/// is fine because none of these jobs carry state — the day's Premieres are rows in Postgres, and
/// every job re-derives what to do from those rows and is idempotent, so a restart simply picks up
/// where it left off on the next tick.</para>
/// </summary>
public static class SchedulingRegistration
{
    public static IServiceCollection AddMarqueeScheduling(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MarqueeScheduleOptions>(configuration.GetSection(MarqueeScheduleOptions.SectionName));
        services.Configure<SchedulerOptions>(configuration.GetSection(SchedulerOptions.SectionName));

        var options = configuration.GetSection(SchedulerOptions.SectionName).Get<SchedulerOptions>()
                      ?? new SchedulerOptions();
        if (!options.Enabled)
            return services;

        services.AddQuartz(q =>
        {
            // Draw the day's schedule shortly after midnight, and once at startup so a fresh database
            // is not left waiting until tomorrow for its first Premiere.
            q.AddJob<GenerateDailyScheduleJob>(j => j.WithIdentity(GenerateDailyScheduleJob.Key));
            q.AddTrigger(t => t
                .ForJob(GenerateDailyScheduleJob.Key)
                .WithIdentity("generate-daily-schedule-cron")
                .WithCronSchedule(options.DailyGenerationCron, c => c.WithMisfireHandlingInstructionFireAndProceed()));
            q.AddTrigger(t => t
                .ForJob(GenerateDailyScheduleJob.Key)
                .WithIdentity("generate-daily-schedule-startup")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(options.StartupDelaySeconds)));

            // One tick drives both transitions: Scheduled -> Active at its time, and Active ->
            // AutoOpened once the 60 minutes are up (§4.5).
            q.AddJob<PremiereTickJob>(j => j.WithIdentity(PremiereTickJob.Key));
            q.AddTrigger(t => t
                .ForJob(PremiereTickJob.Key)
                .WithIdentity("premiere-tick")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(options.StartupDelaySeconds))
                .WithSimpleSchedule(s => s
                    .WithInterval(TimeSpan.FromSeconds(options.TickIntervalSeconds))
                    .RepeatForever()
                    .WithMisfireHandlingInstructionNextWithRemainingCount()));
        });

        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
        return services;
    }
}
