using Marquee.Domain.Entities;
using Marquee.Domain.Options;
using Marquee.Domain.Rules;
using Marquee.Infrastructure.Messaging;
using Marquee.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Worker.Consumers;

/// <summary>
/// The open-time fan-out, moved off the request path in Iteration 4. For every contributor in the
/// event's snapshot: one Contribution row carrying the emblem tier (§4.3), and one LibraryEntry
/// unless that user already owns the movie. Once committed, it publishes <see cref="PremiereRevealReady"/>
/// so the API can broadcast the reveal.
///
/// ## Idempotency
///
/// Reprocessing this event — a redelivery after the worker was killed mid-transaction, or the same
/// event published twice by hand — must produce exactly the outcome of processing it once. Three
/// layers get that, deliberately overlapping:
///
/// 1. **The transaction.** The endpoint wraps this consumer in one (UseEntityFrameworkOutbox), so a
///    worker killed halfway through leaves nothing behind: either every row of a fan-out is present
///    or none is. There is no partial state to reconcile on restart.
/// 2. **The inbox.** InboxState records consumed MessageIds, so a broker redelivery of the *same*
///    message is dropped without re-running this code at all.
/// 3. **This method.** A re-*published* event gets a fresh MessageId, so the inbox will not catch it.
///    The insert set is therefore derived from what is already in the database rather than assumed
///    empty, and the unique constraints on (PremiereId, UserId) and (UserId, MovieId) are the
///    backstop if two copies race.
///
/// On that backstop: a constraint violation is allowed to propagate rather than being caught here.
/// Postgres aborts the entire transaction on a failed statement, so there is nothing useful this
/// method could do with the exception — no subsequent statement on that connection would succeed.
/// Letting it throw hands the message to the retry policy, which re-runs the consumer in a *clean*
/// transaction, where the re-read now sees the winner's rows and inserts nothing. The retry is the
/// conflict handler.
/// </summary>
public sealed class PremiereOpenedConsumer(
    MarqueeDbContext db,
    IOptions<MarqueeRulesOptions> rules,
    ILogger<PremiereOpenedConsumer> logger) : IConsumer<PremiereOpened>
{
    private readonly MarqueeRulesOptions _rules = rules.Value;

    public async Task Consume(ConsumeContext<PremiereOpened> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        // Poison-message guard. The outbox only releases this event after the Premiere row is
        // committed, so a missing Premiere means a hand-crafted or long-stale message — no amount of
        // retrying will conjure the row, and the FK on Contribution would fail forever. Permanent,
        // so the retry policy ignores it and RabbitMQ dead-letters the message immediately.
        var premiereExists = await db.Premieres.AnyAsync(p => p.Id == msg.PremiereId, ct);
        if (!premiereExists)
            throw new UnknownPremiereException(msg.PremiereId);

        var written = await FanOutAsync(msg, ct);

        // Published from inside the consumer, so it rides the endpoint's outbox: the reveal event is
        // written in the same transaction as the library rows and released only once they commit.
        // The reveal therefore cannot be announced for a fan-out that later rolled back.
        await context.Publish(
            new PremiereRevealReady(
                msg.PremiereId,
                msg.ScopeId,
                msg.MovieId,
                msg.Status,
                msg.TotalClaps,
                msg.Threshold,
                msg.Contributors.Count,
                msg.OpenedAt),
            ct);

        logger.LogInformation(
            "Fanned out Premiere {PremiereId}: {Contributions} contributions and {Library} library entries written " +
            "({Total} contributors in the event).",
            msg.PremiereId, written.Contributions, written.LibraryEntries, msg.Contributors.Count);
    }

    private readonly record struct FanOutResult(int Contributions, int LibraryEntries);

    private async Task<FanOutResult> FanOutAsync(PremiereOpened msg, CancellationToken ct)
    {
        if (msg.Contributors.Count == 0)
            return new FanOutResult(0, 0);

        var userIds = msg.Contributors.Select(c => c.UserId).ToList();

        // What is already there. On a first delivery both sets are empty and this costs two indexed
        // reads; on a replay they are exactly what makes the work a no-op.
        var existingContributions = (await db.Contributions
            .Where(c => c.PremiereId == msg.PremiereId && c.UserId != null && userIds.Contains(c.UserId.Value))
            .Select(c => c.UserId!.Value)
            .ToListAsync(ct)).ToHashSet();

        // Keyed on the movie, not the Premiere: CLAUDE.md §3 allows a user one row per movie ever, so
        // someone who already owns this film from an earlier Premiere gets the emblem but no duplicate.
        var alreadyOwn = (await db.LibraryEntries
            .Where(le => le.MovieId == msg.MovieId && userIds.Contains(le.UserId))
            .Select(le => le.UserId)
            .ToListAsync(ct)).ToHashSet();

        var contributionsAdded = 0;
        var libraryAdded = 0;

        foreach (var contributor in msg.Contributors)
        {
            if (contributor.Claps <= 0)
                continue;

            if (existingContributions.Add(contributor.UserId))
            {
                db.Contributions.Add(new Contribution
                {
                    PremiereId = msg.PremiereId,
                    UserId = contributor.UserId,
                    ClapCount = contributor.Claps,
                    EmblemTier = EmblemCalculator.Compute(
                        contributor.Claps, msg.RegisteredClapCap, _rules, isAnonymous: false)
                });
                contributionsAdded++;
            }

            if (alreadyOwn.Add(contributor.UserId))
            {
                db.LibraryEntries.Add(new LibraryEntry
                {
                    UserId = contributor.UserId,
                    MovieId = msg.MovieId,
                    PremiereId = msg.PremiereId,
                    AcquiredAt = msg.OpenedAt
                });
                libraryAdded++;
            }
        }

        // No explicit SaveChanges/commit: the outbox filter around this consumer saves and commits
        // once Consume returns, so these rows and the reveal event above land in one transaction.
        return new FanOutResult(contributionsAdded, libraryAdded);
    }
}
