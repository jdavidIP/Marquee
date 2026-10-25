# Deployment plan (AWS)

Not part of the build sequence in `MARQUEE_PLAN.md`. This is the plan for running Marquee on AWS, and
it doubles as a deliberate AWS learning exercise — where two options are otherwise equal, the
AWS-native one wins. Development on the app continues alongside it: work still lands on `main` through
branches and PRs, and a merge to `main` becomes a deploy.

## Goals and priorities

1. **Get the cloud infrastructure right for the app to run.** This is the objective.
2. **Everything is infrastructure as code**, in the **AWS CDK (C#)** — the AWS-native choice, and the
   same language as the rest of the backend. Nothing is created by hand in the console except the
   one-time account setup and CDK bootstrap. (The budget's email alerts need no confirmation — AWS
   Budgets emails its recipients directly.)
3. **The domain is second-class.** Phases 1 and 2 run on CloudFront's default
   `https://<id>.cloudfront.net` URL, which already has HTTPS. A domain arrives in phase 3.

## Target scale

No revenue, no marketing push, no company entity — a small number of real users (low dozens to low
hundreds). Everything below is sized for that. Cost matters: services that bill while idle have to
earn their place.

## Account

- **Region:** `ca-central-1` (Canada Central).
- **Created** 2026-09-25, on the **Free plan**: usage draws down the $100 sign-up credit (+ up to $100
  earned through activities).
- **Free plan ends 2027-03-24**, or earlier if the credits run out first. The account then **closes
  automatically** and the deployed app goes offline. At the ~$25–30/month estimate below, $100 lasts
  roughly 3–4 months, so the credits are likely to be the earlier cutoff.
- **Intent:** upgrade to the Paid plan before whichever cutoff comes first (leftover credits carry
  over). The budget alerts are the early warning.
- **Budget:** `marquee-monthly` (in `MarqueeCiStack`), $30/month measured *before* credits, emailing at
  50/80/100% of actual and 100% of forecast cost.

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
3. **Production configuration without an `appsettings.Production.json`.** Every tunable's in-code
   default already is its production value (the Development file mostly restates them), so there is
   nothing to copy. Secrets and endpoints come from environment variables (`AnonymousSession__SigningKey`,
   `ConnectionStrings__Postgres`, …), and in Production the API and Worker refuse to start if any
   key whose default is a local-dev value is missing (`RequireKeys`) — notably `Tmdb:ApiKey` (else the
   offline stub) and `Admin:Password` (else the repository's dev password on the seeded admin).
4. **Forwarded headers.** `ForwardedHeaders` for `X-Forwarded-For` only (nothing reads the scheme),
   `ForwardLimit = 1`, switched on by `ForwardedHeaders__Enabled` in the prod compose file. Trusting the
   immediate peer is safe *only* because every request the API accepts came through Marquee's own
   CloudFront distribution — the security group narrows inbound to CloudFront's address range, and the
   origin header check (`OriginVerification`, §1b) narrows that to our distribution. That dependency is
   recorded where it is configured. This fixes the shared rate-limit bucket.
5. **Frontend production environment.** `environment.prod.ts` with relative URLs (`/api`,
   `/hubs/premieres`) wired through `fileReplacements`. The CORS policy stays as-is for local dev;
   production is same-origin and never exercises it.
6. **Email in phase 1:** none. `Email:SmtpHost` stays empty, so `DevNotificationDispatcher` writes
   confirmation/reset links to the log — which lands in CloudWatch. That is enough for the handful of
   test accounts phase 1 needs, and phase 2 replaces the email path anyway.
   *As built:* phase 2 moved those emails to the Cognito user pool, and the `Email:SmtpHost` key and the
   SMTP dispatcher were removed as unused (#131).

### 1b. Infrastructure (CDK, C#)

A CDK app in `infra/` (its own `.csproj`, outside `Marquee.sln` so the app build is unaffected). Two
stacks:

**`MarqueeCiStack`** — deployed rarely, by hand:
- GitHub OIDC identity provider.
- A deploy role trusted only for `repo:jdavidIP@90657602/Marquee@1311474951:ref:refs/heads/main` (and a
  read-only `cdk diff` role for PRs if wanted), scoped to: push to the ECR repos, write the S3 site
  bucket, create CloudFront invalidations, `ssm:SendCommand` to the one instance, read the artifacts
  bucket. The subject is in GitHub's immutable format, with the owner and repo suffixed by their
  numeric ids, because that is what this repo's OIDC tokens carry.
- Separate stack so a deploy can never edit the permissions of the role performing it.
- The deploy role is created with **no permissions**. Each stack that owns a resource grants the role
  access to that resource's ARN — never a broad `*`.

Deploying the stacks by hand, and the CLI credentials CDK needs: [runbook](docs/deployment/runbook.md#cdk-credentials-diff-and-deploy).

The account comes from the signed-in CLI and the alert address from the command line, so neither is
committed; synth refuses to run without `alertEmail`, which receives both the budget and the
CloudWatch alarm emails. The stack outputs `DeployRoleArn` for the workflows. **Pass the same address
on every deploy:** a different one replaces the SNS subscription rather than adding to it, and the new
address has to confirm (within 3 days, see the
[runbook](docs/deployment/runbook.md#confirm-an-alarm-subscription)) before any alarm reaches it. `cdk diff` shows this as
a subscription destroyed and another created — a sign the wrong value was passed.

**`MarqueeStack`** — everything the app runs on:
- **VPC**: 1 AZ, public subnets only, **no NAT gateway** (~$32/mo idle for nothing we need).
- **EC2**: Amazon Linux 2023, `t3.small` (2 vCPU / 2 GB — five containers do not fit in 1 GB), in
  `ca-central-1a`.
  - **CPU credits `standard`**, not the T3 default `unlimited`: sustained load above the 20% baseline
    throttles the host instead of billing surplus credits against the Free plan.
  - **AMI pinned**, not looked up fresh on every synth: a new AMI version would otherwise *replace*
    the instance on the next deploy. Moving to a newer one is a deliberate edit in `MarqueeStack.cs`.
    The metadata hop limit the API container needs (2, see 2a) is set by the stack on the instance
    itself (#128), so an AMI change no longer decides it.
  - IMDSv2 required, no key pair, **no port 22** — shell access through SSM Session Manager.
  - Encrypted 20 GB gp3 root volume, with a 2 GB swap file so a memory spike slows the host rather
    than OOM-killing a container.
  - User data (first boot only — editing it later does not re-run it on the existing host): mounts the
    data volume, installs Docker from the AL2023 repositories and the compose plugin as a pinned,
    checksum-verified release binary, and writes `/etc/docker/daemon.json` so every container logs to
    CloudWatch through the `awslogs` driver. Docker is set to refuse to start without the data volume
    mounted.
  - **Elastic IP**, so the public DNS name CloudFront uses as its origin survives stop/start. The stack
    outputs it as `HostPublicDns`.
  - Instance role: `AmazonSSMManagedInstanceCore`, ECR pull, `ssm:GetParametersByPath` on
    `/marquee/prod/*`, CloudWatch Logs write, read on the artifacts bucket.
- **Data volume**: a separate encrypted 10 GB gp3 EBS volume mounted at `/var/lib/docker/volumes`
  (Postgres, Redis AOF, RabbitMQ, and Docker's own index of the volumes), `RemovalPolicy.RETAIN`, so
  replacing the instance never destroys data. **Daily snapshots** via Data Lifecycle Manager at 08:00 UTC
  (03:00–04:00 in Toronto, outside the Premiere window), 7 kept. Snapshots are crash-consistent, which
  Postgres recovers from like a power cut.
- **Security group**: inbound TCP 80 **only** from the managed prefix list
  `com.amazonaws.global.cloudfront.origin-facing`. Nothing else inbound. It is the host's only group,
  because the prefix list counts as ~55 rules against a group's rule quota. That list covers *every*
  CloudFront distribution, anyone's, so the group alone does not make the host ours: the secret origin
  header below does.
- **ECR**: `marquee-api` and `marquee-worker`, scan on push, lifecycle rule keeping the last 10 images.
- **Artifacts bucket**: private, TLS-only, objects expire after 30 days. Holds each deploy's
  `docker-compose.prod.yml` and `deploy.sh`.
- **Deploy role grants** (on `marquee-github-deploy`): push to the two ECR repos, put objects in the
  artifacts bucket, `ssm:SendCommand` on this instance with `AWS-RunShellScript`, read/write/delete on
  the site bucket, `cloudfront:CreateInvalidation` on this distribution, `cloudformation:DescribeStacks`
  on this stack (the pipeline reads the outputs live), and `ssm:GetCommandInvocation` on `*` (to poll
  the deploy command; the action supports no resource scoping, and only reads output). `MarqueeStack`
  is deployed by hand, never by that role, so the role still cannot change its own permissions.
- **S3 site bucket**: private, TLS-only, reached only through CloudFront **Origin Access Control**.
- **CloudFront distribution** on the default `*.cloudfront.net` certificate, price class 100:
  - Default behaviour → S3, `CachingOptimized`, which honours each object's `Cache-Control` as set at
    upload. SPA deep links (`/library`, `/u/…`) are handled by a small **CloudFront Function** that
    rewrites any path *not* ending in a known static-file extension to `/index.html` — keyed on the
    extension rather than "contains a dot", because usernames may contain one (`/u/john.doe`). *Not*
    custom error responses: those apply distribution-wide and would turn every API 404 into
    `index.html` with a 200.
  - `/api/*` and `/hubs/*` → the EC2 origin over HTTP, `CachingDisabled`, origin request policy
    `AllViewerExceptHostHeader` (forwards `Authorization`, `X-Anon-Session`, and the query string
    SignalR uses for `access_token` on WebSockets). CloudFront supports WebSockets natively;
    SignalR's 15 s keep-alive keeps the connection under the idle timeouts — verify under load.
  - **Secret origin header**: the origin adds `X-Origin-Verify`, and the API refuses (403) any request
    without the right value (`OriginVerification`). `/health/*` is exempt only for loopback callers —
    the container's own healthcheck — because the security group admits *any* distribution, and an
    open exemption would let a stranger's run the dependency checks.
    A viewer-supplied `X-Origin-Verify` is overwritten by CloudFront, so it cannot be spoofed through
    the distribution. The value is resolved from SSM at `cdk deploy` time.
  - `index.html` served `no-cache`; hashed assets long-cached.
  - Standard logging **off** (it would record the SignalR `access_token` query parameter).
  - Outputs: `SiteUrl`, `SiteBucket`, `DistributionId`.
- **SSM Parameter Store** (standard tier, free): `SecureString`s under `/marquee/prod/`, created by
  hand once (CDK should not hold secret values), named after the `.env` key each becomes. **Use only
  letters and digits in every value** (`openssl rand -hex 24` for the random ones): `deploy.sh` and the
  manual deploy (runbook) write them into `.env` unquoted, and Compose treats `$` as interpolation and ` #` as a comment
  there, so a value containing either would be silently corrupted.
  - `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD` — random; the app never sees anything else.
  - `AnonymousSession__SigningKey` — random, **at least 32 characters** (the API checks this at startup
    and refuses to start below it). Signs the visitors' anonymous session tokens; rotating it ends every
    current session (they last 3 hours). Replaces `Jwt__Key`, which signed the API's own user tokens
    until #112 removed them: create this parameter before cutover, and keep `/marquee/prod/Jwt__Key`
    until the cutover is confirmed stable — a rollback to the old images needs it (see Rollback, 1c).
  - `Tmdb__ApiKey` — the TMDB **v3 API key**, not the read access token.
  - `Admin__Password` — at least 10 characters with a digit (the user pool's password policy); this is
    the seeded admin's sign-in password, so keep it somewhere you can find it again.
  - `OriginVerify__Secret` — random (`openssl rand -hex 32`). **A plain `String`, not a
    `SecureString`**: CloudFormation can only resolve a `String` into the CloudFront header, and the
    value is readable in the distribution's config by anyone with account access anyway. Must exist
    before `cdk deploy` (the stack reads it) and before the API starts in Production (it is a required
    key). The host's `.env` picks it up with the rest of the path.
- **CloudWatch**: one log group, `/marquee/prod`, with a stream per container, 14-day retention.
  Two alarms, both emailing `alertEmail` through an SNS topic. **Confirm the subscription email within
  3 days of the deploy that creates it:** SNS silently deletes an unconfirmed subscription and
  CloudFormation keeps reporting it as created, so the alarms email nobody (#133). How to confirm it and
  how to spot a lost one: [runbook](docs/deployment/runbook.md#confirm-an-alarm-subscription).
  - EC2 system status check failed → the instance is **recovered** onto healthy hardware, keeping its
    id, Elastic IP and volumes.
  - CPU credit balance below 20 → about to be throttled to baseline. Under `standard` credits this is
    the meaningful CPU signal; utilization cannot show it, because it drops once throttled. T3
    `standard` launches with no credits, so it fires for roughly the first hour after the instance is
    created.
- **AWS Budgets**: monthly budget with email alerts at 50/80/100%. Set up **first**, before anything
  else is deployed.
- Optional: `cdk-nag` (AwsSolutions pack) in synth, with each suppression justified in code.

### 1b-ops. Manual deploy (fallback)

The manual deploy and the origin-secret rotation are in the
[runbook](docs/deployment/runbook.md#manual-deploy-fallback).

### 1c. Deploy pipeline (GitHub Actions, no long-lived AWS keys)

`.github/workflows/deploy.yml` runs when **CI succeeds on a push to `main`**, and deploys the commit CI
tested (`workflow_run`, not its own `push` trigger — CI cancels superseded runs, which is right for
tests but must never interrupt a deploy). Deploys share a concurrency group and **queue, never cancel**.

**Backend job:**

1. Assume the deploy role via **OIDC** — the repository holds no AWS credentials, only the role ARN
   as the `AWS_DEPLOY_ROLE_ARN` variable. The role trusts `main` only.
2. Read `MarqueeStack`'s and `MarqueeAuthStack`'s outputs live (`describe-stacks`), so no resource id
   is copied into settings. The pool's issuer and client id reach the API through `deploy.sh`'s
   arguments into `.env`.
3. Build and push `marquee-api` / `marquee-worker` to ECR, tagged with the 7-character commit SHA.
4. Upload `docker-compose.prod.yml` and `deploy.sh` to the artifacts bucket under that tag.
5. **SSM Run Command** runs `deploy.sh` on the host: fetch the compose file, write `.env` from
   Parameter Store (via a temp file, mode 600), pull the api/worker images, `docker compose up -d
   --wait --wait-timeout 240`. That exits non-zero if the API's in-container healthcheck never passes
   — not `curl localhost/health/ready` from the host, which the origin check refuses (1b-ops). On
   success it records the tag in `/opt/marquee/last-healthy-tag`; on failure it prints that tag, the
   container states and the API's last log lines, and exits 1. The workflow polls the command
   (`ssm:GetCommandInvocation`), prints its output, and fails with it. No SSH anywhere in the path.

**Frontend job**, only after a healthy backend: `ng build --configuration production` → `aws s3
sync` with the cache headers from 1b-ops → CloudFront invalidation of `/index.html`. A failed backend
therefore never ships a frontend ahead of its API.

**Rollback** is the same workflow run by hand with an existing tag, backend only: see the
[runbook](docs/deployment/runbook.md#rollback), including why the phase 2 cutover cannot be rolled
back that way.

A failed deploy prints only container states to the Actions log, never log lines — the repository is
public, and so are its workflow logs. The containers' logs are in CloudWatch, log group
`/marquee/prod`.

Infrastructure changes go through `cdk diff` / `cdk deploy` run by hand; CI runs `cdk synth` on every
PR (`ci.yml`, no AWS access) so a stack that cannot even produce a template is caught before merge.
Automating `cdk deploy` is a later step.

### 1d. Verification

Recorded in the [verification log](docs/deployment/verification-log.md#1d-phase-1-verification) (phase 1 checks, 2026-09-28 to 2026-09-29).

---

## Phase 2 — authentication moves to Cognito

**This phase changes domain rules** (CLAUDE.md §4.1/§4.2 are written around `EmailConfirmedAt` in
Postgres). Must land before real users sign up: existing password hashes cannot be imported into
Cognito, so switching later would need a User Migration Lambda. The decisions below were settled on
2026-10-04; the domain-rule wording they implied went into CLAUDE.md §3, §4.1 and §4.2 in #112.

What changes:

- **Cognito User Pool** (CDK) for sign-up, sign-in, confirmation and password reset. The API's own
  password hasher, token minting and confirmation/reset-token services were deleted in #112.
- **The API validates Cognito access tokens** — `JwtBearer` pointed at the pool as authority (decision
  9). Integration tests sign in through a cognito-local Testcontainer; the API has no test-only token
  path.
- **The frontend keeps its own screens** (the pass-card login, #64/#67) and calls Cognito's API
  directly (decision 6). Cognito's hosted Managed Login is not used — it would discard the redesign.
- **Email uses Cognito's built-in sender** — no domain or SES needed, capped at a small daily quota
  (check the current limit when building). Every sign-up and resend counts against it: fine with no
  users invited, a hard ceiling for a launch. Phase 3's SES lifts it.

Division of responsibility: **Cognito owns authentication only.** Role, permissions, `IsBlocked`,
username and everything else the domain uses stay in Postgres as the source of truth.

### Decisions (settled 2026-10-04)

1. **Unconfirmed accounts are plain visitors.** Cognito refuses sign-in to an unconfirmed user
   (`UserNotConfirmedException`), so "signed in but unconfirmed" stops existing. Until confirming, the
   person is an ordinary visitor with an anonymous session. Removed: the `unconfirmed:` branch of
   `ParticipantResolver`, the `marquee:email_confirmed` claim and `IsEmailConfirmed()`, and the login
   screen's signed-in "unconfirmed" state. A visitor who registers mid-Premiere keeps clapping on their
   existing anonymous session until they confirm, instead of switching to a derived `unconfirmed:` one;
   once signed in they are registered and win over the anonymous header, exactly as today.
2. **The Postgres `User` row is created just-in-time** on the first authenticated request, by the same
   per-request lookup as decision 7 — a token whose `sub` has no row gets one. `User.Id` **is** the
   Cognito `sub` (both GUIDs, no mapping column); the username comes from the access token's `username`
   claim, and the email — which access tokens do not carry (decision 9) — from one `GetUser` call made
   with that same access token (no IAM needed), only on row creation. Every row is confirmed by
   construction; `EmailConfirmedAt` stays and is set at row creation, so the filters that read it were
   always-true; #112 removed them. **Creation must be idempotent** (CLAUDE.md
   §7): a page load fires several parallel first requests, so insert, and on a unique-key conflict
   re-read the row that won. A Cognito username or email already held by a row with a *different* id
   (a pre-cutover row, say) is refused and logged, never merged or overwritten. The frontend signs in
   immediately after `ConfirmSignUp` with the password it still holds, so the gap between confirming
   and counting is a single request.
   *Rejected:* a Post Confirmation Lambda. Postgres is not published on the host, there is no NAT (so a
   VPC Lambda cannot reach SSM without a ~$8/month interface endpoint), it also fires on password resets,
   and a failed trigger leaves a confirmed user with no row — so it would need this path as a fallback
   anyway.
3. **Confirmation by code, not link.** Cognito's link confirmation lands on a Cognito page that cannot
   redirect back into the app. The existing `confirm-email` route becomes the code-entry page
   (`/confirm-email?u=<username>`, survives a reload), reached right after registering and whenever sign-in
   returns `UserNotConfirmedException`, with a "resend code" action (`ResendConfirmationCode`). Sign-up
   codes are valid 24h. **Password reset follows the same shape**: `ForgotPassword` /
   `ConfirmForgotPassword`, so `reset-password` becomes "code + new password"; reset codes are valid
   **1 hour** and Cognito allows 5–20 reset attempts per user per hour. This does not reopen #48 — signing
   in still needs the password, a code is never a session, and a mail scanner cannot type one.
4. **Issue #30 is kept as a learning task.** With no Postgres row before confirmation, an unconfirmed
   Cognito user holds nothing a visitor doesn't except its username — low product value, and every
   foreign-key concern in #30 is moot. It is re-scoped to: an EventBridge Scheduler schedule (daily) runs a
   small Lambda (CDK, no VPC — it only calls Cognito) that deletes users with
   `cognito:user_status = "UNCONFIRMED"` older than 7 days (single stage, configurable). Admin-created
   users are never `UNCONFIRMED`, so the filter cannot reach the seeded admin. Cost: effectively $0
   (Scheduler's 14M free invocations/month, Lambda's always-free tier). Built after phase 2's core.
5. **Admin seed: startup seeder, Cognito first.** On start, `AdminCreateUser` (with `email_verified=true`,
   `MessageAction=SUPPRESS`; `AdminGetUser` if it already exists), then `AdminSetUserPassword` with
   `Permanent=true`, then the Postgres row with `Id = sub` and `Role = Admin`. The instance role gets
   those three actions scoped to the pool ARN; the password stays in `/marquee/prod/Admin__Password`.
   Cognito enforces the pool policy on `AdminSetUserPassword`, so a weak password now fails seeding
   rather than warning (today `SeedAdminAsync` only logs against `PasswordPolicyOptions`). Any seeding
   failure — weak password, Cognito unreachable, a missing IAM permission — is logged as an error and the
   API keeps starting. *Rejected:* promoting a configured
   username to Admin at first sign-in — whoever registers that name first becomes admin.
   **Cutover:** existing prod user rows (the admin, §1d test accounts) have random GUIDs that will never
   match a `sub`; with no real users, reset the user-linked prod data at cutover (confirm at the time).
6. **The frontend calls Cognito directly, not through Amplify.** `SignUp`, `ConfirmSignUp`,
   `InitiateAuth` (`USER_PASSWORD_AUTH`) and the rest are unsigned JSON POSTs to the `cognito-idp`
   endpoint, made from the existing `AuthService` — the interceptor and the `HttpTestingController` specs
   keep working, no new dependency. Accepted trade-off: the password travels over TLS to Cognito (as it
   does to our API today) instead of Amplify's SRP flow. Refresh, if wanted, is one
   `REFRESH_TOKEN_AUTH` call; today's behaviour (24h token, sign in again) needs none.
7. **Role and permissions are loaded per request, not carried in the token.** `BlockedUserMiddleware`'s
   Redis-cached lookup (`IUserBlockCache`, invalidated by the admin endpoint) grows into one cached
   "access" entry — blocked, role, permissions — invalidated the same way on a role change, so role
   changes take effect on the next request instead of at token expiry. The authorization policies
   (`AuthPolicies.cs`) read permissions from that entry instead of the `marquee:perm` claim
   `JwtTokenService` writes today. The frontend's `jwt.ts` gets permissions from an API response instead
   of decoding them from the token. *Rejected:* a Pre Token
   Generation Lambda — it would need to reach Postgres, the same problem as decision 2.
   *As built:* the middleware and cache became `UserAccessMiddleware` and `IUserAccessCache` (#109);
   the frontend's `jwt.ts` went with the move to Cognito sign-in (#111), and `JwtTokenService` with
   the old auth (#112).
8. **Local development runs `cognito-local`** (Docker image `jagregory/cognito-local`) in
   `docker-compose.yml`: offline, no AWS credentials, codes printed in its log instead of emailed. It
   supports the sign-in flow decision 6 uses (`USER_PASSWORD_AUTH`), the sign-up / confirm / reset /
   admin operations, refresh, and a JWKS endpoint, so only the issuer URL differs from prod. Fidelity is
   "good enough for local development" — no password policy, no resend, case-sensitive usernames among
   the gaps recorded in §2b — so behaviour is verified once against the real pool (below).
   *Rejected:* a dev pool in AWS (internet + `aws login`'s 12h credentials on every run, real email
   addresses, quota) and keeping the old password auth in Development (two auth implementations).
9. **The API accepts Cognito access tokens, not ID tokens.** An access token is the credential meant for
   calling an API; the ID token describes the signed-in user to the frontend. Access tokens carry no
   `aud` claim, so `ValidateAudience` is turned off and the API checks the equivalent itself:
   `client_id` must be our app client and `token_use` must be `access` (which also refuses an ID token
   presented in its place). Issuer and signature are validated against the pool's JWKS as usual. The
   same validation applies to the SignalR hub, which receives the token in the `access_token` query
   string. The email the row needs comes from `GetUser` (decision 2), which works because
   `USER_PASSWORD_AUTH` access tokens carry the `aws.cognito.signin.user.admin` scope — keep it on the
   app client. *Rejected:* accepting ID tokens — they carry `email` and an `aud`, but they are not
   meant as an API credential.

**Password policy — only what Cognito enforces.** The browser calls Cognito directly, so no server code
ever sees the password and no Lambda trigger receives it: a rule Cognito cannot express could only be a
frontend hint, bypassable by calling `cognito-idp` by hand. Rather than keep unenforced hints, the policy
**is** the pool's: **at least 10 characters, at least one number.** #27's repeated-character,
contains-username/email and common-password rules, and its any-alphabet letter rule (Cognito can only
require basic Latin A–Z), are removed from `PasswordPolicy` and the UI. Cognito's *default* policy also
requires upper case, lower case and a symbol, so the pool must set those three to **false** explicitly.
The documented `Admin__Password` requirement (SSM secrets, above: ≥10 + a digit) must satisfy the pool,
or decision 5's seeding fails — nothing at startup checks it today beyond presence. *Rejected:*
routing every auth call through the API with a client-secret app client to keep #27 server-side — it
reverses decision 6 to protect only an account owner from their own choice.

### Domain rule changes (#112)

The replacement wording for CLAUDE.md §3 (User), §4.1 and §4.2 was applied in #112; CLAUDE.md is
now the source. The proposed text lives in that PR and in this file's history.

### Cutover (#113)

The one merge of `feat/phase2-cognito` into `main` (PR #125, `06cac2e`, 2026-10-07). Its ordered
checklist was executed and is kept in #113, PR #125 and this file's history; why it cannot be
rolled back with a tag redeploy is in the [runbook](docs/deployment/runbook.md#rollback).

### Verification against the real pool (once, after the first prod deploy)

What `cognito-local` cannot stand in for was checked once against the real pool after the cutover.
Recorded in the [verification log](docs/deployment/verification-log.md#phase-2-verification-against-the-real-pool) (from 2026-10-07).

### 2a. User pool (#107 — deployed 2026-10-04)

`MarqueeAuthStack` (`infra/MarqueeAuthStack.cs`), separate from `MarqueeStack` because the pool is the
only copy of users' password hashes — they cannot be exported, so losing it means everyone resets.
`MarqueeStack` can be torn down and rebuilt without touching it. Stack termination protection on; the
pool has deletion protection and a `RETAIN` removal policy. Deploy order: `MarqueeAuthStack` before
`MarqueeStack`, which reads the pool ARN for the host role's grant. CDK wires that ARN through
`Fn::GetStackOutput` (resolved at deploy time) rather than a CloudFormation export, which is why the auth
stack has an auto-named `PublishOutputFnGetAtt…` output; its name derives from the pool's construct id
(`Users`), so renaming that id renames the output. The pool name, the `email` alias, case-insensitive
usernames and the required `email` attribute are **immutable after creation** — changing any of them
replaces the pool, which deletion protection and `RETAIN` exist to stop.

- **Pool** `ca-central-1_4mv04X3ap`, issuer `https://cognito-idp.ca-central-1.amazonaws.com/ca-central-1_4mv04X3ap`.
  Username sign-in, case-insensitive, `email` alias (Cognito enforces username uniqueness at sign-up and
  email uniqueness among confirmed users only); code verification ("Your Marquee code is {####}");
  recovery by verified email only; password policy min 10 + numbers, upper/lower/symbols off. The
  verification email ("Your Marquee code is {####}.") names no expiry, because Cognito sends the same
  template for password resets (1h) as for sign-up (24h).
- **Feature plan: Lite** — 10,000 MAU/month always free, then $0.0055/MAU. Essentials (the default for
  new pools, $0.015/MAU after the same free tier) adds passwordless and password-history features this
  app does not use.
- **Email: Cognito's built-in sender, 50 messages per day per AWS account, not adjustable**, from
  `no-reply@verificationemail.com`. Every sign-up, resend and reset counts. Phase 3's SES lifts it.
- **App client** `marquee-web` (`20ljh9mr0pc2hpjbriduqqtp1t`): public, no secret,
  `USER_PASSWORD_AUTH` + refresh, OAuth/hosted UI disabled, access and ID tokens 24h (Cognito's maximum),
  user-existence errors suppressed. The refresh token keeps CDK's default **30 days** — longer than
  today's "24h, then sign in again". It only matters if the frontend stores it; whether to (and whether
  to shorten it) is #111's call.
- **Grants**: the host role has `AdminCreateUser`, `AdminGetUser`, `AdminSetUserPassword` on the pool
  ARN only; the deploy role can `DescribeStacks` on `MarqueeAuthStack` to read its outputs.
  The seeder calls those from **inside the API container**, which reaches the instance role through
  IMDSv2 only because the instance's metadata hop limit is 2 (one hop for the instance, one for Docker's
  bridge). At 1, every AWS call from the container fails.
  **`MarqueeStack` states it (#128)** with a property override on the instance's own `MetadataOptions`
  (`HttpTokens` required, `HttpPutResponseHopLimit` 2), leaving `RequireImdsv2`'s launch template alone.
  The obvious route does not work: CDK refuses `RequireImdsv2` together with the individual metadata
  options, and dropping `RequireImdsv2` for them drops the launch template, which `cdk diff` (2026-10-07)
  reported as **replacing the host**, the known-fragile operation. An instance's `MetadataOptions`
  update in place: the change-set `cdk diff` of 2026-10-25 shows only that property changing, with no
  replacement. Until #128 is deployed the value still comes from the pinned AMI's default.

Recorded in the [verification log](docs/deployment/verification-log.md#2a-user-pool) (deployed and checked 2026-10-04).

### 2b. Local stand-in: `cognito-local` (#108)

The `cognito` service in `docker-compose.yml` (decision 8). Image `jagregory/cognito-local`, pinned by
digest because the project publishes no version tags (the pinned build is from 2026-05-21). The pool
`local_marquee` and client `marquee-local-web` are seeded from `docker/cognito-local/db` with fixed ids,
so local config can name them; users live in the `marquee-cognitodata` volume — in the same file as
the pool, so a seed edit only reaches a machine after `docker volume rm marquee_marquee-cognitodata`.
`config.json` turns off the emulator's default of email-as-username and sets the issuer to
`http://localhost:9229`, so tokens carry `iss: http://localhost:9229/local_marquee` and JwtBearer finds
`/.well-known/openid-configuration` and the JWKS under it. `CODE=123456` makes every code the same.

**Matches the real pool** (checked 2026-10-04 with the AWS CLI): `SignUp`; `UserNotConfirmedException`
on signing in unconfirmed; `CodeMismatchException` on a wrong code; `ConfirmSignUp`;
`USER_PASSWORD_AUTH` and `REFRESH_TOKEN_AUTH`; access-token claims `iss`, `sub`, `client_id`,
`token_use: access`, `username`, `scope: aws.cognito.signin.user.admin` and no `aud` (decision 9);
`GetUser` with the access token returns the email (decision 2); `ForgotPassword` +
`ConfirmForgotPassword`; `AdminCreateUser` (`UsernameExistsException` on a repeat), `AdminGetUser`,
`AdminSetUserPassword --permanent` → `CONFIRMED` (decision 5's seeder); `ListUsers` with
`cognito:user_status = "UNCONFIRMED"` (#30).

**Differs — covered only by the real-pool checklist:**

- **No password policy.** `abc` is accepted. Only the real pool (and the frontend's hints) enforce it;
  the policy in the seed file is there to mirror the real pool, not because the emulator reads it.
- **Usernames are case-sensitive**, despite `CaseSensitive: false`: `ALICE` signs up as a second user
  beside `alice`, and signing in must match the case used at sign-up.
- **No sign-in by email alias** — only the username works.
- **No `ResendConfirmationCode`** (`Unsupported`). With `CODE` fixed, the original code always works, so
  locally "resend" is a no-op the UI must survive, not a path to exercise.
- **`email_verified` stays `false`** after confirmation, unlike the real pool's auto-verification.
- `InitiateAuth` responses carry no `ExpiresIn`; read expiry from the token's `exp`.
- **A wrong password at sign-in is `InvalidPasswordException`**, not the real pool's
  `NotAuthorizedException`. The frontend reads any password refused at sign-in as a wrong one (#111).
- **Confirming an already-confirmed account is `ExpiredCodeException`**, where the real pool says
  `NotAuthorizedException` — which the frontend treats as "already confirmed, carry on" (#111). Locally
  the person sees "code expired" instead; harmless, and only reachable by confirming twice.
- **Accepts an email-shaped username.** The real pool refuses one (`InvalidParameterException`), since
  email is a sign-in alias; the register form stops it before any request either way (#111).

### 2c. Unconfirmed-user cleanup (#30 — deployed 2026-10-14)

`MarqueeCleanupStack` (`infra/MarqueeCleanupStack.cs`) deletes Cognito users who never confirmed their
email, once they are more than 7 days old (decision 4 above). Design and plan:
`docs/superpowers/specs/2026-10-14-unconfirmed-user-cleanup-design.md` and
`docs/superpowers/plans/2026-10-14-unconfirmed-user-cleanup.md`. No VPC, about $0 a month (Scheduler
and Lambda free tiers).

- **Lambda `unconfirmed-user-cleanup`** (Node.js 22, 128 MB, 1 min, logs kept one month), code in
  `infra/lambda/unconfirmed-cleanup/`. It lists `UNCONFIRMED` users with `ListUsers` and deletes those
  older than `MIN_AGE_DAYS` (7) with `AdminDeleteUser`. Its role holds only those two Cognito actions,
  on the pool ARN (plus the standard basic-execution policy for writing its logs). `CONFIRMED`,
  `FORCE_CHANGE_PASSWORD` and newer users are never touched (unit-tested in `select.test.mjs`, which CI
  runs with `node --test "lambda/**/*.test.mjs"`).
- **Only an explicit `{"dryRun": false}` deletes.** Any other input — `{}`, a misspelled key, a console
  test with no body — is a dry run that reports what it would delete and deletes nothing. The rule was
  inverted after a console test with a misspelled key ran for real; the safe failure is now a nightly run
  that silently deletes nothing, which the logs show.
- **EventBridge Scheduler** runs it daily at 04:00 America/Toronto with `{"dryRun": false}`; its role can
  only invoke this function.
- **Alarm**: Lambda `Errors` ≥ 1 within an hour → the stack's own SNS topic → `alertEmail`. The same
  caveat as `MarqueeStack`'s alarms applies (§1b): **confirm the subscription email within 3 days of
  the deploy that creates it**, or SNS silently deletes the subscription and the alarm emails nobody.
- **Deploys by hand**, like the other stacks (it is not part of the Deploy workflow; commands in the
  [runbook](docs/deployment/runbook.md#cdk-credentials-diff-and-deploy)).
  The pool id comes from `MarqueeAuthStack`, so this stack depends on it; the first deploy added an
  auto-named output to the auth stack (§2a explains why), so deploy both from the same commit.

Recorded in the [verification log](docs/deployment/verification-log.md#2c-unconfirmed-user-cleanup-30) (deploy checks 2026-10-14; the live deletion run is still open).

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

## Measured capacity (phase 1 instance)

Measured 2026-09-29 with `tests/Marquee.LoadTests/capacity.js` from one machine through CloudFront,
against the `t3.small` running all five containers, host sampled every ~10 s (`docker stats`,
`/proc/loadavg`, `/proc/meminfo`).

| Load | Result | Host at peak |
|---|---|---|
| **1,000 held-open SignalR WebSockets**, ramped over 3 min, held 2 min | All 1,000 connected and stayed up (none dropped early); handshake p95 259 ms, negotiate p95 57 ms | API memory 118 → 220 MB (**~100 KB per connection**); host free memory 1,086 → 933 MB, no swap; load average ≤ 0.6 |
| **Page-load reads** (`/today` + `/active`), ramped to 200 page loads/s = **~400 req/s** | 54,250 requests, **0 failed**; p50 33 ms, p95 56 ms, p99 100 ms | API ~67% CPU, Postgres ~29%; **load average 3.85 on 2 vCPUs** — work queueing; memory flat |

- **CPU strains first, not memory.** At ~400 req/s the box was past its two cores while latency was
  still good — the next step up is where latency would climb. Memory would give out only around
  **8,000–9,000** held connections at the measured cost.
- **Every page-load read hits Postgres** (~30% CPU at peak): the obvious first optimisation is caching
  the shared part of `/today` and `/active` in Redis — #94, a follow-up rather than done here.
- **Clap throughput at scale cannot be measured in production yet.** With one confirmed user, §4.1
  puts the threshold at its 30–50 floor, so a Premiere opens after a few dozen claps. Iteration 2's
  local numbers (`docs/concurrency-findings.md`) remain the reference for the counter path; the Redis
  counter was never the expected bottleneck. Tracked as #98, to run once thresholds are off the floor.
- CPU credits (`standard`) are the real budget under sustained load: a `t3.small` earns ~24/hour and
  held 322 during the run. A sustained ~400 req/s would drain them and throttle the host to its 20%
  per-vCPU baseline — the `CPUCreditBalance` alarm is the warning.
- If it strains: a bigger instance first (or `unlimited` credits, accepting the billing), splitting
  services apart last.

## Open decisions

- **Timezone** for `TZ` (§4.4's "local time").
- **Instance family** — `t3.small` (x86, no surprises) or `t4g.small` (Graviton, cheaper, but images
  must be built for arm64).

## Legal / licensing — resolve before inviting real users

1. **TMDB API terms**: the free API is for non-commercial use; commercial use needs written permission
   and is judged case by case (revenue, branding, scale, public availability). Email
   api@themoviedb.org before inviting users — describe it plainly (no ads or monetisation,
   hobby/portfolio, low hundreds of users) and get the answer in writing.
2. **"Marquee" trademark**: a generic word, but in use by entertainment businesses (e.g. the Marquee
   nightclub chain). Run a USPTO search in entertainment/software classes before a public-facing name
   and domain are chosen in phase 3.
