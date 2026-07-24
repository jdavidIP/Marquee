# Iteration 2 — concurrency findings

> Deliverable for MARQUEE_PLAN.md Iteration 2 ("the most important iteration"), committed per
> CLAUDE.md §7. It records the naive clap counter failing under concurrent load, then the same load
> passing once counting moved to Redis. **The fix means nothing without seeing the failure**, so the
> failure is measured first.

## How this was produced

- Load script: [`tests/Marquee.LoadTests/clap-storm.mjs`](../tests/Marquee.LoadTests/clap-storm.mjs)
  (a zero-dependency parallel-request driver; a k6 equivalent is `clap-storm.js`). It registers 300
  users, then drives four scenarios against freshly created Premieres, hitting only the public
  `POST /api/premieres/{id}/clap` endpoint — no test hooks.
- Environment: single API instance, Postgres 16 and Redis 7 via `docker-compose.yml`, clean database
  per run. Thresholds differ slightly between runs because they are randomised at creation (§4.1);
  what matters is each run's counts against *its own* threshold.
- Authoritative numbers are read straight from Postgres (and Redis for a still-active Premiere),
  not from what the clap responses claimed.

The scenarios:

| # | Name | What it does | What it exposes |
|---|---|---|---|
| 1 | Lost updates | 300 users, ~threshold−10 concurrent single claps (stays below threshold) | Whether the shared counter loses increments |
| 2 | Double open | 300 concurrent single claps, overshooting the threshold | Whether the open fires exactly once |
| 3 | Contended open | Ramp accurately to threshold−1, then burst 25 claps across the boundary | Whether the open + fan-out survive contention at the exact crossing |
| 4 | Cap enforcement | One user fires 5× their cap concurrently | Whether a participant can exceed their cap |

---

## Part A — the naive counter, broken

The Iteration 1 clap path (`git show` the pre-Iteration-2 `PremiereService.ClapAsync`) did a
read-modify-write on a single Postgres row:

```csharp
var count = premiere.TotalClaps;   // read
count += 1;                        // modify
premiere.TotalClaps = count;       // write
// ... then, on the same request thread:
if (premiere.TotalClaps >= premiere.Threshold && premiere.Status == Active)
    await OpenAsync(premiere);     // non-atomic check, per-request DbContext
```

Every concurrent request reads the same stale `TotalClaps`, so writes clobber each other; and the
open check runs independently on each request with its own `DbContext`, so nothing makes it
happen exactly once.

### Observed (naive run)

Authoritative Postgres state after the storm:

| Scenario | Claps accepted (HTTP 200) | Contribution rows | Authoritative `TotalClaps` | Status | Library entries | Emblems | HTTP 500 | `opened=true` responses |
|---|---|---|---|---|---|---|---|---|
| 1 — Lost updates (threshold 148) | 138 | 138 | **1** | Active | 0 | 0 | 0 | 0 |
| 2 — Double open (threshold 165) | 300 | 300 | **6** | **Active** | 0 | 0 | 0 | **0** |
| 3 — Contended open (threshold 157) | 181 | 181 | 160 | Opened | **159** | 159 | **24** | 1 |

What each row means:

- **Scenario 1 — catastrophic lost updates.** 138 claps were accepted and every *per-user*
  `Contribution` row is correct (each user has its own row, so those writes don't contend). But the
  single shared `TotalClaps` row landed on **1** — **137 of 138 increments (99.3%) were lost.**
- **Scenario 2 — the open never fires *at all*.** 300 real claps against a threshold of 165, yet the
  counter reached only **6**, so the threshold was never observed as crossed. The Premiere stayed
  `Active`, produced **zero** library entries, and **nobody received the movie.** The lost-update bug
  is so severe it *masks* the double-open by never getting near the threshold.
- **Scenario 3 — the open is not exactly-once.** Ramped accurately to threshold−1, then 25 concurrent
  claps crossed the line. All 25 entered the open path (each saw `Status == Active` in its own
  `DbContext`); one committed, **24 collided on the unique `LibraryEntry (UserId, MovieId)` index and
  returned HTTP 500.** The winning open only saw a partial snapshot: **181 people contributed but only
  159 got the movie — 22 contributors were silently left out**, and the persisted count (160) matches
  neither the threshold nor the real clap total.

Two more qualitative failures showed up:

- **Connection exhaustion.** Under the 300-way storm the API held long read-modify-write
  transactions open, saturating the Postgres connection pool (`FATAL: sorry, too many clients
  already`) — even a read-only verification query couldn't get a connection until the API was stopped.
- **No back-pressure.** Because the counter never converged, there was no point at which the system
  said "full" — it just silently dropped work.

---

## Part B — the fix

Counting moved off Postgres onto Redis, which is atomic by construction. Postgres remains the durable
record, written **once** at open time.

- **Atomic counting.** A single Lua script
  ([`RedisClapCounters.ClapScript`](../src/Marquee.Infrastructure/Redis/RedisClapCounters.cs)) does
  the cap check, the per-user `INCR`, the total `INCR`, and the contributors `SADD` as one indivisible
  step. No read-modify-write, so no lost updates and no way to exceed a cap.
- **Exactly-once open, triple-guarded:**
  1. the caller whose `INCR` return value **equals** the threshold is the single trigger (INCR hands
     each caller a unique post-increment value);
  2. a **distributed lock** (`SET NX PX`) serialises the open work;
  3. a **DB conditional update** (`UPDATE ... WHERE Status = 'Active'`, via EF `ExecuteUpdate`) is the
     authoritative guard — if a lock ever expired and a second caller slipped in, `0 rows affected`
     stops the double-open. The status flip and the library/emblem fan-out are one transaction, so an
     open is all-or-nothing (no more partial fan-outs).
- **Accepted ⇔ granted.** Before snapshotting, the opener sets an atomic Redis *closed* flag that the
  clap script checks first; claps arriving after the cutoff are rejected (`409`) instead of being
  counted-but-never-granted. Every accepted clap is inside the snapshot that gets fanned out.
- **Hot path never touches Postgres to count.** Premiere rules (threshold, caps, status) are cached in
  Redis ([`RedisPremiereCache`](../src/Marquee.Infrastructure/Redis/RedisPremiereCache.cs)); a clap
  reads the cache and the counters, and only the single opening request writes to Postgres.

### Observed (fixed run)

| Scenario | Claps accepted | Authoritative final count | Status | Contributions | Library | Emblems | HTTP 500 | `opened=true` | Cap respected |
|---|---|---|---|---|---|---|---|---|---|
| 1 — Lost updates (threshold 160) | 150 | **150** (Redis, still Active) | Active | — | — | — | 0 | 0 | — |
| 2 — Double open (threshold 164) | 166 | **166** (Postgres) | Opened | 166 | 166 | 166 | 0 | **1** | — |
| 3 — Contended open (threshold 139) | 163 | **163** (Postgres) | Opened | 163 | 163 | 163 | **0** | **1** | — |
| 4 — Cap enforcement (cap 6) | 30 responses | user counter **6** | Active | — | — | — | 0 | 0 | **max 6, never exceeded** |

- **Scenario 1 — no lost updates.** 150 accepted, Redis counter **exactly 150**. (Postgres
  `TotalClaps` is 0 because it is only written at open — the live count for an active Premiere is the
  Redis counter, which is authoritative and exact.)
- **Scenario 2 — opens exactly once, and accepted ⇔ granted.** Of 300 concurrent claps, 166 were
  accepted before the cutoff and **134 were correctly rejected (409)** once opening began; **one**
  `opened=true`, **zero** 500s. Postgres shows `TotalClaps = Contributions = Library = Emblems = 166`.
- **Scenario 3 — exactly-once under contention.** The 25-clap burst across the boundary produced
  **one** open and **zero** 500s (naive: 24). `TotalClaps = Contributions = Library = Emblems = 163`,
  fully consistent — no missing contributors.
- **Scenario 4 — cap holds under concurrency.** One user fired 30 claps at once against a cap of 6;
  the atomic Lua cap check let exactly 6 through and reported the rest as `capReached`. The user's
  Redis counter is **6** — never 7, never more.

---

## Acceptance criteria

| Criterion (MARQUEE_PLAN.md) | Result |
|---|---|
| Re-run the load script: final count matches claps sent exactly | ✅ 150 sent → 150 counted (S1); opened Premieres show `TotalClaps = Contributions = Library` exactly (S2/S3) |
| The open event fires exactly once under concurrent load | ✅ Exactly one `opened=true`, zero 500s, in both the overshoot (S2) and the exact-boundary burst (S3) |
| No participant can exceed their cap, even with concurrent requests | ✅ 30 concurrent claps from one user, counter capped at exactly 6 (S4) |
| Findings document committed | ✅ This file |

## Known limitations (deliberately deferred)

- **Opener crash between the INCR trigger and the DB commit.** Because only the exact-threshold caller
  triggers the open, if it dies before committing, no later caller re-triggers. This is closed by the
  60-minute **expiry auto-open** (§4.5), which arrives with the scheduler in **Iteration 3**.
- **Fan-out is exactly-once but not yet idempotent-by-replay.** It runs inline in the open
  transaction. Moving it behind RabbitMQ with the outbox pattern and idempotent consumers is
  **Iteration 4**; the unique constraints and the "skip if already owned" check are the groundwork.
- **Anonymous participants** are not counted here (the clap endpoint is authenticated). Anonymous
  sessions with their own caps arrive in **Iteration 5**; the Redis keys are already namespaced for it.
