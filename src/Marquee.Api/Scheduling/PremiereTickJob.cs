using Marquee.Api.Observability;
using Marquee.Api.Services;
using Quartz;

namespace Marquee.Api.Scheduling;

/// <summary>
/// The heartbeat that moves Premieres through their lifecycle: activate the ones whose time has
/// come, then auto-open the ones that have run out their 60 minutes (§4.5).
///
/// Expiry runs after activation so a Premiere is never activated and expired in the same tick, and
/// both operations are idempotent conditional updates — a tick that overlaps a threshold-crossing
/// clap cannot double-open, because they share the same exactly-once opener.
/// </summary>
[DisallowConcurrentExecution]
public sealed class PremiereTickJob(
    IPremiereScheduleService schedule,
    ILogger<PremiereTickJob> logger) : IJob
{
    public static readonly JobKey Key = new("premiere-tick");

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        // Labels this firing and everything it publishes, so an auto-open is traceable into the
        // worker exactly like a clap-driven one.
        using var correlation = context.BeginCorrelationScope();

        try
        {
            await schedule.ActivateDueAsync(ct);
            await schedule.ExpireDueAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Never let a bad tick stop the trigger — the next one re-derives everything from the DB.
            logger.LogError(ex, "Premiere tick failed.");
        }
    }
}
