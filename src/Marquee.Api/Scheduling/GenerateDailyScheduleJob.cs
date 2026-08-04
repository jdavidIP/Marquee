using Marquee.Api.Observability;
using Marquee.Api.Services;
using Microsoft.Extensions.Options;
using Quartz;

namespace Marquee.Api.Scheduling;

/// <summary>
/// Draws the Premiere times for the coming days (§4.4). Runs just after midnight and once at
/// startup. <see cref="DisallowConcurrentExecution"/> plus the per-day guard inside
/// <see cref="IPremiereScheduleService.GenerateDayAsync"/> mean the startup run and the cron run can
/// overlap harmlessly — the second one finds the day already full and does nothing.
/// </summary>
[DisallowConcurrentExecution]
public sealed class GenerateDailyScheduleJob(
    IPremiereScheduleService schedule,
    IOptions<SchedulerOptions> options,
    ILogger<GenerateDailyScheduleJob> logger) : IJob
{
    public static readonly JobKey Key = new("generate-daily-schedule");

    private readonly SchedulerOptions _options = options.Value;

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        using var correlation = context.BeginCorrelationScope();

        var today = DateOnly.FromDateTime(DateTime.Now); // §4.4 is expressed in local time

        for (var i = 0; i < Math.Max(1, _options.GenerateDaysAhead); i++)
        {
            var date = today.AddDays(i);
            try
            {
                await schedule.GenerateDayAsync(date, ct);
            }
            catch (NoMovieAvailableException ex)
            {
                // TMDB has nothing fresh. Tomorrow's run tries again; today's already-created
                // Premieres are unaffected. (Resilience around TMDB lands in Iteration 6.)
                logger.LogWarning(ex, "Could not generate the schedule for {Date} — no movie available.", date);
                break;
            }
        }
    }
}
