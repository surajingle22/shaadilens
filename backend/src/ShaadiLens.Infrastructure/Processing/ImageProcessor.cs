using System.Globalization;
using ImageMagick;
using ImageMagick.Drawing;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Domain.Entities;

namespace ShaadiLens.Infrastructure.Processing;

/// <summary>
/// Turns an uploaded original into what the app serves:
///   web.jpg   2048px long edge, JPEG q82 — lightbox + face detection input
///   thumb.jpg  400px long edge, JPEG q75 — grids
/// Both are rotated upright (EXIF orientation), converted to sRGB and have ALL metadata stripped
/// (no GPS location leaks to guests). The original is kept byte-for-byte (HEIC is converted once to JPEG).
///
/// Uses Magick.NET (ImageMagick, Apache 2.0): reads JPEG/PNG/WebP/HEIC with no licence key.
/// </summary>
public sealed class ImageProcessor(IStorageService storage)
{
    public const int WebSize = 2048;
    public const int ThumbSize = 400;

    private static readonly string[] HeicExtensions = [".heic", ".heif"];

    /// <param name="watermark">Text drawn on the web image (bottom-right), or null. Thumbs and originals stay clean.</param>
    public async Task PrepareAsync(Photo photo, string? watermark, CancellationToken ct)
    {
        if (HeicExtensions.Contains(Path.GetExtension(photo.StorageKeyOriginal).ToLowerInvariant()))
            await ConvertHeicToJpegAsync(photo, ct);

        // 1. "Ping" = read only the headers: real size + EXIF, no pixel decoding (fast).
        int width, height;
        using (var info = new MagickImage())
        {
            await using (var s = await storage.OpenReadAsync(photo.StorageKeyOriginal, ct))
                info.Ping(s);

            var exif = info.GetExifProfile();
            // EXIF orientation 5–8 = camera held sideways → upright image has width/height swapped.
            var rotated = info.Orientation is Orientation.LeftTop or Orientation.RightTop
                or Orientation.RightBottom or Orientation.LeftBottom;
            width = (int)info.Width;
            height = (int)info.Height;
            photo.Width = rotated ? height : width;
            photo.Height = rotated ? width : height;

            photo.TakenAt = ReadTakenAt(exif);
            photo.CameraModel = ReadString(exif, ExifTag.Model);
        }

        // 2. Decode at reduced size. For JPEG, the "jpeg:size" hint lets libjpeg decode at a fraction
        //    (e.g. 3/8) of full size directly (IDCT scaling) — much faster and far less memory than
        //    decoding all 24 MP. libjpeg may round below the hint, so we ask for 2x and resize exactly after.
        var scale = Math.Min(1.0, 2.0 * WebSize / Math.Max(width, height));
        var settings = new MagickReadSettings();
        settings.SetDefine(MagickFormat.Jpeg, "size", $"{(int)Math.Ceiling(width * scale)}x{(int)Math.Ceiling(height * scale)}");

        using var image = await LoadAsync(photo.StorageKeyOriginal, settings, ct);
        image.AutoOrient();                               // apply EXIF rotation to the pixels
        ToSrgb(image);
        image.Strip();                                    // drop EXIF/GPS/XMP/ICC
        FitWithin(image, WebSize);

        // Thumbnail first, from the clean image: grids look nicer without a watermark on every tile.
        using (var thumb = (MagickImage)image.Clone())
        {
            FitWithin(thumb, ThumbSize);
            await SaveJpegAsync(thumb, StorageKeys.Thumb(photo.EventId, photo.Id), quality: 75, ct);
        }

        if (!string.IsNullOrWhiteSpace(watermark)) DrawWatermark(image, watermark);
        var webKey = StorageKeys.Web(photo.EventId, photo.Id);
        await SaveJpegAsync(image, webKey, quality: 82, ct);
        var thumbKey = StorageKeys.Thumb(photo.EventId, photo.Id);

        photo.StorageKeyWeb = webKey;
        photo.StorageKeyThumb = thumbKey;
    }

    private static readonly string WatermarkFont = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Poppins-SemiBold.ttf");

    /// <summary>
    /// Semi-transparent text in the bottom-right corner, sized to the photo (≈3% of the long edge), with a soft
    /// shadow so it reads on both bright and dark photos. Only on the "web" image guests see in the lightbox.
    /// </summary>
    public static void DrawWatermark(MagickImage image, string text)
    {
        var size = Math.Max(14, Math.Max(image.Width, image.Height) * 0.03);
        var margin = (int)(size * 0.9);
        foreach (var (color, offset) in new[] { (new MagickColor(0, 0, 0, 90), 2), (new MagickColor(255, 255, 255, 170), 0) })
        {
            new Drawables()
                .Font(WatermarkFont)
                .FontPointSize(size)
                .FillColor(color)
                .Gravity(Gravity.Southeast)
                .Text(margin - offset, margin - offset, text.Trim())
                .Draw(image);
        }
    }

    /// <summary>iPhones save HEIC, which many browsers can't show. Convert once to a high-quality JPEG original.</summary>
    private async Task ConvertHeicToJpegAsync(Photo photo, CancellationToken ct)
    {
        var jpgKey = StorageKeys.Original(photo.EventId, photo.Id, ".jpg");
        using (var magick = await LoadAsync(photo.StorageKeyOriginal, new MagickReadSettings(), ct))
        {
            magick.AutoOrient();                          // bake the rotation into the pixels
            magick.Quality = 92;
            await using var output = storage.OpenWrite(jpgKey);
            await magick.WriteAsync(output, MagickFormat.Jpeg, ct);
        }
        await storage.DeleteAsync(photo.StorageKeyOriginal, ct);
        photo.StorageKeyOriginal = jpgKey;
    }

    private async Task<MagickImage> LoadAsync(string key, MagickReadSettings settings, CancellationToken ct)
    {
        await using var s = await storage.OpenReadAsync(key, ct);
        var image = new MagickImage();
        await image.ReadAsync(s, settings, ct);
        return image;
    }

    /// <summary>Wide-gamut photos (Adobe RGB, Display P3) look washed out if we just drop their profile.</summary>
    private static void ToSrgb(MagickImage image)
    {
        if (image.GetColorProfile() is not null) image.TransformColorSpace(ColorProfiles.SRGB);
    }

    private static void FitWithin(MagickImage image, int maxSide)
    {
        if (Math.Max(image.Width, image.Height) <= maxSide) return; // never upscale
        image.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide)); // keeps aspect ratio
    }

    private async Task SaveJpegAsync(MagickImage image, string key, int quality, CancellationToken ct)
    {
        image.Quality = (uint)quality;
        await using var output = storage.OpenWrite(key);
        await image.WriteAsync(output, MagickFormat.Jpeg, ct);
    }

    private static DateTimeOffset? ReadTakenAt(IExifProfile? exif)
    {
        var raw = ReadString(exif, ExifTag.DateTimeOriginal) ?? ReadString(exif, ExifTag.DateTime);
        // EXIF dates look like "2026:12:12 18:45:03" with no time zone. We store them as-is (UTC-labelled),
        // which keeps the order of photos correct as long as all cameras use the same local clock.
        return raw is not null && DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? new DateTimeOffset(dt, TimeSpan.Zero)
            : null;
    }

    private static string? ReadString(IExifProfile? exif, ExifTag<string> tag)
    {
        var value = exif?.GetValue(tag)?.Value?.Trim('\0', ' ');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
