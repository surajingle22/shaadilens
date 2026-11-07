using ImageMagick;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;

namespace ShaadiLens.Infrastructure.Sharing;

public sealed class MagickImageTools : IImageTools
{
    public const int SelfieMaxSide = 1280;

    public async Task<byte[]> NormalizeSelfieAsync(Stream input, CancellationToken ct = default)
    {
        try
        {
            using var image = new MagickImage();
            await image.ReadAsync(input, ct);
            image.AutoOrient();                        // phone photos are often stored sideways + EXIF flag
            if (image.GetColorProfile() is not null) image.TransformColorSpace(ColorProfiles.SRGB);
            image.Strip();                             // remove GPS/EXIF before we store anything
            if (Math.Max(image.Width, image.Height) > SelfieMaxSide)
                image.Resize(new MagickGeometry(SelfieMaxSide, SelfieMaxSide));
            image.Quality = 90;
            return image.ToByteArray(MagickFormat.Jpeg);
        }
        catch (MagickException)
        {
            throw new BadRequestException("That file isn't a photo we can read. Please take a selfie with the camera.");
        }
    }
}
