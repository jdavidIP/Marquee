using Marquee.Infrastructure.Observability;

namespace Marquee.Api.Observability;

/// <summary>
/// Gives every request a correlation id and makes it ambient for the whole request, so the API's own
/// log lines, and every message it publishes, carry the same one.
///
/// The id is taken from the caller's <c>X-Correlation-Id</c> header when they send one — that is what
/// lets a load test, a browser, or a support ticket name a journey and then find exactly that string
/// in both services' logs — and generated otherwise. Either way it is echoed back on the response, so
/// a caller that did not supply one still learns the id its request was filed under.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Long enough for a UUID or a caller's own scheme, short enough that a log line cannot be padded
    /// out with a megabyte of attacker-chosen text.
    /// </summary>
    private const int MaxLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Sanitise(context.Request.Headers[MarqueeHeaders.CorrelationId])
                            ?? CorrelationIdContext.NewId();

        // Set before the response starts: once the first byte is on the wire the headers are frozen,
        // and a streaming endpoint (the SignalR negotiate, say) can start writing at any point.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[MarqueeHeaders.CorrelationId] = correlationId;
            return Task.CompletedTask;
        });

        using (CorrelationIdContext.Push(correlationId))
        {
            await next(context);
        }
    }

    /// <summary>
    /// Client-controlled input that is written straight into log output, so it is filtered rather
    /// than trusted. A correlation id containing a newline would let a caller forge whole log lines
    /// — claiming an error that never happened, or hiding a real one under plausible-looking noise.
    /// Anything outside printable ASCII is rejected rather than escaped, because a caller with a
    /// legitimate id has no reason to send control characters, and rejecting simply means they get a
    /// generated one instead.
    /// </summary>
    private static string? Sanitise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
            return null;

        foreach (var c in trimmed)
        {
            if (c is < ' ' or > '~')
                return null;
        }

        return trimmed;
    }
}

public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();
}
