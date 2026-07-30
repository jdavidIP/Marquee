# Observability, resilience, load — Iteration 6

What Iteration 6 added, why each piece is shaped the way it is, and the evidence for the three
acceptance criteria in `MARQUEE_PLAN.md`.

The short version: a clap's journey is now readable end to end two different ways, the system says
out loud when a dependency is down, TMDB failing costs a scheduling attempt rather than a Premiere,
and the whole thing has been driven under a burst and checked against Postgres afterwards.

---

## 1. Two identifiers, not one

Both a **correlation id** and an OpenTelemetry **trace id** follow a clap across the queue. That
looks redundant until you ask what each survives.

| | Correlation id | Trace id |
|---|---|---|
| Created by | The caller, or the API if none was sent | The tracing stack |
| Survives sampling | Yes | No — a dropped trace refers to nothing |
| Survives the collector being down | Yes | No |
| Callable from outside | Yes — a client, a load test, or a support ticket can name one | No |
| Joins spans into a tree | No | Yes |

The correlation id is the one a human can hold on to. `X-Correlation-Id` is accepted from the caller
when supplied, generated otherwise, and always echoed on the response — so a user reporting "my clap
didn't register" can quote a string that appears verbatim in both services' logs.

Its ambient plumbing (`CorrelationIdContext`) is an `AsyncLocal` rather than a parameter because the
id has to reach the point where a message is published, and that sits several layers below the code
that knows it: controller → service → opener. Threading a string through all of those would put an
observability concern into the signature of every one of them.

Three sources of an id:

- **HTTP** — from the caller, or generated.
- **Quartz** — `job-{name}-{fireInstanceId}`. Without this the most interesting journey in the system
  would be the untraceable one: a Premiere auto-opened on its timer (§4.5) publishes with no ambient
  id, so the worker would log an id matching nothing on the API side.
- **Consumed messages** — read from the message header, falling back to a fresh id so a dead-letter
  replay is still labelled.

Client-supplied ids are filtered to printable ASCII and length-capped before they reach a log. An id
containing a newline would let a caller forge whole log lines — claiming an error that never
happened, or burying a real one under plausible noise.

### Verifying it crosses the queue

This was genuinely uncertain rather than obvious. The API publishes through MassTransit's **bus
outbox**, which does not hand the message to RabbitMQ at `Publish()` time: it serialises it into an
`OutboxMessage` row inside the open transaction, and a delivery service sends it later, on a thread
with no ambient correlation id. Whether a publish filter ran early enough for the header to be
captured had to be tested, not assumed.

It does. One auto-opened Premiere produced correlation id
`job-premiere-tick-639209656245257592` on all three of:

```
[19:49:54 INF] [api]    [job-premiere-tick-639209656245257592/] Marquee.Api.Services.PremiereOpener
    Premiere 8c91abdd… opened (AutoOpened) … fan-out queued.
[19:49:55 INF] [worker] [job-premiere-tick-639209656245257592/] …PremiereOpenedConsumer
    Fanned out Premiere 8c91abdd…: 1 contributions … 1 library entries written.
[19:49:56 INF] [api]    [job-premiere-tick-639209656245257592/] …PremiereRevealReadyConsumer
    Revealed Premiere 8c91abdd… to scope global.
```

API → queue → worker → queue → API. Both directions.

---

## 2. Tracing

OpenTelemetry in both processes, OTLP to a Jaeger all-in-one container added to `docker-compose.yml`.
Jaeger speaks OTLP natively, so local work needs no separate collector.

Almost none of it is hand-written spans. ASP.NET Core opens the request span, StackExchange.Redis and
EF Core add theirs beneath it, and MassTransit emits spans *and* propagates W3C trace context in
message headers — which is what joins the worker's spans to the API's rather than leaving two
unrelated traces.

The source list is shared between hosts rather than configured per host. If one listened to a source
the other did not, a journey would stop at the queue and look like the worker never ran.

A verified trace, 30 spans across both services:

```
api :: outbox send
  api :: outbox process
    api :: PremiereOpened send
      worker :: marquee-premiere-fanout receive
        [16 x db span]
        worker :: marquee-premiere-fanout process
          [3 x db span]
          worker :: outbox send
            worker :: outbox process
              worker :: PremiereRevealReady send
                api :: marquee-premiere-reveal receive
                  api :: marquee-premiere-reveal process
```

### Known limitation: the outbox is where the trace starts

That tree is rooted at `outbox send`, **not** at the request or scheduled job that caused the open.
The outbox commits the message inside the open transaction and delivers it later from its own thread,
so the delivery begins a new trace and the causal origin sits in a separate one.

This is the concrete reason the project keeps both identifiers. The trace gives the cross-service
span tree; the correlation id bridges the gap the outbox creates. Stitching the two traces would mean
capturing `traceparent` into the outbox row and re-attaching it as a span **link** at delivery — span
links exist for exactly this asynchronous-boundary case. Not done here, and recorded rather than
quietly left as a surprise.

### Sampling

Parent-based. A standalone ratio sampler re-rolls per process and would record one half of a journey
and drop the other — the single thing this iteration must not do.

### What is deliberately not recorded

EF Core instrumentation is left on defaults. The knob for including SQL **parameter values** is
compiled out as experimental in the shipped beta and defaults to off, which is what Marquee wants
anyway: parameters are user data — an email being looked up, a username being checked — and a trace
store is not the place for it. Query *text* is recorded, which is what makes a database span identify
itself.

---

## 3. Health checks

Two endpoints, because liveness and readiness answer different questions.

| | Checks | Meaning of a failure |
|---|---|---|
| `/health/live` | nothing | This process should be restarted |
| `/health/ready` | Postgres, Redis, MassTransit bus | Do not send this instance traffic |

Conflating them is actively harmful. A liveness probe that consulted Postgres would make a database
blip look like a dead API container, and an orchestrator would respond by killing and restarting
containers that were perfectly fine — turning a recoverable dependency outage into a crash loop.

RabbitMQ has no probe of its own: MassTransit registers a bus health check, and "the bus is connected
and its endpoints are ready" is a stricter statement than the broker answering a ping.

The body names each dependency and its state rather than returning the default single word
`Healthy` — during an incident the whole value of the endpoint is being told *which* dependency is
down without going to the logs. Exception detail is deliberately omitted: the endpoint is
unauthenticated and reachable from further away than the logs are, and connection strings and
internal hostnames turn up in exception messages.

Verified including the failure case. With Redis stopped:

```
GET /health/ready  → 503   redis Unhealthy, postgres Healthy, masstransit-bus Healthy
GET /health/live   → 200   (throughout)
```

**Only the API exposes these.** The worker owns no HTTP surface by design; adding one purely to be
probed would trade that property for a signal its queue consumption already provides. Its health is
visible as an idle consumer on the fan-out queue and in the dashboard's queue depth.

---

## 4. TMDB resilience

A Polly v8 pipeline on the TMDB typed client: total timeout → retry (exponential, jittered) →
circuit breaker → per-attempt timeout.

- **Retry outside the breaker.** Retry rides out the blip one request hit; the breaker notices when
  retrying has stopped being worth it and stops feeding calls into a service that is plainly down.
- **Jitter matters here specifically.** The daily generation job creates several Premieres in a loop;
  without jitter their retries line up and hit TMDB in synchronised waves.
- **`HttpClient.Timeout` is infinite** and the pipeline owns timeouts. Left at its default it cancels
  an attempt with a `TaskCanceledException` the retry strategy does not classify as transient, so a
  merely *slow* TMDB would fail outright instead of being retried.
- **The breaker needs a minimum throughput** before its failure ratio means anything. One failure out
  of one call is a 100% failure rate; without that floor the breaker trips on the first blip.

None of this is exercised in normal development — no API key means the offline stub — so three unit
tests drive the real client over a scripted transport: two 503s retried then succeeding, a
persistently dead TMDB yielding "no movie" rather than an escaping exception, and a 401 attempted
exactly once, because retrying a bad API key cannot fix it and only invites a rate-limit ban.

---

## 5. Integration tests

`Marquee.IntegrationTests` runs the real API in-process via `WebApplicationFactory` against a real
Postgres and Redis from Testcontainers.

Real servers are the point. Marquee's clap path is correct because of two things that exist only
inside them: a Lua script making the cap check and both increments one atomic step, and a conditional
`UPDATE` letting Postgres arbitrate a double open. An in-memory EF provider runs no SQL and a fake
Redis runs no Lua — a test on either would assert that the parts with no concurrency risk work.

### A fixture bug worth recording

`Program.cs` reads some configuration **inline** while composing the app — the Postgres connection
string, and `Jwt:Key` for the bearer validation parameters. Those reads happen *before*
`WebApplicationFactory` applies its `ConfigureAppConfiguration` delegates, so overrides supplied that
way reached anything bound lazily through `IOptions` but not those two.

The visible symptom was tokens rejected with *"the signature key was not found"*: `JwtTokenService`
signed with the test key from `IOptions`, while the validator had already captured the key from
`appsettings.Development.json`.

The dangerous half was silent. The connection string fell back the same way, so the suite ran against
the developer's **local Postgres** instead of the container — and passed. A green run meant nothing.

Fixed by supplying settings as environment variables, which are in configuration from the first line
of `Program.cs`. `FixtureSanityTests` now asserts the resolved connection string really is the
container's and that the database holds only the seeded admin, so a regression fails loudly instead
of passing against real data.

---

## 6. Load test

`tests/Marquee.LoadTests/premiere-rush.js`, run with k6 (via the `grafana/k6` image — no local
install needed).

**Traffic shape** is `ramping-arrival-rate`, not a fixed VU count, because what is being simulated is
people *arriving*. Holding VUs constant lets the API's own latency throttle the burst to whatever it
can keep up with, quietly removing the contention the test exists to create.

**Participants are anonymous sessions**, not accounts — realistic (anonymous visitors can clap as of
Iteration 5) and the only way to get hundreds of *distinct* participants without registering hundreds
of accounts. Distinctness is what matters: the per-participant cap means one identity cannot generate
contention alone.

Two flaws in the first version, both found by running it:

- `http_req_failed` sat at **98%** on a run where nothing was wrong. Most of a burst arrives after the
  Premiere has opened, and a closed Premiere answering 409 is the correct outcome — but k6 counts
  every 4xx as failed by default. Declaring the expected statuses restored the metric's meaning.
- Counted claps were miscounted: `capReached` is reported both by the clap that *reaches* the cap and
  by every one refused afterwards. `myClaps` is monotonic per session and only advances on a real
  count, so the script tracks that per VU instead.

### Result

`PEAK_RATE=400` against the full stack. All three k6 thresholds passed:

```
✓ marquee_opens_observed  count<=1   → 1
✓ marquee_claps_failed    count==0   → 0
✓ http_req_failed         rate<0.01  → 0.00%
```

Postgres then confirmed what k6 cannot — the API's own responses are not the authoritative record:

```
 Status |  Threshold | TotalClaps | summed | rows
 Opened |        147 |        147 |    147 |  147
```

`TotalClaps` equals the summed `ClapCount` of its contribution rows, and equals k6's accepted count:
**no lost updates**. A separate query confirmed no `(user, premiere)` pair has more than one library
entry: **the fan-out did not run twice**.

> `TotalClaps` can slightly exceed `Threshold` — an earlier run finished at 148 against a threshold of
> 141. That is correct. The Redis cutoff is set *inside* the open, so claps already in flight when the
> threshold was crossed still count, and they receive contribution rows. That is exactly the
> "accepted implies granted" property the cutoff ordering exists to guarantee.

---

## 7. Admin dashboard

`GET /api/admin/metrics`, rendered at `/admin`.

Every number comes from the system that actually knows it, never from Postgres: queue depth from
RabbitMQ's management API, connections from a counter at the hub's lifecycle callbacks (SignalR
exposes none), clap rate from Redis — the only place claps exist while a Premiere is running, since
contributions are not written until the worker fans out.

The clap rate is per-second Redis buckets written **fire-and-forget**. A clap must never be slower, or
fail, because a metric write did; losing a tick under load costs a slightly low reading, which is the
right trade against a round trip on the hottest path in the system.

"Broker unreachable" is a separate flag, not a depth of zero, and renders as a dash. Nothing queued
and cannot reach the broker are opposite pieces of news, and showing both as `0` misleads precisely
during the outage someone opened the dashboard to investigate. For the same reason the reader
swallows its failures rather than throwing, and the panel keeps its last good reading when a poll
fails — a monitoring view that blanks itself when things break is the least useful possible
behaviour.

It polls rather than subscribing to SignalR: pushing operator-only telemetry down the Premiere hub
would mean either a second hub or broadcasting infrastructure numbers to every connected visitor.

---

## 8. Acceptance criteria

| Criterion | Evidence |
|---|---|
| A single clap is traceable end to end across both services | One trace, 30 spans, `api` → `worker` → `api` (§2). Correlation id identical on all three log lines across both processes (§1). |
| Load test runs with no lost claps and no duplicate opens | k6: exactly one `opened=true`, zero failed claps. Postgres: `TotalClaps` 147 = summed contributions 147; max one library entry per `(user, premiere)` (§6). |
| TMDB being down does not prevent an already-scheduled Premiere from running | Integration tests: a scheduled Premiere activates and an expired one auto-opens with TMDB throwing; generating *new* Premieres is the one operation an outage stops, and it fails cleanly for the daily job to retry (§5, §4). |

---

## 9. Notes for the next iteration

- **The outbox trace root** (§2) is the one loose end. Span links at delivery would close it.
- **Rate limiting and the scheduler are disabled for load runs** and the load-tests README says why,
  so neither reads as a claim that the guards are optional.
- **The dashboard's connection count is per-process.** v1 runs a single API instance with no SignalR
  backplane (§6 of `CLAUDE.md`), so that is the whole population today. With a backplane it becomes a
  Redis counter and the panel does not change.
- **Worker health** is still inferred rather than probed, by choice. If the worker ever needs to be
  orchestrated independently, that decision is worth revisiting.
