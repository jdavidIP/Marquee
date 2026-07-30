namespace Marquee.IntegrationTests;

/// <summary>
/// One Postgres and one Redis container shared by every integration test class. Starting a pair per
/// class would multiply an already slow fixture by the number of files, and nothing here needs
/// isolation at the container level — the tests create their own Premieres and assert on those.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<MarqueeAppFactory>
{
    public const string Name = "marquee-integration";
}
