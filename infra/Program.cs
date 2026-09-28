using Amazon.CDK;
using Marquee.Cdk;

var app = new App();

// Kept out of the repository: pass it on the command line, e.g. `cdk deploy -c budgetEmail=you@example.com`.
var budgetEmail = app.Node.TryGetContext("budgetEmail") as string;
if (string.IsNullOrWhiteSpace(budgetEmail))
    throw new InvalidOperationException("Missing budget alert address: add -c budgetEmail=<address> to the cdk command.");

// The account comes from the signed-in CLI rather than being committed; the region is fixed.
var env = new Amazon.CDK.Environment
{
    Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
    Region = "ca-central-1",
};

new MarqueeCiStack(app, "MarqueeCiStack", budgetEmail, new StackProps { Env = env });

app.Synth();
