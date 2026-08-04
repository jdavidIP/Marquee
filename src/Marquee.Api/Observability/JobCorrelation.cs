using Marquee.Infrastructure.Observability;
using Quartz;

namespace Marquee.Api.Observability;

/// <summary>
/// Gives a scheduled job the same kind of correlation id an HTTP request gets.
///
/// Without this, the most interesting journey in the system would be the one that could not be
/// traced: a Premiere that reaches the end of its 60 minutes is auto-opened by the tick job (§4.5),
/// which publishes <c>PremiereOpened</c> with no ambient id, so the worker's fan-out logs would carry
/// a correlation id invented on the receiving side and matching nothing on the API's.
///
/// The id is readable rather than random on purpose — <c>job-premiere-tick-{fireInstanceId}</c> says
/// what caused the work, and Quartz's fire instance id already distinguishes one firing from the
/// next, so it is unique without being opaque.
/// </summary>
public static class JobCorrelation
{
    public static IDisposable BeginCorrelationScope(this IJobExecutionContext context) =>
        CorrelationIdContext.Push($"job-{context.JobDetail.Key.Name}-{context.FireInstanceId}");
}
