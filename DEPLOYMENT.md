# Deployment plan (AWS)

Not part of the build sequence in `MARQUEE_PLAN.md`. This is the plan for running Marquee on AWS, and
it doubles as a deliberate AWS learning exercise — where two options are otherwise equal, the
AWS-native one wins. Development on the app continues alongside it: work still lands on `main` through
branches and PRs, and a merge to `main` becomes a deploy.

## Goals and priorities

1. **Get the cloud infrastructure right for the app to run.** This is the objective.
2. **Everything is infrastructure as code**, in the **AWS CDK (C#)** — the AWS-native choice, and the
   same language as the rest of the backend. Nothing is created by hand in the console except the
   one-time account setup (CDK bootstrap, the budget's email subscription confirmation).
3. **The domain is second-class.** Phases 1 and 2 run on CloudFront's default
   `https://<id>.cloudfront.net` URL, which already has HTTPS. A domain arrives in phase 3.

## Target scale

No revenue, no marketing push, no company entity — a small number of real users (low dozens to low
hundreds). Everything below is sized for that. Cost matters: services that bill while idle have to
earn their place.

## Constraints the app imposes

These come from the code as it stands, and they shape the infrastructure more than anything else.

- **Single instance, by design.** CLAUDE.md §6 rules out multi-instance deployment and the SignalR
  Redis backplane, and the Quartz scheduler runs inside the API. Running two API tasks would split
  SignalR groups and double-fire the scheduler. So: no autoscaling, no load balancer, one API and one
  Worker. What it would take to scale out is a known list (backplane, clustered Quartz), not a
  surprise.
- **"Local time" is the server's clock.** §4.4's 07:00–23:00 window and the peak-hours rule are
  evaluated with `DateTime.Now` / `ToLocalTime()`. Containers default to UTC, so the containers must
  run with `TZ` set to the audience's zone, and the base image must ship `tzdata` (chiseled and Alpine
  .NET images do not).
- **The API migrates the database on startup** (`Program.cs`, "dev convenience"). Acceptable with one
  instance and a stop-then-start deploy; it means a deploy has a short outage and a migration must be
  compatible with being run by the container that needs it.
- **Rate limiting keys on the client IP** with no forwarded-header handling
  (`RateLimitingRegistration.cs`). Behind CloudFront every request would arrive from a CloudFront
  address and all users would share buckets. Must be fixed before going live (phase 1, app changes).
- **All configuration currently lives in `appsettings.Development.json`.** Production needs its own:
  non-secret tunables in a committed `appsettings.Production.json`, secrets and endpoints in SSM.

---

## Architecture (end of phase 1)

```
                         viewer (HTTPS)
                               │
                   ┌───────────▼────────────┐
                   │ CloudFront             │  default *.cloudfront.net cert
                   │  /*        → S3        │  (SPA rewrite via CloudFront Function)
                   │  /api/*    → EC2 :80   │  caching disabled, all viewer headers/query
                   │  /hubs/*   → EC2 :80   │  caching disabled, WebSockets
                   └─────┬─────────────┬────┘
              OAC (private)            │ HTTP, SG allows only the CloudFront
                         │             │ origin-facing managed prefix list
                   ┌─────▼────┐  ┌─────▼──────────────────────────────────────┐
                   │ S3       │  │ EC2 (public subnet, Elastic IP, no SSH)    │
                   │ Angular  │  │  docker compose:                           │
                   │ build    │  │   api · worker · postgres · redis · rabbit │
                   └──────────┘  │  data on a separate retained EBS volume    │
                                 └──┬───────────┬──────────────┬──────────────┘
                                    │           │              │
                              ECR (images)  SSM Parameter  CloudWatch Logs
                                            Store (secrets) (awslogs driver)
```

Because the frontend, `/api` and `/hubs` all sit behind one CloudFront distribution, the browser sees
**a single origin**: no CORS in production, and the SignalR negotiate/WebSocket traffic is same-origin.

---

## Phase 1 — the stack runs on AWS (no real users yet)

Exit criterion: the full app works end to end at the CloudFront URL, deploys itself on merge to
`main`, and survives an instance reboot with its data intact. **Nobody is invited yet** — phase 2
changes how accounts work, and doing that before real users exist avoids a user migration.

### 1a. App changes (before any infrastructure)

1. **Dockerfiles for `Marquee.Api` and `Marquee.Worker`.** Multi-stage (SDK build → ASP.NET runtime),
   on a Debian-based runtime image so `tzdata` is present. Run as non-root.
2. **`docker-compose.prod.yml`**, separate from the local `docker-compose.yml`:
   - `api` and `worker` pulled from ECR by tag (the commit SHA), never built on the box.
   - Only the API is published (`80:8080`); Postgres, Redis and RabbitMQ publish **no** host ports.
   - `TZ` set on the API and Worker only; infrastructure stays on UTC (Postgres would otherwise fix
     its `timezone` setting from `TZ` at initdb). `restart: unless-stopped`.
   - No logging config in the compose file: the EC2 host sets the Docker daemon's default log
     driver to `awslogs` (`/etc/docker/daemon.json`, in user data), so the same file runs locally
     on `json-file` and ships to CloudWatch on the host.
   - Credentials from an env file written at deploy time from SSM — no `marquee`/`marquee` defaults.
   - Jaeger left out by default (RAM). If wanted, run it bound to `127.0.0.1` and reach it through
     Session Manager port forwarding — same for the RabbitMQ management UI.
3. **`appsettings.Production.json`** with the non-secret tunables that today exist only in the
   Development file (schedule, scheduler, clap guards, rate limits, TTLs, messaging retry). Secrets and
   endpoints come from environment variables (`Jwt__Key`, `ConnectionStrings__Postgres`, …).
4. **Forwarded headers.** Enable `ForwardedHeaders` for `X-Forwarded-For`/`X-Forwarded-Proto` with
   `ForwardLimit = 1`. Trusting the immediate peer is safe *only* because the security group admits
   nothing but CloudFront — record that dependency in a comment where it is configured. This fixes
   the shared rate-limit bucket.
5. **Frontend production environment.** `environment.prod.ts` with relative URLs (`/api`,
   `/hubs/premieres`) wired through `fileReplacements`. The CORS policy stays as-is for local dev;
   production is same-origin and never exercises it.
6. **Email in phase 1:** none. `Email:SmtpHost` stays empty, so `DevNotificationDispatcher` writes
   confirmation/reset links to the log — which lands in CloudWatch. That is enough for the handful of
   test accounts phase 1 needs, and phase 2 replaces the email path anyway.

### 1b. Infrastructure (CDK, C#)

A CDK app in `infra/` (its own `.csproj`, outside `Marquee.sln` so the app build is unaffected). Two
stacks:

**`MarqueeCiStack`** — deployed rarely, by hand:
- GitHub OIDC identity provider.
- A deploy role trusted only for `repo:jdavidIP/Marquee:ref:refs/heads/main` (and a read-only
  `cdk diff` role for PRs if wanted), scoped to: push to the ECR repos, write the S3 site bucket,
  create CloudFront invalidations, `ssm:SendCommand` to the one instance, read the artifacts bucket.
- Separate stack so a deploy can never edit the permissions of the role performing it.

**`MarqueeStack`** — everything the app runs on:
- **VPC**: 1 AZ, public subnets only, **no NAT gateway** (~$32/mo idle for nothing we need).
- **EC2**: Amazon Linux 2023, `t3.small` (2 vCPU / 2 GB — five containers do not fit in 1 GB).
  - **AMI pinned**, not looked up fresh on every synth: a new AMI version would otherwise *replace*
    the instance on the next deploy.
  - IMDSv2 required, no key pair, **no port 22** — shell access through SSM Session Manager.
  - User data installs Docker and the compose plugin, mounts the data volume.
  - **Elastic IP**, so the public DNS name CloudFront uses as its origin survives stop/start.
  - Instance role: `AmazonSSMManagedInstanceCore`, ECR pull, `ssm:GetParametersByPath` on
    `/marquee/prod/*`, CloudWatch Logs write.
- **Data volume**: a separate encrypted gp3 EBS volume for the Docker volumes (Postgres, Redis AOF,
  RabbitMQ), `RemovalPolicy.RETAIN`, so replacing the instance never destroys data. **Daily snapshots**
  via Data Lifecycle Manager (or AWS Backup), 7-day retention.
- **Security group**: inbound TCP 80 **only** from the managed prefix list
  `com.amazonaws.global.cloudfront.origin-facing`. Nothing else inbound.
- **ECR**: `marquee-api` and `marquee-worker`, lifecycle rule keeping the last ~10 images.
- **S3 site bucket**: private, reached only through CloudFront **Origin Access Control**.
- **CloudFront distribution**:
  - Default behaviour → S3. SPA deep links (`/library`, `/u/…`) are handled by a small **CloudFront
    Function** that rewrites extension-less paths to `/index.html`. *Not* custom error responses:
    those apply distribution-wide and would turn every API 404 into `index.html` with a 200.
  - `/api/*` and `/hubs/*` → the EC2 origin over HTTP, `CachingDisabled`, origin request policy
    `AllViewerExceptHostHeader` (forwards `Authorization`, `X-Anon-Session`, and the query string
    SignalR uses for `access_token` on WebSockets). CloudFront supports WebSockets natively;
    SignalR's 15 s keep-alive keeps the connection under the idle timeouts — verify under load.
  - `index.html` served `no-cache`; hashed assets long-cached.
  - Standard logging **off** (it would record the SignalR `access_token` query parameter).
- **SSM Parameter Store** (standard tier, free): `SecureString`s under `/marquee/prod/` — JWT key,
  Postgres and RabbitMQ passwords, TMDB key, admin seed password. Created by hand once (CDK should not
  hold secret values), referenced by name.
- **CloudWatch**: log groups per service with 14-day retention; alarm + auto-recover on EC2 system
  status check failure; CPU alarm.
- **AWS Budgets**: monthly budget with email alerts at 50/80/100%. Set up **first**, before anything
  else is deployed.
- Optional: `cdk-nag` (AwsSolutions pack) in synth, with each suppression justified in code.

### 1c. Deploy pipeline (GitHub Actions, no long-lived AWS keys)

A `deploy.yml` workflow on push to `main`, running after CI passes:

1. Assume the deploy role via **OIDC**.
2. Build and push `marquee-api` / `marquee-worker` images to ECR, tagged with the commit SHA.
3. Upload `docker-compose.prod.yml` and `deploy.sh` to an artifacts bucket under that SHA.
4. **SSM Run Command** on the instance: fetch the artifacts, write the env file from Parameter Store,
   `docker compose pull && docker compose up -d`, then poll `/health/ready` locally and fail the run if
   it never goes healthy. No SSH anywhere in the path.
5. Build Angular with the production configuration → `aws s3 sync` → CloudFront invalidation of
   `/index.html`.

Infrastructure changes go through `cdk diff` / `cdk deploy` run by hand at first; CI additionally runs
`cdk synth` on PRs so a broken stack is caught before merge. Automating `cdk deploy` is a later step.

### 1d. Verification

- Clap a Premiere open from two browsers; watch SignalR counts move through CloudFront.
- Reboot the instance: counters, pending messages and the schedule survive.
- Restore a snapshot of the data volume into a scratch volume once, to prove backups actually restore.
- Run the k6 scripts against the CloudFront URL and check them against the capacity estimate below.
- Confirm the rate limiter sees distinct client IPs (log line per request carries the IP).

---

## Phase 2 — authentication moves to Cognito

**This phase changes a domain rule and needs decisions before it starts** (CLAUDE.md §4.1/§4.2 are
written around `EmailConfirmedAt` in Postgres). Must land before real users sign up: existing password
hashes cannot be imported into Cognito, so switching later would need a User Migration Lambda.

What changes:

- **Cognito User Pool** (CDK) for sign-up, sign-in, confirmation and password reset. The existing
  `PasswordHasherService`, confirmation-token and reset-token services and the #27 password rules are
  replaced by pool configuration.
- **The API validates Cognito tokens** — `JwtBearer` pointed at the pool as authority. Integration
  tests keep minting their own tokens through a test issuer accepted only outside Production.
- **The frontend keeps its own screens** (the pass-card login, #64/#67) and calls Cognito through
  Amplify's Auth module or the raw API. Cognito's hosted Managed Login is not used — it would discard
  the redesign.
- **Email uses Cognito's built-in sender** — no domain or SES needed, capped at a small daily quota
  (check the current limit). Fine for the target scale until phase 3.

Proposed division of responsibility: **Cognito owns authentication only.** Role, permissions,
`IsBlocked`, username and everything else the domain uses stay in Postgres as the source of truth.

Decisions to make first:

1. **Unconfirmed accounts.** Cognito will not let an unconfirmed user sign in at all, whereas today an
   unconfirmed account can authenticate and clap as an anonymous participant (§4.2). Under Cognito an
   unconfirmed account is simply a visitor with an anonymous session — simpler, and the
   `unconfirmed:` branch of `ParticipantResolver` disappears. §4.2 needs rewording to match.
2. **When the Postgres `User` row is created.** Options: (a) a **Post Confirmation Lambda trigger**
   writes it — AWS-native, but the Lambda needs VPC access to Postgres and the design must not assume
   a failed trigger rolls the confirmation back; (b) **just-in-time on first authenticated request** —
   no Lambda, and since only confirmed users can sign in, every row is confirmed by construction.
   Under (b) a user who confirms but never signs in does not count toward §4.1's threshold until they
   do — a rule change to decide on explicitly.
3. **Confirmation by code, not link.** Cognito's link confirmation lands on a Cognito page that cannot
   redirect back into the app, so `confirm-email` becomes a code-entry step.
4. **Issue #30** (expire unconfirmed accounts) becomes cleanup of unconfirmed Cognito users rather
   than Postgres rows — re-scope it.
5. **Admin seed.** The seeded admin must exist in Cognito too; Role stays in Postgres.

---

## Phase 3 — managed database, domain, real email

- **RDS for PostgreSQL** (`db.t4g.micro`, single-AZ, in private isolated subnets). SG-to-SG rule from
  the instance only; automated backups; cutover via `pg_dump`/`pg_restore` in a short maintenance
  window. Separate stack with termination protection — stateful resources should not share a
  lifecycle with the stateless ones. Check the current free tier first; AWS reworked it in 2025.
- **Domain**: Route 53 hosted zone, ACM certificate in **us-east-1** (CloudFront requires it) —
  a cross-region reference in CDK.
- **SES**: domain identity with DKIM, request production access (new accounts start in the sandbox),
  switch Cognito's email sending to SES.
- **Origin TLS**: with a real domain the CloudFront → EC2 hop can move to HTTPS.

---

## Deliberately not used

| Service | Why not |
|---|---|
| ALB | ~$16+/mo idle to front a single instance; CloudFront already routes and terminates TLS. |
| ECS / Fargate / App Runner | Their value is scaling and replacement across tasks — the app is single-instance by design. Compose on EC2 is honest about that. |
| ElastiCache | Idle cost for a Redis that is nowhere near its limits on the box. |
| Amazon MQ | Idle cost; the broker's load is one message per Premiere opening. |
| NAT gateway | ~$32/mo idle; the instance sits in a public subnet behind a CloudFront-only SG instead. |
| CloudFront VPC origins | Would allow a fully private instance, but a private instance then needs a NAT (or several interface endpoints) to reach TMDB and ECR. Revisit if the cost picture changes. |
| Multi-AZ anything | No availability target that justifies doubling the bill. |

## Rough monthly cost (phase 1, on-demand, check the pricing calculator)

| Item | ≈ USD/mo |
|---|---|
| EC2 `t3.small` | 15 |
| Public IPv4 (Elastic IP) | 3.60 |
| EBS root + data volume (gp3) + snapshots | 4–6 |
| CloudWatch Logs | 1–3 |
| S3, CloudFront, ECR, SSM standard params | ~0–2 at this traffic |
| **Total** | **~25–30** |

Phase 2 adds effectively nothing at this user count (Cognito's free MAU allowance). Phase 3's RDS
instance is the next real cost step.

## Expected capacity (phase 1 instance)

- The Redis clap counter is not the bottleneck (tens of thousands of ops/sec on modest hardware).
- The real limit is CPU/RAM contention between the colocated services plus held-open SignalR
  connections. Estimate for a 2 vCPU / 2 GB box: a few hundred concurrent clappers per Premiere, low
  thousands of idle connections. The k6 run in 1d replaces this estimate with a measurement.
- If it strains: a bigger instance first, splitting services apart last.

## Open decisions

- **AWS region** — nearest the expected audience.
- **Timezone** for `TZ` (§4.4's "local time").
- **Instance family** — `t3.small` (x86, no surprises) or `t4g.small` (Graviton, cheaper, but images
  must be built for arm64).
- **Phase 2 decisions** 1–5 above.

## Legal / licensing — resolve before inviting real users

1. **TMDB API terms**: the free API is for non-commercial use; commercial use needs written permission
   and is judged case by case (revenue, branding, scale, public availability). Email
   api@themoviedb.org before inviting users — describe it plainly (no ads or monetisation,
   hobby/portfolio, low hundreds of users) and get the answer in writing.
2. **"Marquee" trademark**: a generic word, but in use by entertainment businesses (e.g. the Marquee
   nightclub chain). Run a USPTO search in entertainment/software classes before a public-facing name
   and domain are chosen in phase 3.
