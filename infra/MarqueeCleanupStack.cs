using Amazon.CDK;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.Cognito;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.Scheduler;
using Amazon.CDK.AWS.Scheduler.Targets;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Constructs;
using ScheduleExpression = Amazon.CDK.AWS.Scheduler.ScheduleExpression;

namespace Marquee.Cdk;

// Deletes Cognito users who never confirmed their email, once a day (#30, DEPLOYMENT.md § Phase 2,
// decision 4). Low product value on purpose: an unconfirmed account only holds its username. This is
// the project's first Lambda and Scheduler schedule, kept out of the app's critical path.
//
// Its own stack so learning iterations never deploy MarqueeAuthStack, which holds the pool.
public class MarqueeCleanupStack : Stack
{
    // How old an unconfirmed sign-up must be before it is deleted.
    private const int UnconfirmedMaxAgeDays = 7;

    public MarqueeCleanupStack(Construct scope, string id, string alertEmail, IUserPool userPool, IStackProps props)
        : base(scope, id, props)
    {
        var cleanup = new Function(this, "UnconfirmedUserCleanup", new FunctionProps
        {
            FunctionName = "unconfirmed-user-cleanup",
            Description = "Deletes UNCONFIRMED Cognito users older than the window (#30).",
            Runtime = Runtime.NODEJS_22_X,
            Handler = "index.handler",
            // Plain .mjs: the runtime ships AWS SDK v3, so there is nothing to bundle. Paths are
            // relative to infra/, where cdk runs.
            Code = Code.FromAsset("lambda/unconfirmed-cleanup", new Amazon.CDK.AWS.S3.Assets.AssetOptions
            {
                Exclude = new[] { "*.test.mjs" },
            }),
            MemorySize = 128,
            Timeout = Duration.Minutes(1),
            Environment = new Dictionary<string, string>
            {
                ["USER_POOL_ID"] = userPool.UserPoolId,
                ["MIN_AGE_DAYS"] = UnconfirmedMaxAgeDays.ToString(),
            },
            LogGroup = new LogGroup(this, "UnconfirmedUserCleanupLogs", new LogGroupProps
            {
                Retention = RetentionDays.ONE_MONTH,
                RemovalPolicy = RemovalPolicy.DESTROY,
            }),
        });

        // Exactly the two calls the handler makes, on this pool only.
        userPool.Grant(cleanup, "cognito-idp:ListUsers", "cognito-idp:AdminDeleteUser");

        // Scheduler, not an EventBridge rule: it takes a time zone and follows daylight saving.
        // The L2 target creates Scheduler's own role, allowed only to invoke this function.
        new Schedule(this, "DailyCleanup", new ScheduleProps
        {
            Description = "Runs unconfirmed-user-cleanup daily (#30).",
            Schedule = ScheduleExpression.Cron(new CronOptionsWithTimezone
            {
                Minute = "0",
                Hour = "4",
                TimeZone = Amazon.CDK.TimeZone.AMERICA_TORONTO,
            }),
            Target = new LambdaInvoke(cleanup, new ScheduleTargetBaseProps
            {
                // Only this exact payload deletes; any other event, including a console test with a
                // typo, is a dry run.
                Input = ScheduleTargetInput.FromObject(new Dictionary<string, object> { ["dryRun"] = false }),
            }),
        });

        // Its own topic: reusing MarqueeStack's would tie this stack to the app host. The email has to be
        // confirmed within 3 days or SNS deletes the subscription (#133).
        var alerts = new Topic(this, "CleanupAlerts");
        alerts.AddSubscription(new EmailSubscription(alertEmail));

        var failures = new Alarm(this, "CleanupErrorsAlarm", new AlarmProps
        {
            AlarmDescription = "unconfirmed-user-cleanup failed; see its CloudWatch Logs.",
            Metric = cleanup.MetricErrors(new MetricOptions
            {
                Period = Duration.Hours(1),
                Statistic = "Sum",
            }),
            Threshold = 1,
            ComparisonOperator = ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
            EvaluationPeriods = 1,
            // No invocation in an hour means no data point, not a failure.
            TreatMissingData = TreatMissingData.NOT_BREACHING,
        });
        failures.AddAlarmAction(new SnsAction(alerts));

        new CfnOutput(this, "FunctionName", new CfnOutputProps { Value = cleanup.FunctionName });
    }
}
