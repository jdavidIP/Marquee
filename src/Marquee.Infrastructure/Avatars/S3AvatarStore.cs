using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Marquee.Infrastructure.Avatars;

public sealed class S3AvatarStore(IAmazonS3 s3, IOptions<AvatarOptions> options) : IAvatarStore
{
    private const string KeyPrefix = "avatars/";

    public async Task<string> PutAsync(Guid userId, byte[] webp, CancellationToken ct)
    {
        // A fresh key every time, never reused: a URL always means the same image, so it can be cached
        // without ever going stale.
        var key = $"{KeyPrefix}{userId}/{Guid.NewGuid():N}.webp";

        var request = new PutObjectRequest
        {
            BucketName = options.Value.BucketName,
            Key = key,
            InputStream = new MemoryStream(webp),
            ContentType = "image/webp",
        };
        // Matches CloudFront's 24-hour edge cap, so a removed photo leaves browsers within a day too.
        request.Headers.CacheControl = "public, max-age=86400";

        await s3.PutObjectAsync(request, ct);
        return $"{BaseUrl}/{key}";
    }

    public async Task DeleteAsync(string publicUrl, CancellationToken ct)
    {
        var prefix = BaseUrl + "/";
        if (!publicUrl.StartsWith(prefix, StringComparison.Ordinal))
            return;

        var key = publicUrl[prefix.Length..];
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            return;

        await s3.DeleteObjectAsync(options.Value.BucketName, key, ct);
    }

    private string BaseUrl => options.Value.PublicBaseUrl.TrimEnd('/');
}
