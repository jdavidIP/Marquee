using SkiaSharp;

namespace Marquee.Infrastructure.Avatars;

/// <summary>An upload refused for what it is, with a message fit to show the person who sent it.</summary>
public sealed class AvatarImageException(string message) : Exception(message);

/// <summary>
/// Turns an uploaded photo into the one Marquee stores (#63). Whatever the browser sent, the result
/// is an image the API produced itself: upright, a centred square, resized, re-encoded as WebP, with
/// no metadata. Re-encoding is the point — it is what keeps a phone photo's EXIF and GPS out of the
/// bucket — so the client's crop is never trusted to have done any of this.
/// </summary>
public static class AvatarImage
{
    private static readonly SKEncodedImageFormat[] Accepted =
        [SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Png, SKEncodedImageFormat.Webp];

    public static async Task<byte[]> ToWebpAsync(Stream source, AvatarOptions options, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        using var data = SKData.Create(buffer);
        using var codec = SKCodec.Create(data);
        if (codec is null || !Accepted.Contains(codec.EncodedFormat))
            throw Unsupported();

        // From the header, before decoding: a few kilobytes of PNG can claim a bitmap big enough to
        // exhaust memory once decoded.
        if (codec.Info.Width > options.MaxSourceDimension || codec.Info.Height > options.MaxSourceDimension)
            throw new AvatarImageException("That image is too large.");

        using var decoded = SKBitmap.Decode(codec) ?? throw Unsupported();
        // Immutable, so wrapping it in an SKImage shares its pixels instead of copying them.
        decoded.SetImmutable();
        using var turned = Upright(decoded, codec.EncodedOrigin);
        var upright = turned ?? decoded;

        var side = Math.Min(upright.Width, upright.Height);
        var crop = SKRect.Create((upright.Width - side) / 2f, (upright.Height - side) / 2f, side, side);

        // No colour space on the target, so the encoder has no ICC profile to embed, and Skia's WebP
        // encoder writes no EXIF or XMP of its own.
        using var surface = SKSurface.Create(new SKImageInfo(options.Size, options.Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using (var image = SKImage.FromBitmap(upright))
            surface.Canvas.DrawImage(image, crop, SKRect.Create(options.Size, options.Size), new SKSamplingOptions(SKCubicResampler.Mitchell));

        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Webp, options.Quality);
        return encoded.ToArray();
    }

    /// <summary>
    /// The bitmap as the camera meant it to be seen, or null when it already is. Skia decodes the
    /// stored pixels and only reports the EXIF orientation, so each of the eight origins is mapped to
    /// the transform that undoes it.
    /// </summary>
    private static SKBitmap? Upright(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return null;

        float w = bitmap.Width, h = bitmap.Height;
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        // Each maps a stored pixel (x, y) to its upright position.
        var transform = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };

        var result = new SKBitmap(swaps ? bitmap.Height : bitmap.Width, swaps ? bitmap.Width : bitmap.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(transform);
        // Quarter turns and flips land every pixel exactly on another, so nothing needs resampling.
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        result.SetImmutable();
        return result;
    }

    private static AvatarImageException Unsupported() =>
        new("That file type isn't supported. Use a JPEG, PNG or WebP.");
}
