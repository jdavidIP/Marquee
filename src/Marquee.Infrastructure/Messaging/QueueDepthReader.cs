using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marquee.Infrastructure.Messaging;

/// <summary>How many messages are sitting in a queue, and how many have been dead-lettered.</summary>
public sealed record QueueDepth(string Queue, long Ready, long Unacknowledged, bool Available)
{
    public long Total => Ready + Unacknowledged;
}

public interface IQueueDepthReader
{
    Task<QueueDepth> ReadAsync(string queueName, CancellationToken ct);
}

/// <summary>
/// Reads queue depth from RabbitMQ's HTTP management API (Iteration 6).
///
/// Management API rather than AMQP on purpose: AMQP can only report a queue's depth as a side effect
/// of declaring it or consuming from it, and a dashboard must never be able to alter the thing it is
/// reporting on. The management API is a read.
///
/// <para>
/// Every failure is swallowed into <see cref="QueueDepth.Available"/> = false. A monitoring endpoint
/// that throws when the thing it monitors is down is the least useful possible behaviour: the
/// dashboard would go blank at precisely the moment someone opened it to find out what was wrong.
/// </para>
/// </summary>
public sealed class RabbitMqQueueDepthReader : IQueueDepthReader
{
    private readonly HttpClient _http;
    private readonly ILogger<RabbitMqQueueDepthReader> _logger;

    public RabbitMqQueueDepthReader(
        HttpClient http, IOptions<MessagingOptions> options, ILogger<RabbitMqQueueDepthReader> logger)
    {
        var opts = options.Value;
        _logger = logger;
        _http = http;
        _http.BaseAddress = new Uri($"http://{opts.Host}:{opts.ManagementPort}/");
        _http.Timeout = TimeSpan.FromSeconds(5);

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{opts.Username}:{opts.Password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        // "/" is RabbitMQ's default vhost and has to be percent-encoded twice to survive the path.
        _vhost = Uri.EscapeDataString(opts.VirtualHost);
    }

    private readonly string _vhost;

    public async Task<QueueDepth> ReadAsync(string queueName, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync($"api/queues/{_vhost}/{Uri.EscapeDataString(queueName)}", ct);
            if (!response.IsSuccessStatusCode)
            {
                // A queue that has never been declared 404s. That is a legitimate state, not an
                // error: nothing has been published through it yet.
                return new QueueDepth(queueName, 0, 0, Available: false);
            }

            var body = await response.Content.ReadFromJsonAsync<QueueResponse>(ct);
            return new QueueDepth(queueName, body?.Ready ?? 0, body?.Unacknowledged ?? 0, Available: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Could not read depth for queue {Queue}.", queueName);
            return new QueueDepth(queueName, 0, 0, Available: false);
        }
    }

    private sealed record QueueResponse(
        [property: JsonPropertyName("messages_ready")] long Ready,
        [property: JsonPropertyName("messages_unacknowledged")] long Unacknowledged);
}
