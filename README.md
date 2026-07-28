# Marquee

A social movie-discovery app built around a synchronous, collaborative unlock mechanic. Four
times a day a **Premiere** appears containing a hidden movie; it opens only when enough people
**clap** for it before its 60-minute timer runs out. See [`CLAUDE.md`](./CLAUDE.md) for the full
domain spec and [`MARQUEE_PLAN.md`](./MARQUEE_PLAN.md) for the iteration-by-iteration build plan.

> **Status: Iteration 3 complete** — Premieres now run themselves. A Quartz scheduler draws the day's
> four Premiere times, activates each at its moment, and auto-opens any that run out their 60 minutes;
> SignalR pushes throttled clap counts and the reveal to everyone watching. Iteration 2's concurrency
> work (atomic Redis counting, exactly-once open) is recorded in
> [`docs/concurrency-findings.md`](./docs/concurrency-findings.md).

## Architecture

```
src/
  Marquee.Domain/          Entities, enums, and the pure §4 formulas (threshold, cap, emblem, schedule)
  Marquee.Infrastructure/  EF Core + Postgres, Redis clap counters, TMDB client, DI wiring
  Marquee.Api/             ASP.NET Core Web API — JWT auth, premieres, clap, library,
                           SignalR hub + broadcast loop, Quartz scheduler jobs
  Marquee.Worker/          Background service (used from iteration 4)
  Marquee.Web/             Angular 20 SPA (standalone components + signals)
tests/
  Marquee.UnitTests/       xUnit tests for the domain formulas (§4 worked examples)
  Marquee.IntegrationTests/ Testcontainers-based tests (fleshed out in iteration 6)
  Marquee.LoadTests/       clap-storm load scripts (Node + k6) and the iteration 3 realtime check
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

The threshold, cap, emblem, and daily-schedule formulas live in `Marquee.Domain` as pure,
dependency-free functions and are unit-tested without a database, Redis, or HTTP.

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
- Docker Desktop (for Postgres and Redis)
- `dotnet-ef` global tool: `dotnet tool install --global dotnet-ef --version 9.*`

## Running it

**1. Start Postgres + Redis**

```bash
docker compose up -d
```

**2. Run the API** (applies EF migrations and seeds an admin on startup)

```bash
cd src/Marquee.Api
dotnet run
# listens on http://localhost:5080 (the default 'http' launch profile), which the SPA targets
```

The seeded admin (dev only) is `admin` / `admin12345` — override via the `Admin:*` config keys.

**3. Run the Angular app**

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

**Without a key**, the app falls back to `StubTmdbClient` — a small fixed pool of real films — so the
whole flow (including premiere creation) runs offline. The stub logs a warning and is not for
production. See [`.env.example`](./.env.example).

## Testing

```bash
dotnet test tests/Marquee.UnitTests            # domain formula + schedule tests

# API + docker infra must be running for both of these
cd tests/Marquee.LoadTests
node clap-storm.mjs        # iteration 2 — concurrency: lost updates, double open, cap enforcement
node realtime-check.mjs    # iteration 3 — two watchers, throttling, reveal, timer auto-open
```

`realtime-check.mjs` exits non-zero if any check fails, and includes a ~1 minute wait while it proves
a Premiere auto-opens on its timer. Set `SKIP_AUTOOPEN=1` to skip that part.

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

## Key endpoints

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/api/auth/register` | – | Create account, returns JWT |
| POST | `/api/auth/login` | – | Log in, returns JWT |
| GET | `/api/auth/me` | user | Current user |
| POST | `/api/premieres` | admin | Create + activate a Premiere on demand |
| GET | `/api/premieres/active` | optional | The live Premiere (movie hidden until open) |
| GET | `/api/premieres/next` | optional | The next Premiere the scheduler has lined up |
| GET | `/api/premieres/{id}` | optional | One Premiere (initial load; counts then arrive over the hub) |
| POST | `/api/premieres/{id}/clap` | user | Clap; opens synchronously on threshold |
| GET | `/api/library` | user | The signed-in user's movies |

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
