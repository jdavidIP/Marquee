namespace Marquee.Infrastructure.Avatars;

/// <summary>
/// Profile photos (#63): where they are stored, how they are served, and what an upload may be.
/// All tunable, bound from configuration.
/// </summary>
public sealed class AvatarOptions
{
    public const string SectionName = "Avatars";

    public string BucketName { get; set; } = "";

    /// <summary>
    /// Where the bucket's <c>avatars/</c> prefix is served, and so the start of every stored
    /// <c>AvatarUrl</c>. Production uses the relative <c>/avatars</c>, CloudFront's behaviour for that
    /// prefix, so stored values survive a change of domain; S3Mock serves it at
    /// <c>…/marquee-avatars/avatars</c>.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>An S3-compatible endpoint (S3Mock locally and in tests). Empty means real S3.</summary>
    public string? ServiceUrl { get; set; }

    public string Region { get; set; } = "ca-central-1";

    /// <summary>Side of the stored square, in pixels. The badge draws it at about 270 CSS px.</summary>
    public int Size { get; set; } = 640;

    /// <summary>Largest upload accepted, checked before anything is decoded.</summary>
    public int MaxUploadBytes { get; set; } = 5_000_000;

    /// <summary>
    /// Largest width or height a source may declare. A few kilobytes of PNG can claim a bitmap big
    /// enough to exhaust memory once decoded, so this is read from the header before decoding. 2048
    /// keeps one decode to 16 MB of pixels on a 2 GB host shared with the database; the client only
    /// ever sends its 640px crop, so nothing real comes near it.
    /// </summary>
    public int MaxSourceDimension { get; set; } = 2048;

    public int Quality { get; set; } = 80;
}
