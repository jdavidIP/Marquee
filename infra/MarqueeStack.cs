using Amazon.CDK;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.DLM;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Constructs;

namespace Marquee.Cdk;

// The single app host (DEPLOYMENT.md Â§1b): one EC2 instance running docker-compose.prod.yml, with its
// data on a separate retained volume. Single instance by design â€” see "Constraints the app imposes".
public class MarqueeStack : Stack
{
    private const string Az = "ca-central-1a";

    // Amazon Linux 2023, kernel-default, x86_64, published 2026-09-18. Pinned because a new AMI would
    // replace the instance on the next deploy. To move deliberately, read the current id from
    // /aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-x86_64.
    private const string HostAmi = "ami-0c1364958a673e3af";

    // com.amazonaws.global.cloudfront.origin-facing in ca-central-1 â€” hardcoded rather than looked up so
    // synth needs no AWS access (CI synthesizes without credentials).
    private const string CloudFrontOriginFacing = "pl-38a64351";

    // Not in the AL2023 repositories, so installed as the release binary and checked against its checksum.
    private const string ComposeVersion = "v5.5.1";
    private const string ComposeSha256 = "db1889184726840f75c4f9c001048430d4f25b3be3cb084d3ddd762bc0aed576";

    private const string LogGroupName = "/marquee/prod";
    private const string ParameterPath = "marquee/prod";
    private const string BackupTagKey = "marquee:backup";

    public MarqueeStack(Construct scope, string id, string alertEmail, IStackProps props)
        : base(scope, id, props)
    {
        // Public subnet only: a NAT gateway would cost ~$32/month idle for nothing the host needs.
        var vpc = new Vpc(this, "Vpc", new VpcProps
        {
            AvailabilityZones = new[] { Az },
            NatGateways = 0,
            SubnetConfiguration = new[]
            {
                new SubnetConfiguration { Name = "public", SubnetType = SubnetType.PUBLIC, CidrMask = 24 },
            },
        });

        // Its own group: the prefix list counts as one rule per entry (~55) against the group's rule quota.
        // Nothing else is admitted inbound â€” the forwarded-header trust in docker-compose.prod.yml relies on it.
        var hostSg = new SecurityGroup(this, "HostSg", new SecurityGroupProps
        {
            Vpc = vpc,
            Description = "Marquee host: HTTP from CloudFront only",
            AllowAllOutbound = true,
        });
        hostSg.AddIngressRule(Peer.PrefixList(CloudFrontOriginFacing), Port.Tcp(80), "CloudFront origin-facing");

        var apiRepo = ImageRepository("ApiRepo", "marquee-api");
        var workerRepo = ImageRepository("WorkerRepo", "marquee-worker");

        // Per-deploy docker-compose.prod.yml and deploy.sh, keyed by commit SHA.
        var artifacts = new Bucket(this, "Artifacts", new BucketProps
        {
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            Encryption = BucketEncryption.S3_MANAGED,
            EnforceSSL = true,
            LifecycleRules = new[] { new Amazon.CDK.AWS.S3.LifecycleRule { Expiration = Duration.Days(30) } },
        });

        var logs = new LogGroup(this, "Logs", new LogGroupProps
        {
            LogGroupName = LogGroupName,
            Retention = RetentionDays.TWO_WEEKS,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });

        var hostRole = new Role(this, "HostRole", new RoleProps
        {
            AssumedBy = new ServicePrincipal("ec2.amazonaws.com"),
        });
        apiRepo.GrantPull(hostRole);
        workerRepo.GrantPull(hostRole);
        logs.GrantWrite(hostRole);
        artifacts.GrantRead(hostRole);
        // The secrets are SecureStrings under the AWS-managed aws/ssm key, whose key policy already lets
        // callers in the account decrypt through SSM â€” no kms:Decrypt grant needed.
        hostRole.AddToPrincipalPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = new[] { "ssm:GetParametersByPath" },
            Resources = new[] { ParameterArn(ParameterPath), ParameterArn($"{ParameterPath}/*") },
        }));

        var data = new Amazon.CDK.AWS.EC2.Volume(this, "Data", new VolumeProps
        {
            AvailabilityZone = Az,
            Size = Size.Gibibytes(10),
            VolumeType = EbsDeviceVolumeType.GP3,
            Encrypted = true,
            RemovalPolicy = RemovalPolicy.RETAIN,
        });
        Amazon.CDK.Tags.Of(data).Add(BackupTagKey, "daily");

        var host = new Instance_(this, "Host", new InstanceProps
        {
            Vpc = vpc,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PUBLIC },
            InstanceType = new InstanceType("t3.small"),
            // Standard, not the T3 default "unlimited": sustained CPU above baseline would otherwise bill
            // surplus credits against the Free plan. The host is throttled instead.
            CreditSpecification = CpuCredits.STANDARD,
            MachineImage = MachineImage.GenericLinux(new Dictionary<string, string> { ["ca-central-1"] = HostAmi }),
            SecurityGroup = hostSg,
            Role = hostRole,
            // AmazonSSMManagedInstanceCore: shell access through Session Manager. No key pair, no port 22.
            SsmSessionPermissions = true,
            RequireImdsv2 = true,
            BlockDevices = new[]
            {
                new BlockDevice
                {
                    DeviceName = "/dev/xvda",
                    Volume = BlockDeviceVolume.Ebs(20, new EbsDeviceOptions
                    {
                        Encrypted = true,
                        VolumeType = EbsDeviceVolumeType.GP3,
                    }),
                },
            },
            UserData = HostUserData(data),
        });

        new CfnVolumeAttachment(this, "DataAttachment", new CfnVolumeAttachmentProps
        {
            InstanceId = host.InstanceId,
            VolumeId = data.VolumeId,
            Device = "/dev/sdf",
        });

        // Keeps the public DNS name â€” CloudFront's origin â€” stable across stop/start.
        var hostIp = new CfnEIP(this, "HostIp", new CfnEIPProps { Domain = "vpc", InstanceId = host.InstanceId });

        DailySnapshots();
        Alarms(host, alertEmail);

        // Owning stack grants the deploy role (MarqueeCiStack) access to these resources only. This stack
        // is deployed by hand, never by that role, so the role still cannot edit its own permissions.
        var deployRole = Role.FromRoleName(this, "DeployRole", MarqueeCiStack.DeployRoleName);
        apiRepo.GrantPush(deployRole);
        workerRepo.GrantPush(deployRole);
        artifacts.GrantPut(deployRole);
        deployRole.AddToPrincipalPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = new[] { "ssm:SendCommand" },
            Resources = new[]
            {
                FormatArn(new ArnComponents { Service = "ec2", Resource = "instance", ResourceName = host.InstanceId }),
                FormatArn(new ArnComponents { Service = "ssm", Account = "", Resource = "document", ResourceName = "AWS-RunShellScript" }),
            },
        }));

        new CfnOutput(this, "HostInstanceId", new CfnOutputProps { Value = host.InstanceId });
        new CfnOutput(this, "HostPublicDns", new CfnOutputProps
        {
            Value = Fn.Join("", new[] { "ec2-", Fn.Join("-", Fn.Split(".", hostIp.Ref)), $".{Region}.compute.amazonaws.com" }),
        });
        new CfnOutput(this, "ArtifactsBucket", new CfnOutputProps { Value = artifacts.BucketName });
    }

    private Repository ImageRepository(string id, string name) => new(this, id, new RepositoryProps
    {
        RepositoryName = name,
        ImageScanOnPush = true,
        LifecycleRules = new[] { new Amazon.CDK.AWS.ECR.LifecycleRule { MaxImageCount = 10 } },
    });

    private string ParameterArn(string name) =>
        FormatArn(new ArnComponents { Service = "ssm", Resource = "parameter", ResourceName = name });

    // Runs once, at the instance's first boot: changing it later does not re-run it on the existing host.
    private static UserData HostUserData(Amazon.CDK.AWS.EC2.Volume data)
    {
        // Nitro instances expose EBS volumes as NVMe devices named after the volume id without its dash.
        var device = Fn.Join("", new[]
        {
            "/dev/disk/by-id/nvme-Amazon_Elastic_Block_Store_",
            Fn.Join("", Fn.Split("-", data.VolumeId)),
        });

        var userData = UserData.ForLinux();
        userData.AddCommands(
            "set -euo pipefail",

            // Data volume at Docker's volume directory, so Postgres, Redis and RabbitMQ data â€” and Docker's
            // own index of the volumes â€” survive the instance being replaced. The attachment is created
            // after the instance, so wait for it; format only a blank volume, never one holding data.
            $"DEV={device}",
            "for i in $(seq 1 60); do [ -e \"$DEV\" ] && break; sleep 5; done",
            "[ -e \"$DEV\" ] || { echo 'data volume never attached' >&2; exit 1; }",
            "blkid \"$DEV\" || mkfs.xfs \"$DEV\"",
            "mkdir -p /var/lib/docker/volumes",
            "echo \"UUID=$(blkid -s UUID -o value \"$DEV\") /var/lib/docker/volumes xfs defaults,nofail 0 2\" >> /etc/fstab",
            "mount /var/lib/docker/volumes",

            // Five containers in 2 GB: swap so a memory spike slows the host rather than OOM-killing Postgres.
            "dd if=/dev/zero of=/swapfile bs=1M count=2048",
            "chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile",
            "echo '/swapfile none swap defaults 0 0' >> /etc/fstab",

            "dnf install -y docker",
            "mkdir -p /usr/local/lib/docker/cli-plugins",
            $"curl -fsSL -o /usr/local/lib/docker/cli-plugins/docker-compose https://github.com/docker/compose/releases/download/{ComposeVersion}/docker-compose-linux-x86_64",
            $"echo '{ComposeSha256}  /usr/local/lib/docker/cli-plugins/docker-compose' | sha256sum -c -",
            "chmod +x /usr/local/lib/docker/cli-plugins/docker-compose",

            // Default log driver for every container, so docker-compose.prod.yml stays free of logging config
            // and runs unchanged locally. Non-blocking: a CloudWatch hiccup must not stall the app.
            "mkdir -p /etc/docker",
            "cat > /etc/docker/daemon.json <<'EOF'",
            "{\"log-driver\": \"awslogs\", \"log-opts\": {\"awslogs-region\": \"ca-central-1\", \"awslogs-group\": \"" + LogGroupName + "\", \"tag\": \"{{.Name}}\", \"mode\": \"non-blocking\"}}",
            "EOF",

            // Docker refuses to start without the data volume, rather than quietly creating empty volumes on
            // the root disk.
            "mkdir -p /etc/systemd/system/docker.service.d",
            "printf '[Unit]\\nRequiresMountsFor=/var/lib/docker/volumes\\n' > /etc/systemd/system/docker.service.d/data-volume.conf",
            "systemctl daemon-reload",
            "systemctl enable --now docker",
            "mkdir -p /opt/marquee");
        return userData;
    }

    // Crash-consistent snapshots of the data volume; Postgres recovers from them like from a power cut.
    private void DailySnapshots()
    {
        var role = new Role(this, "SnapshotRole", new RoleProps
        {
            AssumedBy = new ServicePrincipal("dlm.amazonaws.com"),
            ManagedPolicies = new[]
            {
                ManagedPolicy.FromAwsManagedPolicyName("service-role/AWSDataLifecycleManagerServiceRole"),
            },
        });

        new CfnLifecyclePolicy(this, "DailySnapshots", new CfnLifecyclePolicyProps
        {
            Description = "Marquee data volume - daily - 7 kept",
            State = "ENABLED",
            ExecutionRoleArn = role.RoleArn,
            PolicyDetails = new CfnLifecyclePolicy.PolicyDetailsProperty
            {
                ResourceTypes = new[] { "VOLUME" },
                TargetTags = new[] { new CfnTag { Key = BackupTagKey, Value = "daily" } },
                Schedules = new[]
                {
                    new CfnLifecyclePolicy.ScheduleProperty
                    {
                        Name = "daily",
                        // 08:00 UTC is 03:00â€“04:00 in Toronto, outside the 07:00â€“23:00 Premiere window.
                        CreateRule = new CfnLifecyclePolicy.CreateRuleProperty
                        {
                            Interval = 24,
                            IntervalUnit = "HOURS",
                            Times = new[] { "08:00" },
                        },
                        RetainRule = new CfnLifecyclePolicy.RetainRuleProperty { Count = 7 },
                        CopyTags = true,
                    },
                },
            },
        });
    }

    private void Alarms(Instance_ host, string alertEmail)
    {
        var alerts = new Topic(this, "Alerts");
        alerts.AddSubscription(new EmailSubscription(alertEmail));
        var notify = new SnsAction(alerts);
        var dimensions = new Dictionary<string, string> { ["InstanceId"] = host.InstanceId };

        // The underlying hardware failed: EC2 moves the instance to healthy hardware, keeping its id,
        // Elastic IP and volumes.
        var systemStatus = new Alarm(this, "SystemStatusAlarm", new AlarmProps
        {
            AlarmDescription = "Marquee host failed its system status check; EC2 is recovering it.",
            Metric = new Metric(new MetricProps
            {
                Namespace = "AWS/EC2",
                MetricName = "StatusCheckFailed_System",
                DimensionsMap = dimensions,
                Statistic = "Maximum",
                Period = Duration.Minutes(1),
            }),
            Threshold = 1,
            ComparisonOperator = ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
            EvaluationPeriods = 2,
        });
        systemStatus.AddAlarmAction(new Ec2Action(Ec2InstanceAction.RECOVER), notify);

        // With credit specification "standard", running out of CPU credits is what hurts: the host is
        // throttled to its 20% baseline. Utilization can't show that (it drops once throttled), the
        // credit balance can. T3 standard launches with no credits, so expect this to fire for the first
        // hour or so after the instance is created.
        var cpuCredits = new Alarm(this, "CpuCreditAlarm", new AlarmProps
        {
            AlarmDescription = "Marquee host is nearly out of CPU credits and will be throttled to baseline.",
            Metric = new Metric(new MetricProps
            {
                Namespace = "AWS/EC2",
                MetricName = "CPUCreditBalance",
                DimensionsMap = dimensions,
                Statistic = "Minimum",
                Period = Duration.Minutes(5),
            }),
            Threshold = 20,
            ComparisonOperator = ComparisonOperator.LESS_THAN_THRESHOLD,
            EvaluationPeriods = 3,
        });
        cpuCredits.AddAlarmAction(notify);
    }
}
