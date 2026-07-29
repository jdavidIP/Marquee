# Marquee

A social movie-discovery app built around a synchronous, collaborative unlock mechanic. Four
times a day a **Premiere** appears containing a hidden movie; it opens only when enough people
**clap** for it before its 60-minute timer runs out. See [`CLAUDE.md`](./CLAUDE.md) for the full
domain spec and [`MARQUEE_PLAN.md`](./MARQUEE_PLAN.md) for the iteration-by-iteration build plan.

> **Status: Iteration 5 complete** — the app is now survivable in public and has its social layer.
> Claps are guarded by a per-participant cap, a debounce, idempotency keys, and a sliding-window rate
> limiter that partitions per caller; visitors can clap under a short-lived signed anonymous session
> without ever becoming a user. Authorisation is permission-based rather than a blanket admin flag,
> and blocking bites on every request rather than only at login. Friendships, viewer-shaped profiles,
> and a per-request Redis `SINTER` for "which of my friends clapped" complete the social layer.
> Design notes in [`docs/security-and-social.md`](./docs/security-and-social.md);
> the queue work in [`docs/queue-and-outbox.md`](./docs/queue-and-outbox.md);
> Iteration 2's concurrency work in [`docs/concurrency-findings.md`](./docs/concurrency-findings.md).

## Architecture

```
src/
  Marquee.Domain/          Entities, enums, and the pure §4 formulas (threshold, cap, emblem, schedule)
  Marquee.Infrastructure/  EF Core + Postgres, Redis clap counters, TMDB client, message contracts,
                           MassTransit/RabbitMQ wiring, DI
  Marquee.Api/             ASP.NET Core Web API — JWT auth, premieres, clap, library,
                           SignalR hub + broadcast loop, Quartz scheduler jobs, outbox publisher
  Marquee.Worker/          Queue consumer — the open-time fan-out (contributions, emblems, library)
  Marquee.Web/             Angular 20 SPA (standalone components + signals)
tests/
  Marquee.UnitTests/       xUnit tests for the domain formulas (§4 worked examples)
  Marquee.IntegrationTests/ Testcontainers-based tests (fleshed out in iteration 6)
  Marquee.LoadTests/       clap-storm load scripts (Node + k6), the realtime check, the queue check
```

Clap counting is the hot path and lives in Redis: an atomic Lua script does the cap check plus the
per-user and total `INCR` in one step, and the open fires exactly once behind a distributed lock and
a DB conditional update. Postgres is the durable record, written once when a Premiere opens.

Real-time updates are **batched, not per-clap**: a clap marks its Premiere dirty and returns, and a
background loop flushes at most one message per Premiere per interval (250ms by default), reading the
current count straight from Redis. The outbound message rate is therefore independent of the clap
rate. The reveal is the exception — it is sent immediately, once, by whichever caller won the
exactly-once open. SignalR group names are derived from `scopeId` + `premiereId`, never a single
hardcoded global broadcast (CLAUDE.md §5).

Opening a Premiere is split across two processes. The API does the small, bounded part — the
exactly-once status change and the final count — and publishes a `PremiereOpened` event carrying the
clap snapshot; `Marquee.Worker` does the part that grows with the audience: a `Contribution` and a
`LibraryEntry` per participant. The status change and the event are written **in one transaction**
(the outbox pattern), so a crash between them cannot lose the event, and RabbitMQ being down delays
the reveal without breaking the open. Full reasoning in
[`docs/queue-and-outbox.md`](./docs/queue-and-outbox.md).

The threshold, cap, emblem, and daily-schedule formulas live in `Marquee.Domain` as pure,
dependency-free functions and are unit-tested without a database, Redis, or HTTP.

### Queue: RabbitMQ + MassTransit 8

MassTransit is pinned to the **8.x** line on purpose: version 9 moved to a commercial-gated licence,
while 8.x is Apache-2.0. The outbox and inbox APIs used here are identical across both, so this is a
licensing choice rather than a capability one.

### Scheduling: Quartz.NET

The work is time-triggered ("generate tomorrow's schedule at 00:05", "check every 20s for a Premiere
that is due"), not queued, and Quartz models exactly that — cron triggers, misfire policies, and
`DisallowConcurrentExecution` out of the box. Hangfire is fundamentally a background *job queue* with
a persistent store; most of what it adds here is queueing, which is iteration 4's job and belongs to
RabbitMQ/MassTransit. Quartz also runs in memory by default, which suits a single instance and keeps
the schedule out of the application database.

The trade-off: an in-memory scheduler forgets its triggers on restart. That is fine because no job
carries state — the day's Premieres are rows in Postgres, and every job re-derives what to do from
those rows and is idempotent, so a restart simply picks up on the next tick.

## Prerequisites

- .NET 9 SDK
- Node 20+/24+ (Angular CLI 20)
- Docker Desktop (for Postgres, Redis and RabbitMQ)
- `dotnet-ef` global tool: `dotnet tool install --global dotnet-ef --version 9.*`

## Running it

**1. Start Postgres + Redis + RabbitMQ**

```bash
docker compose up -d
# RabbitMQ management UI: http://localhost:15672  (marquee / marquee)
```

**2. Run the API** (applies EF migrations and seeds an admin on startup)

```bash
cd src/Marquee.Api
dotnet run
# listens on http://localhost:5080 (the default 'http' launch profile), which the SPA targets
```

The seeded admin (dev only) is `admin` / `admin12345` — override via the `Admin:*` config keys.

**3. Run the worker** — without it, Premieres still open but nobody's library is filled

```bash
cd src/Marquee.Worker
dotnet run
```

The API owns the schema, so start it at least once before the worker. Events published while the
worker is down wait in the queue and are processed when it returns.

**4. Run the Angular app**

```bash
cd src/Marquee.Web
npx ng serve
# http://localhost:4200
```

### TMDB

Movie selection uses TMDB `/discover/movie` with the §4.6 filters. Set a v3 API key to use the real
service:

```
# environment variable (double-underscore binds to config)
Tmdb__ApiKey=your-tmdb-v3-key
```

**Without a key**, the app falls back to `StubTmdbClient` — a fixed pool of 12 real films — so the
whole flow (including premiere creation) runs offline. The stub logs a warning and is not for
production. See [`.env.example`](./.env.example).

Because §4.6 forbids a movie ever repeating, the stub pool is also a hard ceiling of **12 Premieres
ever** for a given database. That is plenty for a demo but runs out quickly under repeated load
testing; set a real `Tmdb__ApiKey`, or clear `premieres`/`movies` in the dev database, before a long
test session.

## Testing

```bash
dotnet test tests/Marquee.UnitTests            # domain formula + schedule tests

# API + docker infra must be running for all of these
cd tests/Marquee.LoadTests
node clap-storm.mjs        # iteration 2 — concurrency: lost updates, double open, cap enforcement
node realtime-check.mjs    # iteration 3 — two watchers, throttling, reveal, timer auto-open
node queue-check.mjs       # iteration 4 — fan-out, crash recovery, replay, dead-lettering
node security-check.mjs    # iteration 5 — throttling, privacy, friend intersection, admin 403s
```

`realtime-check.mjs` exits non-zero if any check fails, and includes a ~1 minute wait while it proves
a Premiere auto-opens on its timer. Set `SKIP_AUTOOPEN=1` to skip that part.

`queue-check.mjs` needs the worker running too, and its crash check deliberately kills the worker —
it waits for something to restart it. Set `SKIP_CRASH=1` to skip that part.

## Iteration 1 acceptance criteria — met

- A user can register, log in, clap, and see the movie land in their library ✔
- Domain formula unit tests pass, including the small-user-base edge case ✔
- An admin can manually create a Premiere (no scheduler yet — iteration 3) ✔

## Iteration 2 acceptance criteria — met

- Re-run the load script: final count matches claps sent exactly (no lost updates) ✔
- The open event fires exactly once under concurrent load, no duplicate fan-out ✔
- No participant can exceed their cap, even under concurrent requests ✔
- Findings document committed ([`docs/concurrency-findings.md`](./docs/concurrency-findings.md)) ✔

## Iteration 3 acceptance criteria — met

- Two browsers watching the same Premiere see the count move in near-real-time ✔
- A Premiere that hits its timer auto-opens and reveals correctly (`AutoOpened`, §4.5) ✔
- 4 Premieres generate daily within §4.4, with the 2-hour minimum gap respected ✔

## Iteration 4 acceptance criteria — met

- Kill the worker mid-processing and restart it: no duplicate library entries, no lost contributors ✔
- Publish the same event twice manually: the outcome is identical to publishing once ✔
- A deliberately poisoned message lands in the DLQ instead of blocking the queue ✔

Verified by `tests/Marquee.LoadTests/queue-check.mjs` against a running stack; see
[`docs/queue-and-outbox.md`](./docs/queue-and-outbox.md) for the recorded run.

## Iteration 5 acceptance criteria — met

- A script hammering the clap endpoint is throttled without degrading service for normal users ✔
- A stranger requesting a private profile receives only username and bio, and the private user still
  appears in search ✔
- Friend intersection is computed per request, not broadcast ✔
- Non-admin requests to admin endpoints return 403 ✔

Verified by `tests/Marquee.LoadTests/security-check.mjs` against a running stack; see
[`docs/security-and-social.md`](./docs/security-and-social.md) for the recorded run and the
reasoning behind the guard ordering.

## Key endpoints

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/api/auth/register` | – | Create account, returns JWT |
| POST | `/api/auth/login` | – | Log in, returns JWT |
| GET | `/api/auth/me` | user | Current user |
| POST | `/api/sessions/anonymous` | – | Issue a short-lived anonymous session so a visitor can clap |
| POST | `/api/premieres` | `CanManagePremieres` | Create + activate a Premiere on demand |
| GET | `/api/premieres/active` | optional | The live Premiere (movie hidden until open) |
| GET | `/api/premieres/next` | optional | The next Premiere the scheduler has lined up |
| GET | `/api/premieres/{id}` | optional | One Premiere (initial load; counts then arrive over the hub) |
| POST | `/api/premieres/{id}/clap` | user *or* anonymous session | Clap; the threshold-crossing clap opens the Premiere and queues the fan-out. Accepts `Idempotency-Key` |
| GET | `/api/premieres/{id}/friends` | user | Which of your friends clapped — per viewer, never broadcast |
| GET | `/api/library` | user | The signed-in user's movies |
| GET | `/api/users?query=` | optional | Search users by username prefix; private accounts included |
| GET | `/api/users/{username}` | optional | Profile, shaped by viewer (see below) |
| PATCH | `/api/users/me` | user | Update your own bio / privacy |
| GET | `/api/friends` | user | Accepted friends |
| GET | `/api/friends/requests` | user | Pending requests, both directions |
| POST | `/api/friends/requests` | user | Send a friend request by username |
| POST | `/api/friends/requests/{id}/accept` | user | Accept |
| POST | `/api/friends/requests/{id}/reject` | user | Reject |
| DELETE | `/api/friends/{userId}` | user | Unfriend |
| GET | `/api/admin/users` | `CanViewUsers` | List / search users |
| POST | `/api/admin/users/{id}/block` · `/unblock` | `CanBlockUsers` | Block or unblock an account |
| GET | `/api/admin/premieres` | `CanManagePremieres` | All Premieres, movie visible before the reveal |
| PATCH | `/api/admin/premieres/{id}/schedule` | `CanManagePremieres` | Change a Scheduled Premiere's time |
| POST | `/api/admin/premieres/{id}/movie` | `CanManagePremieres` | Regenerate the hidden movie |
| POST | `/api/admin/premieres/{id}/activate` | `CanManagePremieres` | Start a Scheduled Premiere now |

### Security model in one paragraph

Authorisation is **permission-based**: endpoints require a capability (`premieres:manage`,
`users:view`, `users:block`) carried as a JWT claim, mapped from the role at login, so permissions
can diverge from roles without touching call sites. Blocking is the exception that cannot ride in a
token — it is checked on every authenticated request against a short-TTL Redis cache, because a JWT
issued before the block stays valid otherwise. Clapping is guarded by four independent mechanisms in
a specific order (rate limiter → block check → idempotency → debounce → cap), and a profile a
stranger is not entitled to see returns a genuinely smaller payload rather than one with nulls. Full
reasoning in [`docs/security-and-social.md`](./docs/security-and-social.md).

### Real-time hub

`/hubs/premieres` — anonymous connections allowed (watching is public). The token, when there is one,
is passed as an `access_token` query parameter because WebSockets cannot carry an `Authorization`
header.

| Direction | Name | Payload |
|---|---|---|
| client → server | `JoinScope` / `LeaveScope` | `scopeId` |
| client → server | `JoinPremiere` / `LeavePremiere` | `scopeId`, `premiereId` |
| server → client | `clapUpdate` | batched count + threshold + contributor count |
| server → client | `premiereOpened` | final counts and the revealed movie |
| server → client | `premiereActivated` | a new Premiere just went live in this scope |

Only public aggregates are ever broadcast. Per-viewer data (your own clap count) stays on the request
path — nothing personalised goes to a group.
