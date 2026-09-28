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
   nothing to copy. Secrets and endpoints come from environment variables (`Jwt__Key`,
   `ConnectionStrings__Postgres`, …), and in Production the API and Worker refuse to start if any
   key whose default is a local-dev value is missing (`RequireKeys`) — notably `Tmdb:ApiKey` (else the
   offline stub) and `Admin:Password` (else the repository's dev password on the seeded admin).
4. **Forwarded headers.** `ForwardedHeaders` for `X-Forwarded-For` only (nothing reads the scheme),
   `ForwardLimit = 1`, switched on by `ForwardedHeaders__Enabled` in the prod compose file. Trusting the
   immediate peer is safe *only* because the security group admits nothing but CloudFront — that
   dependency is recorded where it is configured. This fixes the shared rate-limit bucket.
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
- A deploy role trusted only for `repo:jdavidIP@90657602/Marquee@1311474951:ref:refs/heads/main` (and a
  read-only `cdk diff` role for PRs if wanted), scoped to: push to the ECR repos, write the S3 site
  bucket, create CloudFront invalidations, `ssm:SendCommand` to the one instance, read the artifacts
  bucket. The subject is in GitHub's immutable format, with the owner and repo suffixed by their
  numeric ids, because that is what this repo's OIDC tokens carry.
- Separate stack so a deploy can never edit the permissions of the role performing it.
- The deploy role is created with **no permissions**. Each stack that owns a resource grants the role
  access to that resource's ARN — never a broad `*`.

Running it, signed in with `aws login` and with `aws configure set region ca-central-1` done once, from
`infra/` in Git Bash:

```
eval "$(aws configure export-credentials --format env)"   # per shell; see below
cdk bootstrap aws://<account-id>/ca-central-1              # once per account and region
cdk deploy MarqueeCiStack -c alertEmail=<address>
cdk deploy MarqueeStack -c alertEmail=<address>
```

The CDK CLI cannot yet read the `login_session` credentials `aws login` stores, so the first line hands
it short-lived credentials through environment variables for that shell. In PowerShell the equivalent
is `aws configure export-credentials --format powershell | Out-String | Invoke-Expression`. Claude
Code's `!` prompt cannot answer `cdk deploy`'s approval question: review `cdk diff` first, then pass
`--require-approval never`.

The account comes from the signed-in CLI and the alert address from the command line, so neither is
committed; synth refuses to run without `alertEmail`, which receives both the budget and the
CloudWatch alarm emails. The stack outputs `DeployRoleArn` for the workflows.

**`MarqueeStack`** — everything the app runs on:
- **VPC**: 1 AZ, public subnets only, **no NAT gateway** (~$32/mo idle for nothing we need).
- **EC2**: Amazon Linux 2023, `t3.small` (2 vCPU / 2 GB — five containers do not fit in 1 GB), in
  `ca-central-1a`.
  - **CPU credits `standard`**, not the T3 default `unlimited`: sustained load above the 20% baseline
    throttles the host instead of billing surplus credits against the Free plan.
  - **AMI pinned**, not looked up fresh on every synth: a new AMI version would otherwise *replace*
    the instance on the next deploy. Moving to a newer one is a deliberate edit in `MarqueeStack.cs`.
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
  because the prefix list counts as ~55 rules against a group's rule quota.
- **ECR**: `marquee-api` and `marquee-worker`, scan on push, lifecycle rule keeping the last 10 images.
- **Artifacts bucket**: private, TLS-only, objects expire after 30 days. Holds each deploy's
  `docker-compose.prod.yml` and `deploy.sh`.
- **Deploy role grants** (on `marquee-github-deploy`): push to the two ECR repos, put objects in the
  artifacts bucket, `ssm:SendCommand` on this instance with `AWS-RunShellScript`. `MarqueeStack` is
  deployed by hand, never by that role, so the role still cannot change its own permissions.
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
- **SSM Parameter Store** (standard tier, free): `SecureString`s under `/marquee/prod/`, created by
  hand once (CDK should not hold secret values), named after the `.env` key each becomes:
  - `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD` — random; the app never sees anything else.
  - `Jwt__Key` — random, **at least 32 characters** (the API checks this at startup and refuses to
    start below it).
  - `Tmdb__ApiKey` — the TMDB **v3 API key**, not the read access token.
  - `Admin__Password` — at least 10 characters with a digit (the app's own password policy); this is
    the seeded admin's sign-in password, so keep it somewhere you can find it again.
- **CloudWatch**: one log group, `/marquee/prod`, with a stream per container, 14-day retention.
  Two alarms, both emailing `alertEmail` through an SNS topic (confirm the subscription email once):
  - EC2 system status check failed → the instance is **recovered** onto healthy hardware, keeping its
    id, Elastic IP and volumes.
  - CPU credit balance below 20 → about to be throttled to baseline. Under `standard` credits this is
    the meaningful CPU signal; utilization cannot show it, because it drops once throttled. T3
    `standard` launches with no credits, so it fires for roughly the first hour after the instance is
    created.
- **AWS Budgets**: monthly budget with email alerts at 50/80/100%. Set up **first**, before anything
  else is deployed.
- Optional: `cdk-nag` (AwsSolutions pack) in synth, with each suppression justified in code.

### 1b-ops. Manual deploy, until 1c exists

`MarqueeStack`'s "Done when" checks need something running on the host before the pipeline in 1c is
built. Until then, deploy by hand, from `infra/` with the CLI signed in:

```
aws cloudformation describe-stacks --stack-name MarqueeStack --query 'Stacks[0].Outputs'
```

gives `HostInstanceId`, `HostPublicDns` and `ArtifactsBucket`. Then, from the repository root:

```
TAG=$(git rev-parse --short HEAD)
REGISTRY=<account-id>.dkr.ecr.ca-central-1.amazonaws.com   # aws sts get-caller-identity for the id
aws ecr get-login-password --region ca-central-1 | docker login --username AWS --password-stdin $REGISTRY
docker build --target api -t $REGISTRY/marquee-api:$TAG . && docker push $REGISTRY/marquee-api:$TAG
docker build --target worker -t $REGISTRY/marquee-worker:$TAG . && docker push $REGISTRY/marquee-worker:$TAG
aws s3 cp docker-compose.prod.yml s3://<ArtifactsBucket>/$TAG/docker-compose.prod.yml
```

Then on the host — no SSH, everything through SSM Run Command (`AWS-RunShellScript`) or, for a shell,
`aws ssm start-session --target <HostInstanceId>` (needs the Session Manager plugin locally):

```bash
mkdir -p /opt/marquee && cd /opt/marquee
aws s3 cp s3://<ArtifactsBucket>/$TAG/docker-compose.prod.yml .
umask 077
aws ssm get-parameters-by-path --path /marquee/prod --with-decryption --query 'Parameters[].[Name,Value]' \
  --output text | while IFS=$'\t' read -r name value; do echo "$(basename "$name")=$value"; done > .env
echo "REGISTRY=$REGISTRY" >> .env
echo "IMAGE_TAG=$TAG" >> .env
echo "PUBLIC_BASE_URL=<the site's public URL; a placeholder until #76>" >> .env
chmod 600 .env
aws ecr get-login-password --region ca-central-1 | docker login --username AWS --password-stdin $REGISTRY
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml up -d --wait
curl -s -o /dev/null -w '%{http_code}\n' http://localhost/health/ready
```

The `.env` file is written `600`, owned by root; nothing in it is ever echoed back through SSM output.

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

- [x] **2026-09-28** — `docker compose -f docker-compose.prod.yml up -d --wait` on the host: all five
  containers reach `healthy`/running, `/health/ready` returns 200, and the worker's MassTransit
  consumers register against RabbitMQ with no errors.
- [x] **2026-09-28** — Reboot survival: wrote a marker row into Postgres, `aws ec2 reboot-instances`,
  confirmed via `uptime -s` that the host actually rebooted (not just SSM reconnecting), then confirmed
  the data volume and swap remounted from `/etc/fstab`, Docker and all five containers restarted on
  their own (`restart: unless-stopped` survives a daemon restart), `/health/ready` returned 200 again,
  and the marker row was still there with its original timestamp.
- [x] **2026-09-28** — Nothing reachable inbound except from CloudFront: direct TCP connection attempts
  to the host's public DNS name on ports 80, 22, 5432, 6379 and 15672, run from outside AWS, all got no
  response.
- [ ] Restore a snapshot of the data volume into a scratch volume once, to prove backups actually
  restore. Daily snapshot runs 08:00 UTC; first one due 2026-09-29.
- [ ] Clap a Premiere open from two browsers; watch SignalR counts move through CloudFront. Waits on #76
  — there is no CloudFront distribution yet, so this was exercised against the host directly instead.
- [ ] Run the k6 scripts against the CloudFront URL and check them against the capacity estimate below.
  Waits on #76.
- [ ] Confirm the rate limiter sees distinct client IPs (log line per request carries the IP).
  `ForwardedHeaders__Enabled` is only safe once the security group admits nothing but CloudFront (#76);
  unverified until then.

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
