# Marquee

A social movie-discovery app built around a synchronous, collaborative unlock mechanic. Four
times a day a **Premiere** appears containing a hidden movie; it opens only when enough people
**clap** for it before its 60-minute timer runs out. See [`CLAUDE.md`](./CLAUDE.md) for the full
domain spec and [`MARQUEE_PLAN.md`](./MARQUEE_PLAN.md) for the iteration-by-iteration build plan.

> **Status: Iteration 2 complete** — clap counting has moved to atomic Redis `INCR` with an
> exactly-once open (distributed lock + DB conditional update). The naive Postgres read-modify-write
> counter was first broken under load on purpose; see
> [`docs/concurrency-findings.md`](./docs/concurrency-findings.md) for the before/after numbers and
> [`tests/Marquee.LoadTests`](./tests/Marquee.LoadTests) for the load script.

## Architecture

```
src/
  Marquee.Domain/          Entities, enums, and the pure §4 formulas (threshold, cap, emblem)
  Marquee.Infrastructure/  EF Core + Postgres, Redis clap counters, TMDB client, DI wiring
  Marquee.Api/             ASP.NET Core Web API — JWT auth, premieres, clap, library
  Marquee.Worker/          Background service (used from iteration 4)
  Marquee.Web/             Angular 20 SPA (standalone components + signals)
tests/
  Marquee.UnitTests/       xUnit tests for the domain formulas (§4 worked examples)
  Marquee.IntegrationTests/ Testcontainers-based tests (fleshed out in iteration 6)
  Marquee.LoadTests/       clap-storm load scripts (Node + k6) for the Iteration 2 concurrency work
```

Clap counting is the hot path and lives in Redis: an atomic Lua script does the cap check plus the
per-user and total `INCR` in one step, and the open fires exactly once behind a distributed lock and
a DB conditional update. Postgres is the durable record, written once when a Premiere opens.

The threshold, cap, and emblem formulas live in `Marquee.Domain` as pure, dependency-free functions
and are unit-tested without a database, Redis, or HTTP.

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
dotnet test tests/Marquee.UnitTests            # domain formula tests

# concurrency load test (API + docker infra must be running)
cd tests/Marquee.LoadTests && node clap-storm.mjs
```

## Iteration 1 acceptance criteria — met

- A user can register, log in, clap, and see the movie land in their library ✔
- Domain formula unit tests pass, including the small-user-base edge case ✔
- An admin can manually create a Premiere (no scheduler yet — iteration 3) ✔

## Iteration 2 acceptance criteria — met

- Re-run the load script: final count matches claps sent exactly (no lost updates) ✔
- The open event fires exactly once under concurrent load, no duplicate fan-out ✔
- No participant can exceed their cap, even under concurrent requests ✔
- Findings document committed ([`docs/concurrency-findings.md`](./docs/concurrency-findings.md)) ✔

## Key endpoints

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/api/auth/register` | – | Create account, returns JWT |
| POST | `/api/auth/login` | – | Log in, returns JWT |
| GET | `/api/auth/me` | user | Current user |
| POST | `/api/premieres` | admin | Create + activate a Premiere |
| GET | `/api/premieres/active` | optional | The live Premiere (movie hidden until open) |
| GET | `/api/premieres/{id}` | optional | One Premiere (polled for the count) |
| POST | `/api/premieres/{id}/clap` | user | Clap; opens synchronously on threshold |
| GET | `/api/library` | user | The signed-in user's movies |
