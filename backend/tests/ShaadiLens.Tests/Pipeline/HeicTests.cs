using FluentAssertions;
using ImageMagick;
using Microsoft.Extensions.Options;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Infrastructure.Processing;
using ShaadiLens.Infrastructure.Storage;

namespace ShaadiLens.Tests.Pipeline;

/// <summary>iPhone photos: HEIC original → converted to JPEG once, then web/thumb as usual.</summary>
public sealed class HeicTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shaadilens-heic", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Heic_original_is_converted_to_jpeg_and_resized()
    {
        // A real HEIC (HEVC-encoded) 600x800 portrait, generated with libheif's heif-enc.
        // (Magick.NET can decode HEIC but has no encoder, so the fixture is a committed file.)
        var heicBytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portrait-600x800.heic"));

        var storage = new LocalDiskStorage(Options.Create(new StorageOptions { Root = _root }));
        var photo = new Photo { EventId = Guid.NewGuid(), OriginalFileName = "IMG_0042.HEIC" };
        photo.StorageKeyOriginal = $"events/{photo.EventId}/photos/{photo.Id}/original.heic";
        await storage.SaveAsync(photo.StorageKeyOriginal, new MemoryStream(heicBytes));

        await new ImageProcessor(storage).PrepareAsync(photo, null, CancellationToken.None);

        photo.StorageKeyOriginal.Should().EndWith("original.jpg");
        (await storage.ExistsAsync($"events/{photo.EventId}/photos/{photo.Id}/original.heic")).Should().BeFalse();
        using var original = new MagickImage(storage.GetPath(photo.StorageKeyOriginal));
        original.Format.Should().Be(MagickFormat.Jpeg);
        (photo.Width, photo.Height).Should().Be((600, 800));

        using var thumb = new MagickImage(storage.GetPath(photo.StorageKeyThumb!));
        (thumb.Width, thumb.Height).Should().Be((300u, 400u));
        using var web = new MagickImage(storage.GetPath(photo.StorageKeyWeb!));
        (web.Width, web.Height).Should().Be((600u, 800u), "small images are never upscaled");
    }
}
