using FluentAssertions;
using Marquee.Api.Services;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// Two people acting on the same friendship at the same moment.
///
/// Every mutation in FriendshipService used to read the row, branch on its status in memory, and
/// then write unconditionally — the same check-then-write shape fixed for Premieres in #19. Nothing
/// tied the check to the write, so a concurrent action could invalidate it in between:
///
///   #21  Accept and Reject racing each other both passed their own "still Pending" check and both
///        wrote. Postgres applied them in arrival order, and *both* callers were told Ok — one of
///        them about an outcome that did not survive.
///   #22  Two removals of the same friendship both loaded the row; the loser's tracked DELETE then
///        matched nothing, which EF raises as DbUpdateConcurrencyException — an unhandled 500 for
///        the entirely ordinary case of arriving second.
///
/// Each test races the pair for real across separate DI scopes, so the two calls hold separate
/// DbContexts on separate connections — the shape two concurrent HTTP requests actually have.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FriendshipRaceTests(MarqueeAppFactory factory)
{
    /// <summary>Two users with a friendship between them in the given state.</summary>
    private async Task<(Guid Requester, Guid Addressee)> PairAsync(FriendshipStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();

        // Unique per call: the collection shares one database, and Username/Email are unique.
        // Confirmed at creation (issue #29): these tests race friendship state transitions, not the
        // confirmation gate, and SendRequestAsync now refuses an unconfirmed party outright.
        var tag = Guid.NewGuid().ToString("n")[..12];
        var confirmedAt = DateTime.UtcNow;
        var requester = new User { Username = $"req_{tag}", Email = $"req_{tag}@marquee.test", PasswordHash = "x", EmailConfirmedAt = confirmedAt };
        var addressee = new User { Username = $"add_{tag}", Email = $"add_{tag}@marquee.test", PasswordHash = "x", EmailConfirmedAt = confirmedAt };

        db.Users.AddRange(requester, addressee);
        db.Friendships.Add(new Friendship
        {
            Requester = requester,
            Addressee = addressee,
            Status = status,
        });
        await db.SaveChangesAsync();

        return (requester.Id, addressee.Id);
    }

    private async Task<Guid> RequestIdAsync(Guid requesterId, Guid addresseeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        var friendship = await db.Friendships.AsNoTracking()
            .FirstAsync(f => f.RequesterId == requesterId && f.AddresseeId == addresseeId);
        return friendship.Id;
    }

    /// <summary>
    /// Each call gets its own scope, so the two race on separate DbContexts and connections. Both
    /// tasks are started before either is awaited, which is what puts them genuinely in flight
    /// together rather than one after the other.
    /// </summary>
    private async Task<FriendActionResult> InScopeAsync(Func<IFriendshipService, Task<FriendActionResult>> call)
    {
        using var scope = factory.Services.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<IFriendshipService>());
    }

    private async Task<Friendship?> StoredAsync(Guid requesterId, Guid addresseeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        return await db.Friendships.AsNoTracking().FirstOrDefaultAsync(
            f => (f.RequesterId == requesterId && f.AddresseeId == addresseeId)
                 || (f.RequesterId == addresseeId && f.AddresseeId == requesterId));
    }

    [Fact]
    public async Task Accept_and_reject_racing_the_same_request_do_not_both_report_success()
    {
        var (requester, addressee) = await PairAsync(FriendshipStatus.Pending);
        var requestId = await RequestIdAsync(requester, addressee);

        var accept = InScopeAsync(s => s.AcceptAsync(addressee, requestId, default));
        var reject = InScopeAsync(s => s.RejectAsync(addressee, requestId, default));
        var results = await Task.WhenAll(accept, reject);

        // The property that was broken: exactly one caller may be told it worked.
        results.Count(r => r.Outcome == FriendActionOutcome.Ok).Should().Be(1,
            "only one of accept and reject can survive, so only one caller may be told it succeeded");
        results.Count(r => r.Outcome == FriendActionOutcome.NotAllowed).Should().Be(1,
            "the loser must be told the request was no longer pending, not that it succeeded");

        // And whichever won must be what is actually stored — no third state, no both-applied.
        var stored = await StoredAsync(requester, addressee);
        stored.Should().NotBeNull();
        stored!.Status.Should().BeOneOf(FriendshipStatus.Accepted, FriendshipStatus.Rejected);

        // results[0] is the accept, results[1] the reject — Task.WhenAll preserves argument order.
        var expected = results[0].Outcome == FriendActionOutcome.Ok
            ? FriendshipStatus.Accepted
            : FriendshipStatus.Rejected;
        stored.Status.Should().Be(expected, "the stored outcome must be the one whose caller was told Ok");
    }

    [Fact]
    public async Task Two_removals_of_the_same_friendship_do_not_throw()
    {
        var (requester, addressee) = await PairAsync(FriendshipStatus.Accepted);

        // Before the fix the loser's tracked DELETE matched no row and EF threw, so this await was
        // what surfaced the 500. Reaching the assertions at all is half the point of the test.
        var first = InScopeAsync(s => s.RemoveFriendAsync(requester, addressee, default));
        var second = InScopeAsync(s => s.RemoveFriendAsync(addressee, requester, default));
        var results = await Task.WhenAll(first, second);

        results.Count(r => r.Outcome == FriendActionOutcome.Ok).Should().Be(1);
        results.Count(r => r.Outcome == FriendActionOutcome.RequestNotFound).Should().Be(1,
            "arriving second is not an error — the friendship the caller wanted gone is gone");

        (await StoredAsync(requester, addressee)).Should().BeNull();
    }

    [Fact]
    public async Task Both_halves_reopening_a_rejected_friendship_leave_exactly_one_pending_row()
    {
        // Adjacent to #21 and the same shape: the reopen branch of SendRequestAsync read a rejected
        // row and rewrote both participants unconditionally, so two people re-asking at the same
        // moment could each be told their own direction had been recorded.
        var (requester, addressee) = await PairAsync(FriendshipStatus.Rejected);

        var requesterName = await UsernameAsync(requester);
        var addresseeName = await UsernameAsync(addressee);

        var forward = InScopeAsync(s => s.SendRequestAsync(requester, addresseeName, default));
        var backward = InScopeAsync(s => s.SendRequestAsync(addressee, requesterName, default));
        var results = await Task.WhenAll(forward, backward);

        results.Count(r => r.Outcome == FriendActionOutcome.Ok).Should().Be(1,
            "only one direction can be recorded, so only one caller may be told it was");
        results.Count(r => r.Outcome == FriendActionOutcome.AlreadyPending).Should().Be(1);

        var stored = await StoredAsync(requester, addressee);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(FriendshipStatus.Pending);
    }

    private async Task<string> UsernameAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Username).FirstAsync();
    }
}
