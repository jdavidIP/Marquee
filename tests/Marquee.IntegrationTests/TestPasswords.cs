namespace Marquee.IntegrationTests;

/// <summary>
/// One password for every test that just needs an account to exist, so retuning the policy (#27)
/// touches one line rather than every registration helper in the suite. Tests that are <em>about</em>
/// the policy state their own passwords inline — the point there is the specific password.
/// </summary>
public static class TestPasswords
{
    /// <summary>
    /// Satisfies the default policy: long, has a letter and a digit, not on the blocklist, and
    /// short of any username these tests generate. Was "correct horse battery staple" until the
    /// digit requirement landed.
    /// </summary>
    public const string Valid = "correct horse battery staple 7";
}
