using Marquee.Api.Dtos;
using Marquee.Api.Realtime;
using Marquee.Api.Services;
using Marquee.Infrastructure.Messaging;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Tmdb;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Messaging;

/// <summary>
/// The last leg of the open: the worker has durably written every library entry and emblem, and says
/// so; the API — the only process holding SignalR connections while v1 runs without a backplane
/// (CLAUDE.md §6) — turns that into the reveal.
///
/// Deliberately not wrapped in the inbox. It writes nothing, so there is no state to protect, and a
/// duplicated reveal is inert: clients receive the same notification and render the same already-open
/// Premiere. Paying for a transaction per reveal to prevent a harmless repeat would be the wrong trade.
/// </summary>
public sealed class PremiereRevealReadyConsumer(
    MarqueeDbContext db,
    IPremiereBroadcaster broadcaster,
    IOptions<TmdbOptions> tmdbOptions,
    ILogger<PremiereRevealReadyConsumer> logger) : IConsumer<PremiereRevealReady>
{
    private readonly TmdbOptions _tmdb = tmdbOptions.Value;

    public async Task Consume(ConsumeContext<PremiereRevealReady> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var movie = await db.Movies.AsNoTracking().FirstOrDefaultAsync(m => m.Id == msg.MovieId, ct);
        if (movie is null)
        {
            // The Premiere's movie is resolved and stored at creation time (§4.6), so this cannot
            // happen for a real Premiere. Permanent, not transient — dead-letter rather than retry.
            throw new PermanentMessageException(
                $"Movie {msg.MovieId} for Premiere {msg.PremiereId} is missing; cannot build the reveal.");
        }

        var notification = new PremiereOpenedNotification(
            msg.PremiereId,
            msg.Status,
            msg.TotalClaps,
            msg.Threshold,
            msg.Contributors,
            msg.OpenedAt,
            MovieDtoFactory.Create(movie, _tmdb));

        await broadcaster.PremiereOpenedAsync(msg.ScopeId, notification, ct);

        logger.LogInformation(
            "Revealed Premiere {PremiereId} ({Status}) to scope {ScopeId}: {Claps} claps, {Contributors} contributors.",
            msg.PremiereId, msg.Status, msg.ScopeId, msg.TotalClaps, msg.Contributors);
    }
}
