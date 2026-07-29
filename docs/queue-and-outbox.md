# Queue-based unlock processing — Iteration 4

How opening a Premiere became a two-process operation, why the outbox pattern is load-bearing rather
than decorative, and what was actually verified.

Companion to [`concurrency-findings.md`](./concurrency-findings.md), which covers Iteration 2's
counting problem. This one is about the *fan-out* problem that sits immediately after it.

---

## 1. The problem

Through Iteration 3, the caller whose clap crossed the threshold did all of this inside its HTTP
request:

1. take the distributed open lock,
2. snapshot the clap counts out of Redis,
3. conditionally `UPDATE` the Premiere to `Opened`,
4. **write one `Contribution` and one `LibraryEntry` for every participant**,
5. broadcast the reveal.

Step 4 is the problem. It is `O(contributors)` — the exact quantity the product is designed to make
large. A Premiere that draws 500 people means 1,000 row inserts inside one unlucky user's clap
request, on the single request that is already the most contended moment in the system. Every other
step is bounded and small; only this one grows.

It is also the step with no reason to be synchronous. Nobody is waiting on their library page at the
instant a Premiere opens — they are watching the curtain.

## 2. The shape of the fix

```
        clap crosses threshold
                 │
   ┌─────────────▼──────────────────────────────┐
   │ Marquee.Api — ONE transaction               │
   │   UPDATE premieres SET Status=Opened …      │
   │            WHERE Status=Active              │   exactly-once guard, unchanged
   │   INSERT INTO "OutboxMessage" (PremiereOpened)│  ← the event, same transaction
   │   COMMIT                                    │
   └─────────────┬──────────────────────────────┘
                 │  (delivery service publishes committed rows)
            RabbitMQ  marquee-premiere-fanout
                 │
   ┌─────────────▼──────────────────────────────┐
   │ Marquee.Worker — ONE transaction            │
   │   InboxState dedup                          │
   │   INSERT contributions  (per participant)   │
   │   INSERT library_entries (per participant)  │
   │   INSERT "OutboxMessage" (PremiereRevealReady)│
   │   COMMIT                                    │
   └─────────────┬──────────────────────────────┘
                 │
            RabbitMQ  marquee-premiere-reveal
                 │
   ┌─────────────▼──────────────────────────────┐
   │ Marquee.Api — SignalR reveal to the group   │
   └────────────────────────────────────────────┘
```

The API's transaction is now bounded: one `UPDATE` and one `INSERT`, regardless of how many people
clapped.

## 3. Why the outbox, specifically

The naive version of "publish an event when a Premiere opens" is:

```csharp
await tx.CommitAsync();          // Premiere is now Opened
await bus.Publish(premiereOpened); // ...and if the process dies here?
```

That gap is unrecoverable. The Premiere is durably `Opened`, the exactly-once guard will never let it
open again, and the event that would have given everyone their movie was never sent. Nobody gets
anything, forever, and no retry can fix it because there is no record that anything is outstanding.

Reversing the order is no better — then a rollback can leave an event promising an open that never
happened.

The outbox removes the gap by refusing to have two commits. MassTransit's bus outbox turns
`IPublishEndpoint.Publish` into an `INSERT` on the same `DbContext` and therefore the same
transaction:

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);

var rows = await db.Premieres
    .Where(p => p.Id == meta.PremiereId && p.Status == PremiereStatus.Active)
    .ExecuteUpdateAsync(…, ct);
if (rows == 0) { await tx.RollbackAsync(ct); return false; }

await publishEndpoint.Publish(new PremiereOpened(…), ct);  // writes OutboxMessage
await db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);                                   // both, or neither
```

A separate delivery service moves committed rows to the broker. The useful consequence: **RabbitMQ
being completely down does not break an open.** The event commits to Postgres and is published
whenever the broker comes back. The queue is a delivery mechanism, not a dependency of correctness.

The same pattern applies on the worker's side, where `UseEntityFrameworkOutbox` on the receive
endpoint puts the library rows and the outgoing `PremiereRevealReady` in one transaction. The reveal
therefore cannot be announced for a fan-out that rolled back — preserving the invariant the inline
version had: nobody is told a Premiere opened before the movie is durably theirs.

## 4. Why the event carries the whole snapshot

`PremiereOpened` includes every contributor and their clap count, rather than an id the worker looks
up. Two reasons:

- **Redis is gone by then.** The API deletes the Premiere's hot keys once the open commits. An event
  that referenced them would become unprocessable the moment cleanup ran.
- **Replayability.** A self-contained event produces the same result whenever it is processed — which
  is exactly what the idempotency requirement asks for. An event that depends on mutable external
  state does not have that property.

The cost is message size: ~10k contributors serialises to a few hundred KB. Well inside RabbitMQ's
limits. If a scope ever outgrew that, the answer is to page the fan-out across several events keyed
by contributor range — not to reintroduce the Redis dependency.

## 5. Idempotency, in three overlapping layers

Reprocessing must be indistinguishable from processing once. Three mechanisms cover different cases,
deliberately overlapping:

| Layer | Catches | Does not catch |
|---|---|---|
| **The transaction** | A worker killed mid-fan-out. Either all rows are present or none are — never a partial set to reconcile. | Nothing; it is about atomicity, not repetition. |
| **The inbox** (`InboxState`) | Broker redelivery of the *same* `MessageId` — the common case after a crash or an ack that never landed. | A genuinely re-published event, which gets a fresh `MessageId`. |
| **The consumer itself** | Everything else. The insert set is derived from what is already in the database, not assumed empty. | — |

Underneath all three sit the unique constraints on `(PremiereId, UserId)` and `(UserId, MovieId)`.

### On "tolerate conflicts"

The plan says to rely on the unique constraints and make the write path tolerate conflicts. Worth
being precise about what that means on Postgres: a constraint violation **aborts the entire
transaction**. Catching it inside the consumer and retrying there cannot work — no subsequent
statement on that connection would succeed.

So the conflict handler is the *retry*, not a `try/catch`. A violation propagates, MassTransit re-runs
the consumer in a clean transaction, the re-read now sees the winner's rows, and the insert set comes
out empty. This is also why filter order on the endpoint is load-bearing:

```csharp
e.UseMessageRetry(r => r.ConfigureMarqueeRetry(options));  // registered first  → outermost
e.UseEntityFrameworkOutbox<MarqueeDbContext>(context);     // registered second → inside the retry
```

Retry outside the transaction, so each attempt gets a fresh one. The other order would deadlock the
recovery path against Postgres' aborted-transaction rule.

## 6. Retries and the dead-letter queue

`Exponential(5, 1s, 30s, 2s)` on the fan-out endpoint, all four values bound from configuration
(`Messaging:Retry`) per CLAUDE.md §7.

Not everything deserves a retry. `PermanentMessageException` marks failures that no amount of waiting
can fix — an event naming a Premiere that does not exist, for instance, which the FK would reject
forever. The retry policy `Ignore`s that type, so those messages skip the backoff entirely and go
straight to `marquee-premiere-fanout_error`. Burning five escalating waits on a message that is
structurally doomed only delays the healthy messages behind it.

## 7. What was verified

`tests/Marquee.LoadTests/queue-check.mjs`, run against the full stack. It asserts against **Postgres**,
never against what an HTTP response claimed, and exits non-zero on any failure. Checks B, C and D
publish to RabbitMQ directly in MassTransit's envelope format — replaying a message by hand is
precisely the operational scenario these guarantees exist for, and a test back door in the API would
prove nothing about the real consumer.

```
A. FAN-OUT — the worker writes the durable record off the request path
  PASS  exactly one clap reported opened=true — got 1
  PASS  premiere is Opened
  PASS  TotalClaps equals claps sent — 84 vs 84
  PASS  one contribution per contributor — 84 vs 84
  PASS  one library entry per contributor — 84 vs 84
  PASS  every registered contributor has an emblem tier — tier 1: 84

C. REPLAY — publishing the same event again changes nothing
  PASS  replay with the same MessageId leaves the record unchanged — contributions 84->84, library 84->84
  PASS  replay with a NEW MessageId leaves the record unchanged — contributions 84->84, library 84->84
  PASS  no user holds the same movie twice — 0 duplicated (user, movie)

D. DLQ — a poisoned message is dead-lettered instead of blocking the queue
  PASS  poisoned message landed in the DLQ — marquee-premiere-fanout_error: 5 -> 6
  PASS  the main queue is not blocked behind it — marquee-premiere-fanout depth=0
  PASS  a valid message is still consumed after the poison — marquee-premiere-fanout depth=0
  PASS  and it left the record intact — contributions=84, library=84

B. CRASH — kill the worker, restart it, expect no duplicates and no losses
     baseline: contributions=84, library=84
  B1. worker down while an event is outstanding
  PASS  the worker is confirmed down
  PASS  the event waits in the queue while the worker is down, not lost — depth=1
  PASS  nothing changed while the worker was down — contributions=84, library=84
  PASS  the parked event was consumed on restart — depth=0
  PASS  after restart, no contributor was lost — 84 vs 84
  PASS  after restart, no library entry was duplicated — 84 vs 84
  B2. killed with a delivery in flight, then redelivered on restart
  PASS  the redelivered message was consumed on restart — depth=0
  PASS  the redelivered message added nothing — contributions 84->84, library 84->84
  PASS  no duplicate contributions — 0 duplicated users
  PASS  no duplicate library entries — 0 duplicated (user, movie)
```

The replay check runs both flavours on purpose. Same-`MessageId` only exercises the inbox; it is the
**fresh-`MessageId`** case that proves the consumer is idempotent on its own merits, and that is the
one the acceptance criterion ("publish the same event twice manually") actually describes.

The crash check similarly splits into two, because they fail differently. **B1** (worker killed, then
an event published while it is down) is the *no losses* half — RabbitMQ holds the message and the
worker picks it up on return. **B2** (event published, worker killed ~30ms later) is the *no
duplicates* half: the consumer only acks after its transaction commits, so RabbitMQ redelivers
whatever was in flight, and the redelivery must be absorbed rather than re-applied.

### One thing that nearly produced a false result

The first version of the script read queue depth from RabbitMQ's management HTTP API. That endpoint
serves *sampled* stats, and it reported `messages=0` for a queue that `rabbitmqctl list_queues`
showed holding a message — which turned B1's "the event was parked" assertion into a spurious
failure. The script now reads depth via `rabbitmqctl`. Worth recording: when a test asserts on broker
state, the sampled dashboard number and the authoritative one are not interchangeable.

## 8. Known limitations

- **Reveal latency.** The reveal now travels API → queue → worker → queue → API instead of firing
  inline. Measured at well under a second locally; the ceiling is `Messaging:OutboxQueryDelaySeconds`
  (1s in dev) in the worst case where the delivery service's notification is missed. Accepted: the
  alternative is announcing a movie before it is durably anyone's.
- **Contributor count immediately after an open.** `GET /premieres/{id}` counts `contributions` rows
  for a terminal Premiere, so in the sub-second window before the fan-out lands it can report 0.
  Self-correcting, and the reveal broadcast carries the correct number.
- **Anonymous participants are not fanned out.** The snapshot only carries registered users, matching
  the Redis contributor set. Anonymous participation arrives in Iteration 5 (§ plan), at which point
  the contract gains an anonymous list.
- **Single consumer instance assumed.** Correctness does not depend on it — the inbox, the unique
  constraints and the retry all hold with several workers — but nothing has been load-tested with
  more than one, and the SignalR reveal still has to come back through the single API process because
  v1 runs without a Redis backplane (CLAUDE.md §6).
