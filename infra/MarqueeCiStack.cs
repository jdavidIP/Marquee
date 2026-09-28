using Amazon.CDK;
using Amazon.CDK.AWS.Budgets;
using Amazon.CDK.AWS.IAM;
using Constructs;

namespace Marquee.Cdk;

// Deployed rarely, by hand. Kept apart from the app stack so a deploy can never edit the
// permissions of the role performing it.
public class MarqueeCiStack : Stack
{
    private const string GitHubIssuer = "token.actions.githubusercontent.com";
    private const string DeployRef = "repo:jdavidIP/Marquee:ref:refs/heads/main";
    private const double MonthlyBudgetUsd = 30;

    public MarqueeCiStack(Construct scope, string id, string budgetEmail, IStackProps props)
        : base(scope, id, props)
    {
        var subscribers = new[]
        {
            new CfnBudget.SubscriberProperty { SubscriptionType = "EMAIL", Address = budgetEmail },
        };

        new CfnBudget(this, "MonthlyBudget", new CfnBudgetProps
        {
            Budget = new CfnBudget.BudgetDataProperty
            {
                BudgetName = "marquee-monthly",
                BudgetType = "COST",
                TimeUnit = "MONTHLY",
                BudgetLimit = new CfnBudget.SpendProperty { Amount = MonthlyBudgetUsd, Unit = "USD" },
                // Cost before credits: on the Free plan the bill is always ~$0 after credits, so
                // this is the only view that shows how fast the credits are burning.
                CostTypes = new CfnBudget.CostTypesProperty { IncludeCredit = false },
            },
            NotificationsWithSubscribers = new[]
            {
                Alert("ACTUAL", 50, subscribers),
                Alert("ACTUAL", 80, subscribers),
                Alert("ACTUAL", 100, subscribers),
                Alert("FORECASTED", 100, subscribers),
            },
        });

        var github = new OidcProviderNative(this, "GitHubOidc", new OidcProviderNativeProps
        {
            Url = $"https://{GitHubIssuer}",
            ClientIds = new[] { "sts.amazonaws.com" },
        });

        // No permissions here on purpose: the stacks that own each resource grant this role access
        // to that resource's ARN, never a broad `*`.
        var deployRole = new Role(this, "DeployRole", new RoleProps
        {
            RoleName = "marquee-github-deploy",
            Description = "Assumed by GitHub Actions on pushes to main to deploy Marquee.",
            AssumedBy = new WebIdentityPrincipal(github.OidcProviderArn, new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, object>
                {
                    [$"{GitHubIssuer}:aud"] = "sts.amazonaws.com",
                    [$"{GitHubIssuer}:sub"] = DeployRef,
                },
            }),
        });

        new CfnOutput(this, "DeployRoleArn", new CfnOutputProps { Value = deployRole.RoleArn });
    }

    private static CfnBudget.NotificationWithSubscribersProperty Alert(
        string type, double thresholdPercent, CfnBudget.SubscriberProperty[] subscribers) => new()
    {
        Notification = new CfnBudget.NotificationProperty
        {
            NotificationType = type,
            ComparisonOperator = "GREATER_THAN",
            Threshold = thresholdPercent,
            ThresholdType = "PERCENTAGE",
        },
        Subscribers = subscribers,
    };
}
