# RDS for PostgreSQL in its own stack (#160)

Status: design agreed with the owner on 2026-10-26. This issue provisions the database only. Moving the
app onto it, and retiring the Postgres container, is #161.

## Intent

Postgres runs as a container on the app host (`docker-compose.prod.yml`). Its data sits on the host's
EBS data volume, and its only backup is that volume's daily crash-consistent snapshot. A managed
database gives automated backups and point-in-time recovery, and separates the database's lifecycle
from the host's. It is also a deliberate AWS learning goal (DEPLOYMENT.md § Phase 3); at fewer than
100 users it is not needed for capacity.

## Cost (checked 2026-10-26)

- The account is on the credit-based Free Plan (created 2026-09-25): $141.19 of credits left on
  2026-10-26, plan expiry 2027-03-24.
- AWS lists `db.t3.micro` and `db.t4g.micro` on the Free Plan for PostgreSQL, paid from the shared
  credits. There is no separate 750-hour allowance for accounts created after 15 July 2025.
- Estimate, unverified: about $12/month for the instance and about $2/month for 20 GB of gp3, so
  roughly $14–15/month. Backups up to the database's size are normally free.
- An RDS instance can be stopped for at most 7 days before AWS starts it again, so it effectively bills
  around the clock, unlike the host the owner stops when idle.
- With everything always on (~$37/month), the credits run out around mid-February 2027, about five
  weeks before the plan would end. With the host stopped when idle, they last to expiry. The owner
  accepted this.

## Goals

- A PostgreSQL 16 instance in a new CDK stack, reachable from the app host and nothing else.
- Automated backups and a recorded, tested restore story (the restore test itself is #161).
- No change to the host, its subnet or anything else in `MarqueeStack` beyond new exports.

## Non-goals

- Pointing the app at it, removing the Postgres container, changing the deploy pipeline (#161).
- Multi-AZ, read replicas, Aurora, RDS Proxy.
- Performance Insights, enhanced monitoring, CloudWatch log export (all add cost).
- New CloudWatch alarms (would need the SNS topic exported from `MarqueeStack`; can come with #161).

## Anti-goals

- Anything that replaces the host, its subnet, its security group or its data volume.
- A database reachable from the internet.
- A deletion that loses data without a snapshot.

## What exists today

- VPC `10.0.0.0/16`, one public subnet `10.0.0.0/24` in `ca-central-1a`, owned by `MarqueeStack`
  (`infra/MarqueeStack.cs`). The region's AZs are `ca-central-1a`, `1b` and `1d`.
- The host's security group admits only CloudFront's origin-facing prefix list on port 80; outbound is open.
- The app connects as user `marquee` to database `marquee`, password from the SSM SecureString
  `/marquee/prod/POSTGRES_PASSWORD`, through the compose network host `postgres`.
- The container runs with `TZ: America/Toronto`, which fixed Postgres's `timezone` setting at initdb.
  No application SQL depends on the database timezone (no `now()`, `AT TIME ZONE` or date functions in
  `src/`).
- `MarqueeAuthStack` is the existing pattern for a separate stateful stack: its own file, termination
  protection, a construct passed into `MarqueeStack` through its constructor.

## Decisions (made by the owner)

1. **Its own stack**, `MarqueeDataStack`, in `infra/MarqueeDataStack.cs`, created in `infra/Program.cs`
   with `TerminationProtection = true`.
2. **Removal policy `SNAPSHOT`**, not `RETAIN`: deleting the stack takes a final snapshot and removes the
   instance, rather than leaving it running and billing. Deletion protection stays on, so an accidental
   delete is refused first anyway.
3. **Reuse the existing password**: the master password is the existing
   `/marquee/prod/POSTGRES_PASSWORD`, resolved at deploy time (a CloudFormation `ssm-secure` dynamic
   reference, e.g. `SecretValue.SsmSecure`). User `marquee`, database `marquee`. After #161 the
   connection string changes only in its host name.
4. **SSM, not Secrets Manager** (avoids its monthly charge).
5. **Isolated subnets added without touching the `Vpc` construct** (see below).
6. **Backup retention: 7 days, with 1 day as an accepted fallback.** The Free Plan may cap automated
   backup retention at 1 day; no official AWS page confirmed or ruled this out on 2026-10-26. Deploy
   with 7. If RDS refuses it (CloudFormation rolls the whole create back, so nothing is left behind),
   set 1, redeploy, and record in DEPLOYMENT.md which one held. One day still gives point-in-time
   recovery over the last 24 hours, plus the final snapshot on delete.

## Calls made in review (not the owner's; change freely)

- **The password parameter is pinned to a version.** An unversioned `ssm-secure` reference never
  changes the template text, so CloudFormation sees nothing to update and a redeploy would not rotate
  anything. The stack names a specific parameter version (a constant in the stack file, next to the
  parameter name). Rotating the password is: write a new parameter version, bump the constant, deploy
  this stack (RDS applies a new master password straight away), then redeploy the app so its `.env`
  picks it up. That sequence goes in `docs/deployment/runbook.md`.
- **The current password must meet RDS's rules** (8–128 printable ASCII characters, no `/`, `"`, `@` or
  space), which the container never enforced. The owner verifies this before the first deploy, without
  printing the value. If it fails, rotate it first (the container's password changes with
  `ALTER USER`, then the parameter), still before this stack is deployed.

## The design

**Networking**

- Two subnets defined directly in the existing VPC with explicit CIDRs: `10.0.100.0/24` in
  `ca-central-1a` and `10.0.101.0/24` in `ca-central-1b`. An RDS subnet group needs two AZs even for a
  single-AZ instance.
- Each subnet gets a route table with no route beyond the VPC's local one: isolated, no internet.
- Not done by adding a subnet configuration to the existing `Vpc` construct, which can renumber the
  existing subnet and replace the host.
- A security group for the database admitting TCP 5432 from the host's security group only.

**The instance**

- PostgreSQL 16 (latest 16.x minor available in `ca-central-1`; automatic minor upgrades on), `db.t4g.micro`,
  single-AZ, not publicly accessible, storage encrypted.
- 20 GB gp3, storage auto-scaling up to 40 GB (no cost until used; avoids a full-disk outage).
- Automated backups kept 7 days (1 if the Free Plan refuses 7, see decision 6), backup window 08:00–08:30 UTC (03:00–04:30 in Toronto, the same quiet
  window as today's EBS snapshots, outside the 07:00–23:00 Premiere window). Maintenance window right after,
  for example Monday 08:30–09:00 UTC.
- Deletion protection on; removal policy `SNAPSHOT`.

**Parameter group** (PostgreSQL 16 family)

- `rds.force_ssl = 1`.
- `timezone = America/Toronto`, matching the container exactly so #161 changes nothing observable.

**Cross-stack wiring**

- `MarqueeStack` exposes its `Vpc` and host `SecurityGroup` as public properties; `Program.cs` passes them
  into `MarqueeDataStack`'s constructor, as it already passes `auth.UserPool` into `MarqueeStack`.
- This creates CloudFormation exports from `MarqueeStack`. While the data stack imports them,
  `MarqueeStack` cannot replace or remove that VPC or that security group. That is wanted for a database,
  but it is a new constraint on future `MarqueeStack` changes, worth one line in DEPLOYMENT.md.
- **Superseded during the build (Task 1 ruling):** CDK used plain outputs read with `Fn::GetStackOutput`, not
  exports, so there is no such lock. DEPLOYMENT.md §3a says what a replacement would actually do.

**Outputs**

- Endpoint address, port, and the database security group's id. The deploy role's
  `cloudformation:DescribeStacks` on this stack is added in #161, when the pipeline first needs it.

## Constraints

- CDK in C#, `infra/` never part of `Marquee.sln` (CI builds the solution by name).
- `cdk synth` runs in CI without AWS credentials: no lookups (`Vpc.FromLookup` and similar). Passing
  constructs between stacks and the `ssm-secure` reference both synthesize offline.
- The CDK CLI cannot read `aws login` credentials directly: export them first
  (`aws configure export-credentials --format env`). Every `cdk` command takes `-c alertEmail=<address>`.
- The deploy role never deploys infrastructure stacks; the owner runs `cdk deploy` by hand.
- Region `ca-central-1`.

## Deploy and verification

0. The owner confirms the password meets RDS's rules (see "Calls made in review").
1. `cdk diff MarqueeStack MarqueeDataStack`. `MarqueeStack` must show only new exports (outputs) and no
   resource replaced or modified. Any replacement: stop and rethink.
2. The owner deploys `MarqueeStack` (exports only), then `MarqueeDataStack`.
3. From the host through Session Manager, with a throwaway container:
   `docker run --rm postgres:16 psql "host=<endpoint> dbname=marquee user=marquee sslmode=require"`
   connects with the existing password, and `SHOW timezone;` reads `America/Toronto`. A connection
   with `sslmode=disable` is refused.
4. From outside the VPC (the owner's laptop) the endpoint does not connect: it resolves to a private
   address and nothing routes to it.
5. DEPLOYMENT.md § Phase 3 records the settings, the cost estimate, the backup and point-in-time-recovery
   story, and the new cross-stack constraint. `docs/deployment/verification-log.md` gets dated entries for
   steps 1–4.

## Left to the builder

- Construct choices and the exact CDK API calls (`DatabaseInstance` with a `SubnetGroup` over the two
  subnets, `ParameterGroup`, `SecretValue.SsmSecure` or an equivalent), as long as the synthesized template
  matches the design.
- The exact maintenance-window day.
- Resource and construct ids, and the CDK `PostgresEngineVersion` constant for 16.x.
- Comment wording in the stack file, in the style of the existing stacks (why, not what).
