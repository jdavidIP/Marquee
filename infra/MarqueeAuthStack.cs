using Amazon.CDK;
using Amazon.CDK.AWS.Cognito;
using Amazon.CDK.AWS.IAM;
using Constructs;

namespace Marquee.Cdk;

// Sign-up, sign-in, confirmation and password reset (DEPLOYMENT.md § Phase 2). Cognito owns
// authentication only; role, permissions and everything else the domain uses stay in Postgres.
//
// Its own stack because the pool is stateful in a way nothing else here is: password hashes cannot be
// exported, so losing the pool means every user resets their password. MarqueeStack can be torn down
// and rebuilt (see the instance-replacement caveat in DEPLOYMENT.md) without touching it.
public class MarqueeAuthStack : Stack
{
    public UserPool UserPool { get; }

    public MarqueeAuthStack(Construct scope, string id, IStackProps props)
        : base(scope, id, props)
    {
        UserPool = new UserPool(this, "Users", new UserPoolProps
        {
            UserPoolName = "marquee",
            SelfSignUpEnabled = true,
            // Username sign-in with email as an alias: Cognito then enforces username uniqueness at
            // sign-up, and email uniqueness among confirmed users only — an unconfirmed sign-up
            // cannot hold an address hostage.
            SignInAliases = new SignInAliases { Username = true, Email = true },
            SignInCaseSensitive = false,
            StandardAttributes = new StandardAttributes
            {
                Email = new StandardAttribute { Required = true, Mutable = true },
            },
            AutoVerify = new AutoVerifiedAttrs { Email = true },
            // A code, not a link: link confirmation lands on a Cognito page that cannot return to the app.
            // Cognito sends this same template for password resets, whose codes last 1 hour rather than
            // sign-up's 24 — so the message names no expiry.
            UserVerification = new UserVerificationConfig
            {
                EmailStyle = VerificationEmailStyle.CODE,
                EmailSubject = "Your Marquee code",
                EmailBody = "Your Marquee code is {####}.",
            },
            // The product policy is exactly what Cognito enforces. The three false flags are not
            // redundant: Cognito's defaults turn them on.
            PasswordPolicy = new PasswordPolicy
            {
                MinLength = 10,
                RequireDigits = true,
                RequireLowercase = false,
                RequireUppercase = false,
                RequireSymbols = false,
            },
            AccountRecovery = AccountRecovery.EMAIL_ONLY,
            // Built-in sender: 50 emails a day per account, not adjustable. Phase 3 moves to SES.
            Email = UserPoolEmail.WithCognito(),
            // Lite: 10,000 MAU always free and every feature in use here. Essentials (the default for
            // new pools) adds passwordless and password-history features this app does not use.
            FeaturePlan = FeaturePlan.LITE,
            DeletionProtection = true,
            RemovalPolicy = RemovalPolicy.RETAIN,
        });

        // Public: the browser calls cognito-idp directly, and a browser cannot keep a secret.
        var webClient = UserPool.AddClient("WebClient", new UserPoolClientOptions
        {
            UserPoolClientName = "marquee-web",
            GenerateSecret = false,
            AuthFlows = new AuthFlow { UserPassword = true },
            // No hosted UI and no OAuth flows. Tokens from InitiateAuth still carry the
            // aws.cognito.signin.user.admin scope the API's GetUser call relies on.
            DisableOAuth = true,
            // Today's behaviour: a 24h session, then sign in again. Both are Cognito's maximum.
            AccessTokenValidity = Duration.Hours(24),
            IdTokenValidity = Duration.Hours(24),
            PreventUserExistenceErrors = true,
        });

        // The pipeline reads the outputs below live, like MarqueeStack's.
        var deployRole = Role.FromRoleName(this, "DeployRole", MarqueeCiStack.DeployRoleName);
        deployRole.AddToPrincipalPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = new[] { "cloudformation:DescribeStacks" },
            Resources = new[] { StackId },
        }));

        new CfnOutput(this, "UserPoolId", new CfnOutputProps { Value = UserPool.UserPoolId });
        new CfnOutput(this, "UserPoolClientId", new CfnOutputProps { Value = webClient.UserPoolClientId });
        new CfnOutput(this, "Issuer", new CfnOutputProps { Value = UserPool.UserPoolProviderUrl });
    }
}
