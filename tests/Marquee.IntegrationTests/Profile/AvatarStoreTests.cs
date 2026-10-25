using FluentAssertions;
using Marquee.Infrastructure.Avatars;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// The profile-photo bucket (#63), through the AWS SDK against S3Mock. Every photo gets a key of its
/// own and keys are never reused, which is what lets CloudFront cache a photo without ever serving a
/// stale one; a replaced or removed photo is deleted, not left behind.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AvatarStoreTests(MarqueeAppFactory factory)
{
    private static readonly byte[] Webp = [0x52, 0x49, 0x46, 0x46, 0x01, 0x02, 0x03, 0x04];

    private IAvatarStore Store => factory.Services.GetRequiredService<IAvatarStore>();

    [Fact]
    public async Task Put_writes_webp_under_user_prefix()
    {
        var userId = Guid.NewGuid();

        var url = await Store.PutAsync(userId, Webp, CancellationToken.None);

        url.Should().StartWith($"{factory.S3.PublicBaseUrl}/avatars/{userId}/").And.EndWith(".webp");
        using var response = await factory.S3.GetAsync(url);
        response.IsSuccessStatusCode.Should().BeTrue();
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/webp");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(Webp);
    }

    [Fact]
    public async Task Two_puts_use_different_keys()
    {
        var userId = Guid.NewGuid();

        var first = await Store.PutAsync(userId, Webp, CancellationToken.None);
        var second = await Store.PutAsync(userId, Webp, CancellationToken.None);

        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Delete_removes_the_object()
    {
        var url = await Store.PutAsync(Guid.NewGuid(), Webp, CancellationToken.None);

        await Store.DeleteAsync(url, CancellationToken.None);

        (await factory.S3.ExistsAsync(url)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_ignores_foreign_urls()
    {
        var kept = await Store.PutAsync(Guid.NewGuid(), Webp, CancellationToken.None);

        var act = () => Store.DeleteAsync("https://elsewhere.test/avatars/x.webp", CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await factory.S3.ExistsAsync(kept)).Should().BeTrue();
    }
}
