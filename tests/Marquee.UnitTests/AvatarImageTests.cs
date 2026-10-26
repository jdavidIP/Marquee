using System.Text;
using FluentAssertions;
using Marquee.Infrastructure.Avatars;
using SkiaSharp;

namespace Marquee.UnitTests;

/// <summary>
/// Every profile photo is one the API produced itself (#63): whatever arrives is decoded, turned
/// upright, cut to a square, resized and re-encoded, so nothing of the original file — its EXIF and
/// GPS above all — survives into the bucket.
/// </summary>
public class AvatarImageTests
{
    private static readonly AvatarOptions Options = new();

    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Blue = new(0, 0, 255);

    [Fact]
    public async Task Jpeg_becomes_640_square_webp()
    {
        var output = await Process(Encode(Solid(1000, 1000, SKColors.Green), SKEncodedImageFormat.Jpeg));

        using var codec = SKCodec.Create(new MemoryStream(output));
        codec.EncodedFormat.Should().Be(SKEncodedImageFormat.Webp);
        (codec.Info.Width, codec.Info.Height).Should().Be((640, 640));
    }

    [Fact]
    public async Task Non_square_input_is_centre_cropped()
    {
        // 1200×800 with a red strip in its leftmost 150px: the centre 800×800 square starts at x=200,
        // so a crop drops the strip and a stretch would keep it.
        using var source = Solid(1200, 800, Blue);
        for (var y = 0; y < 800; y++)
            for (var x = 0; x < 150; x++)
                source.SetPixel(x, y, Red);

        var output = await Process(Encode(source, SKEncodedImageFormat.Png));

        using var image = SKBitmap.Decode(output);
        (image.Width, image.Height).Should().Be((640, 640));
        IsBlue(image.GetPixel(2, 320)).Should().BeTrue("the strip outside the centre square is cropped away");
    }

    [Fact]
    public async Task Metadata_is_stripped()
    {
        var input = WithExif(Encode(Solid(800, 800, SKColors.Green), SKEncodedImageFormat.Jpeg), orientation: null);
        Contains(input, "Exif").Should().BeTrue("the test input must carry EXIF to prove anything");

        var output = await Process(input);

        // A WebP carries metadata in its own RIFF chunks; none of them may be present.
        Contains(output, "EXIF").Should().BeFalse();
        Contains(output, "Exif").Should().BeFalse();
        Contains(output, "ICCP").Should().BeFalse();
        Contains(output, "XMP ").Should().BeFalse();
        Contains(output, "Phone").Should().BeFalse();
    }

    /// <summary>
    /// Stored pixels: left half red, right half blue. The EXIF orientation says how to turn them
    /// upright: 1 as stored, 3 upside down, 6 and 8 sideways — the ones phones write — and the
    /// mirrored 2, 4, 5 and 7, which they almost never do but which share the same matrix table.
    /// </summary>
    [Theory]
    [InlineData(1, true, true, false)]
    [InlineData(2, false, false, true)]
    [InlineData(3, false, false, true)]
    [InlineData(4, true, true, false)]
    [InlineData(5, true, false, true)]
    [InlineData(6, true, false, true)]
    [InlineData(7, false, true, false)]
    [InlineData(8, false, true, false)]
    public async Task Exif_orientation_is_applied(ushort orientation, bool topLeftRed, bool bottomLeftRed, bool topRightRed)
    {
        using var source = new SKBitmap(400, 200);
        for (var y = 0; y < 200; y++)
            for (var x = 0; x < 400; x++)
                source.SetPixel(x, y, x < 200 ? Red : Blue);

        var output = await Process(WithExif(Encode(source, SKEncodedImageFormat.Jpeg), orientation));

        using var image = SKBitmap.Decode(output);
        IsRed(image.GetPixel(5, 5)).Should().Be(topLeftRed, "top left");
        IsRed(image.GetPixel(5, 634)).Should().Be(bottomLeftRed, "bottom left");
        IsRed(image.GetPixel(634, 5)).Should().Be(topRightRed, "top right");
        // Each corner is one colour or the other, never neither (a wrong transform leaves it empty).
        IsBlue(image.GetPixel(5, 5)).Should().Be(!topLeftRed, "top left");
        IsBlue(image.GetPixel(5, 634)).Should().Be(!bottomLeftRed, "bottom left");
        IsBlue(image.GetPixel(634, 5)).Should().Be(!topRightRed, "top right");
    }

    /// <summary>The theory's fixture is the same top to bottom, so a vertical flip (4) needs its own.</summary>
    [Fact]
    public async Task Exif_orientation_4_flips_vertically()
    {
        using var source = new SKBitmap(200, 200);
        for (var y = 0; y < 200; y++)
            for (var x = 0; x < 200; x++)
                source.SetPixel(x, y, y < 100 ? Red : Blue);

        var output = await Process(WithExif(Encode(source, SKEncodedImageFormat.Jpeg), orientation: 4));

        using var image = SKBitmap.Decode(output);
        IsBlue(image.GetPixel(320, 10)).Should().BeTrue("the stored bottom is the upright top");
        IsRed(image.GetPixel(320, 630)).Should().BeTrue("the stored top is the upright bottom");
    }

    [Fact]
    public async Task Transparent_png_keeps_alpha()
    {
        var output = await Process(Encode(Solid(100, 100, SKColors.Transparent), SKEncodedImageFormat.Png));

        using var image = SKBitmap.Decode(output);
        image.GetPixel(0, 0).Alpha.Should().Be(0);
    }

    [Fact]
    public async Task Gif_is_refused()
    {
        // A 1×1 GIF; Skia can decode GIFs but cannot write one.
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

        var act = () => Process(gif);

        await act.Should().ThrowAsync<AvatarImageException>()
            .WithMessage("That file type isn't supported. Use a JPEG, PNG or WebP.");
    }

    [Fact]
    public async Task Garbage_is_refused()
    {
        var bytes = new byte[2048];
        new Random(42).NextBytes(bytes);

        var act = () => Process(bytes);

        await act.Should().ThrowAsync<AvatarImageException>()
            .WithMessage("That file type isn't supported. Use a JPEG, PNG or WebP.");
    }

    /// <summary>
    /// The client only ever sends its 640px crop, so 2048 leaves headroom while keeping one decode to
    /// 16 MB of pixels — on a 2 GB host shared with the database, 8000 (256 MB) was an OOM away.
    /// </summary>
    [Fact]
    public async Task Sources_over_2048_px_are_refused()
    {
        var act = () => Process(Encode(Solid(2049, 10, SKColors.Green), SKEncodedImageFormat.Png));

        await act.Should().ThrowAsync<AvatarImageException>().WithMessage("That image is too large.");
    }

    [Fact]
    public async Task Huge_dimensions_are_refused()
    {
        var act = () => Process(Encode(Solid(9000, 10, SKColors.Green), SKEncodedImageFormat.Png));

        await act.Should().ThrowAsync<AvatarImageException>().WithMessage("That image is too large.");
    }

    private static Task<byte[]> Process(byte[] input) =>
        AvatarImage.ToWebpAsync(new MemoryStream(input), Options, CancellationToken.None);

    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format)
    {
        using (bitmap)
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(format, 95))
            return data.ToArray();
    }

    /// <summary>
    /// Inserts a real EXIF segment (APP1) straight after a JPEG's start marker: a camera make of
    /// "Phone", and the orientation tag when one is given. Skia writes no EXIF of its own.
    /// </summary>
    private static byte[] WithExif(byte[] jpeg, ushort? orientation)
    {
        var entries = new List<byte[]>
        {
            // Make (0x010F), ASCII, 6 bytes, stored at the offset that follows the IFD.
            new byte[] { 0x01, 0x0F, 0x00, 0x02, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00 },
        };
        if (orientation is { } o)
            entries.Add([0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)o, 0x00, 0x00]);

        var ifdSize = 2 + entries.Count * 12 + 4;
        var makeOffset = 8 + ifdSize;
        entries[0][8] = (byte)(makeOffset >> 24);
        entries[0][9] = (byte)(makeOffset >> 16);
        entries[0][10] = (byte)(makeOffset >> 8);
        entries[0][11] = (byte)makeOffset;

        var tiff = new List<byte> { 0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x00, (byte)entries.Count };
        foreach (var entry in entries) tiff.AddRange(entry);
        tiff.AddRange([0, 0, 0, 0]);
        tiff.AddRange(Encoding.ASCII.GetBytes("Phone\0"));

        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var length = payload.Length + 2;
        var app1 = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload);

        return jpeg[..2].Concat(app1).Concat(jpeg[2..]).ToArray();
    }

    private static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle)) >= 0;

    private static bool IsRed(SKColor p) => p.Red > 200 && p.Green < 60 && p.Blue < 60;
    private static bool IsBlue(SKColor p) => p.Blue > 200 && p.Red < 60 && p.Green < 60;
}
