using ImageMagick;
using ImageMagick.Drawing;

namespace ShaadiLens.Tests.Support;

public static class TestImages
{
    /// <summary>
    /// A JPEG like a camera makes: landscape pixels with EXIF orientation 6 ("rotate 90° clockwise"),
    /// a capture date, a camera model and a fake GPS position (which must NOT survive into web/thumb).
    /// </summary>
    public static byte[] CameraJpeg(int width = 3000, int height = 2000, ushort orientation = 6, string seed = "a")
    {
        using var image = new MagickImage($"gradient:#b0004a-#ffd27f", (uint)width, (uint)height);
        image.AddNoise(NoiseType.Gaussian); // real photos are noisy → realistic file sizes (multi-chunk uploads)
        // A white square at a seed-dependent spot → different seeds give different bytes (different SHA-256).
        var offset = (uint)seed.GetHashCode() % (uint)(Math.Min(width, height) / 2);
        new Drawables().FillColor(MagickColors.White).Rectangle(offset, offset, offset + 100, offset + 100).Draw(image);

        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, orientation);
        exif.SetValue(ExifTag.DateTimeOriginal, "2026:12:12 18:45:03");
        exif.SetValue(ExifTag.Model, "TestCam X100");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        image.SetProfile(exif);
        // ImageMagick rewrites the EXIF orientation tag from this property when saving, so set both.
        image.Orientation = (Orientation)orientation;
        image.Quality = 90;
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    public static (uint Width, uint Height, bool HasExif) Inspect(string path)
    {
        using var image = new MagickImage(path);
        return (image.Width, image.Height, image.GetExifProfile() is not null);
    }
}
