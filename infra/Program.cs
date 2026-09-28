using Amazon.CDK;
using Marquee.Cdk;

var app = new App();

// Budget and alarm emails go here. Kept out of the repository: pass it on the command line,
// e.g. `cdk deploy -c alertEmail=you@example.com`.
var alertEmail = app.Node.TryGetContext("alertEmail") as string;
if (string.IsNullOrWhiteSpace(alertEmail))
    throw new InvalidOperationException("Missing alert address: add -c alertEmail=<address> to the cdk command.");

// The account comes from the signed-in CLI rather than being committed; the region is fixed.
var env = new Amazon.CDK.Environment
{
    Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
    Region = "ca-central-1",
};

new MarqueeCiStack(app, "MarqueeCiStack", alertEmail, new StackProps { Env = env });
new MarqueeStack(app, "MarqueeStack", alertEmail, new StackProps { Env = env });

app.Synth();
