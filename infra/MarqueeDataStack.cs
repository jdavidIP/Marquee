using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.RDS;
using Constructs;

namespace Marquee.Cdk;

// The database (DEPLOYMENT.md § Phase 3, #160): RDS for PostgreSQL in the host's VPC, reachable from the
// host and nothing else. Design: docs/superpowers/specs/2026-10-26-rds-stack-design.md.
//
// Its own stack for the same reason as MarqueeAuthStack: it holds data the other stacks do not own, so
// MarqueeStack can be torn down and rebuilt without touching it.
public class MarqueeDataStack : Stack
{
    // The password the Postgres container already uses, so moving the app across (#161) changes only the
    // host name. Pinned to a version: an unversioned reference never changes the template, so a redeploy
    // after rotating the parameter would leave the old password in place. Rotating means writing a new
    // parameter version, bumping this, deploying this stack, then redeploying the app (runbook).
    private const string PasswordParameter = "/marquee/prod/POSTGRES_PASSWORD";
    private const string PasswordParameterVersion = "1";

    public MarqueeDataStack(Construct scope, string id, IVpc vpc, ISecurityGroup hostSecurityGroup, IStackProps props)
        : base(scope, id, props)
    {
        // An RDS subnet group needs two AZs even for a single-AZ instance. Added here with fixed CIDRs
        // rather than as a subnet configuration on MarqueeStack's Vpc, which can renumber the existing
        // public subnet and replace the host. Each Subnet gets its own route table with only the local
        // route: no internet in or out.
        var subnets = new ISubnet[]
        {
            new Subnet(this, "DbSubnetA", new SubnetProps
            {
                VpcId = vpc.VpcId,
                AvailabilityZone = "ca-central-1a",
                CidrBlock = "10.0.100.0/24",
            }),
            new Subnet(this, "DbSubnetB", new SubnetProps
            {
                VpcId = vpc.VpcId,
                AvailabilityZone = "ca-central-1b",
                CidrBlock = "10.0.101.0/24",
            }),
        };

        var subnetGroup = new SubnetGroup(this, "DbSubnets", new SubnetGroupProps
        {
            Vpc = vpc,
            VpcSubnets = new SubnetSelection { Subnets = subnets },
            Description = "Marquee database: isolated subnets in two AZs",
        });

        // The rule lives on this group, in this stack. Opening the path from the host's side instead
        // (connections.allowTo) would add a rule to MarqueeStack that refers back here: a cycle.
        var dbSg = new SecurityGroup(this, "DbSg", new SecurityGroupProps
        {
            Vpc = vpc,
            Description = "Marquee database: Postgres from the app host only",
            AllowAllOutbound = false,
        });
        dbSg.AddIngressRule(hostSecurityGroup, Port.Tcp(5432), "Marquee app host");

        var engine = DatabaseInstanceEngine.Postgres(new PostgresInstanceEngineProps
        {
            Version = PostgresEngineVersion.Of("16.15", "16"),
        });

        var parameters = new ParameterGroup(this, "DbParameters", new ParameterGroupProps
        {
            Engine = engine,
            Description = "Marquee: SSL only, Toronto time",
            Parameters = new Dictionary<string, string>
            {
                ["rds.force_ssl"] = "1",
                // Matches the container, whose timezone was fixed at initdb from TZ, so the move changes
                // nothing observable.
                ["timezone"] = "America/Toronto",
            },
        });

        var db = new DatabaseInstance(this, "Db", new DatabaseInstanceProps
        {
            Engine = engine,
            AutoMinorVersionUpgrade = true,
            InstanceType = Amazon.CDK.AWS.EC2.InstanceType.Of(InstanceClass.BURSTABLE4_GRAVITON, InstanceSize.MICRO),
            Vpc = vpc,
            SubnetGroup = subnetGroup,
            SecurityGroups = new[] { dbSg },
            ParameterGroup = parameters,
            Credentials = Credentials.FromPassword("marquee", SecretValue.SsmSecure(PasswordParameter, PasswordParameterVersion)),
            DatabaseName = "marquee",
            AllocatedStorage = 20,
            // Grows instead of filling up; costs nothing until used.
            MaxAllocatedStorage = 40,
            StorageType = StorageType.GP3,
            StorageEncrypted = true,
            MultiAz = false,
            PubliclyAccessible = false,
            // 08:00 UTC is 03:00–04:00 in Toronto, the same quiet window as the host's EBS snapshots.
            BackupRetention = Duration.Days(7),
            PreferredBackupWindow = "08:00-08:30",
            PreferredMaintenanceWindow = "mon:08:30-mon:09:00",
            // Deleting is refused while protection is on; a delete that does go through takes a final
            // snapshot first, rather than leaving the instance running unowned (RETAIN).
            DeletionProtection = true,
            RemovalPolicy = RemovalPolicy.SNAPSHOT,
        });

        new CfnOutput(this, "DbEndpoint", new CfnOutputProps { Value = db.DbInstanceEndpointAddress });
        new CfnOutput(this, "DbPort", new CfnOutputProps { Value = db.DbInstanceEndpointPort });
        new CfnOutput(this, "DbSecurityGroupId", new CfnOutputProps { Value = dbSg.SecurityGroupId });
    }
}
