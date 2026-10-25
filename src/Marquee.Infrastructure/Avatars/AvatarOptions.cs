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
    /// Prefix of every stored <c>AvatarUrl</c>. Production uses the relative <c>/avatars</c>, served by
    /// CloudFront from the bucket, so stored values survive a change of domain.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>An S3-compatible endpoint (S3Mock locally and in tests). Empty means real S3.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Side of the stored square, in pixels. The badge draws it at about 270 CSS px.</summary>
    public int Size { get; set; } = 640;

    /// <summary>Largest upload accepted, checked before anything is decoded.</summary>
    public int MaxUploadBytes { get; set; } = 5_000_000;

    /// <summary>
    /// Largest width or height a source may declare. A few kilobytes of PNG can claim a bitmap big
    /// enough to exhaust memory once decoded, so this is read from the header before decoding.
    /// </summary>
    public int MaxSourceDimension { get; set; } = 8000;

    public int Quality { get; set; } = 80;
}
