using ImageMagick;
using ImageMagick.Drawing;
using QRCoder;
using ShaadiLens.Application.Abstractions;

namespace ShaadiLens.Infrastructure.Sharing;

/// <summary>
/// QR codes (QRCoder) and printable posters (Magick.NET) for share links.
/// Posters are portrait PNGs at 150 dpi: A4 = 1240×1754 px, A5 = 874×1240 px — print at "actual size".
/// Fonts ship with the app (Assets/Fonts, SIL Open Font License) so rendering is identical everywhere.
/// </summary>
public sealed class ShareArtwork : IShareArtwork
{
    private static readonly string FontDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
    private static readonly string TitleFont = Path.Combine(FontDir, "PlayfairDisplay.ttf");
    private static readonly string BodyFont = Path.Combine(FontDir, "Poppins-Regular.ttf");
    private static readonly string BoldFont = Path.Combine(FontDir, "Poppins-SemiBold.ttf");

    public byte[] QrPng(string url, int pixelsPerModule = 20)
    {
        // ECC level Q: still scans with ~25% of the code damaged (creased poster, glare on a table card).
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    public byte[] PosterPng(PosterInfo info, string size)
    {
        var (w, h) = size == "a5" ? (874u, 1240u) : (1240u, 1754u);
        var s = w / 1240.0; // scale factor so A5 is a shrunk A4
        var theme = ParseColor(info.ThemeColor);

        using var poster = new MagickImage(new MagickColor("#FFF8F2"), w, h);

        // Double border in the theme colour.
        new Drawables()
            .StrokeColor(theme).StrokeWidth(6 * s).FillColor(MagickColors.Transparent)
            .Rectangle(40 * s, 40 * s, w - 40 * s, h - 40 * s)
            .StrokeWidth(2 * s)
            .Rectangle(60 * s, 60 * s, w - 60 * s, h - 60 * s)
            .Draw(poster);

        // Render every block first, then stack them centred vertically: (image, space above it).
        var blocks = new List<(MagickImage Image, double Gap)>
        {
            (Caption(info.Title, TitleFont, 84 * s, theme, w, s), 0),
        };
        if (info.Subtitle is not null)
            blocks.Add((Caption(info.Subtitle, BodyFont, 34 * s, new MagickColor("#5A4A42"), w, s), 14 * s));

        var qr = new MagickImage(QrPng(info.Url, 24));
        qr.FilterType = FilterType.Point;          // keep modules perfectly sharp
        qr.Resize((uint)(760 * s), (uint)(760 * s));
        blocks.Add((qr, 70 * s));

        var callToAction = info.IsFullGallery
            ? "Scan to see all the wedding photos"
            : "Scan  ·  Take a selfie  ·  Get your photos";
        blocks.Add((Caption(callToAction, BoldFont, 42 * s, theme, w, s), 50 * s));

        var footer = info.IsFullGallery
            ? "No app needed — opens in your phone's browser."
            : "No app needed. Your selfie is used only to find your photos at this event, and you can delete it anytime.";
        blocks.Add((Caption(footer, BodyFont, 22 * s, new MagickColor("#7A6A62"), w, s), 18 * s));

        var total = blocks.Sum(b => b.Image.Height + b.Gap);
        var y = (h - total) / 2;
        foreach (var (image, gap) in blocks)
        {
            y += gap;
            poster.Composite(image, (int)((w - image.Width) / 2), (int)y, CompositeOperator.Over);
            y += image.Height;
            image.Dispose();
        }

        poster.Density = new Density(150, 150, DensityUnit.PixelsPerInch);
        return poster.ToByteArray(MagickFormat.Png);
    }

    /// <summary>Centered, word-wrapped text block rendered with a bundled font.</summary>
    private static MagickImage Caption(string text, string font, double pointSize, IMagickColor<byte> color, uint canvasWidth, double s)
    {
        var settings = new MagickReadSettings
        {
            Font = font,
            FontPointsize = pointSize,
            FillColor = color,
            BackgroundColor = MagickColors.Transparent,
            TextGravity = Gravity.Center,
            Width = (uint)(canvasWidth - 220 * s),
        };
        // ImageMagick treats a leading '@' as "read text from this file" and '%' as an escape — neutralise both.
        var safe = text.Replace("%", "%%").Replace("\\", "\\\\");
        if (safe.StartsWith('@')) safe = " " + safe;
        return new MagickImage($"caption:{safe}", settings);
    }

    private static MagickColor ParseColor(string hex)
    {
        try { return new MagickColor(hex); }
        catch (ArgumentException) { return new MagickColor("#b0004a"); }
    }
}
