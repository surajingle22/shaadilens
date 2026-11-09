using System.Diagnostics;
using System.Text;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ShaadiLens.Infrastructure.Reels;

public sealed class ReelRenderOptions
{
    public const string Section = "Reels";
    /// <summary>The ffmpeg executable (installed in the reel-worker image).</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int TimeoutMinutes { get; set; } = 10;
}

/// <param name="ImagePath">Photo file on disk (the original, or the web image if the original is missing).</param>
/// <param name="FaceX">Guest's face centre, 0..1 of the upright photo.</param>
public sealed record ReelSource(string ImagePath, float FaceX, float FaceY);

public sealed record ReelRenderRequest(
    IReadOnlyList<ReelSource> Shots, string Title, string Subtitle, string Watermark, string? MusicPath,
    string OutputPath, double SecondsPerPhoto, double Crossfade);

/// <summary>Turns a list of photos into an MP4. Swapped for a fake in tests (no ffmpeg in the test container).</summary>
public interface IReelRenderer
{
    Task RenderAsync(ReelRenderRequest request, CancellationToken ct);
}

/// <summary>
///   1. Magick.NET: cut a 9:16 frame around the guest's face out of each photo (1.5× video size)
///   2. Magick.NET: title card + watermark as transparent PNGs (no text escaping problems in ffmpeg)
///   3. ffmpeg: Ken Burns zoom + cross-fades + overlays + music → H.264 MP4 (see <see cref="ReelVideo"/>)
/// </summary>
public sealed class FfmpegReelRenderer(IOptions<ReelRenderOptions> options, ILogger<FfmpegReelRenderer> log) : IReelRenderer
{
    private static readonly string Fonts = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");

    public async Task RenderAsync(ReelRenderRequest r, CancellationToken ct)
    {
        var work = Directory.CreateTempSubdirectory("shaadilens-reel-");
        try
        {
            // Decoding 24 MP originals is the slow part of this step: do it on all the cores the container has.
            var frames = new (string, float, float)[r.Shots.Count];
            await Parallel.ForAsync(0, r.Shots.Count,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                (i, _) =>
                {
                    var path = Path.Combine(work.FullName, $"frame{i:D2}.jpg");
                    var (fx, fy) = MakeFrame(r.Shots[i], path);
                    frames[i] = (path, fx, fy);
                    return ValueTask.CompletedTask;
                });
            var title = Path.Combine(work.FullName, "title.png");
            var watermark = Path.Combine(work.FullName, "watermark.png");
            MakeTitle(r.Title, r.Subtitle, title);
            MakeWatermark(r.Watermark, watermark);

            var temp = Path.Combine(work.FullName, "reel.mp4");
            var args = ReelVideo.Arguments(frames, title, watermark, r.MusicPath, temp, r.SecondsPerPhoto, r.Crossfade);
            await RunFfmpegAsync(args, ct);

            Directory.CreateDirectory(Path.GetDirectoryName(r.OutputPath)!);
            File.Move(temp, r.OutputPath, overwrite: true);
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Upright photo → 9:16 crop around the face → 1620×2880 JPEG. Returns the face position inside the frame.</summary>
    private static (float, float) MakeFrame(ReelSource s, string output)
    {
        var settings = new MagickReadSettings();
        // JPEG "size hint": the decoder may skip detail we'd throw away anyway (much faster on 24 MP originals).
        settings.SetDefine(MagickFormat.Jpeg, "size", $"{ReelVideo.FrameHeight}x{ReelVideo.FrameHeight}");
        using var image = new MagickImage(s.ImagePath, settings);
        image.AutoOrient();
        var c = ReelVideo.Crop((int)image.Width, (int)image.Height, s.FaceX, s.FaceY);
        image.Crop(new MagickGeometry(c.X, c.Y, (uint)c.Width, (uint)c.Height));
        image.ResetPage();
        image.Resize(new MagickGeometry((uint)ReelVideo.FrameWidth, (uint)ReelVideo.FrameHeight) { IgnoreAspectRatio = true });
        image.Strip();
        image.Quality = 90;
        image.Write(output, MagickFormat.Jpeg);
        return (c.FocusX, c.FocusY);
    }

    /// <summary>Event name + "Riya's highlights" over a dark gradient at the bottom; faded out by ffmpeg after ~2 s.</summary>
    private static void MakeTitle(string title, string subtitle, string output)
    {
        using var canvas = new MagickImage(MagickColors.Transparent, ReelVideo.Width, ReelVideo.Height);
        using (var shade = new MagickImage("gradient:#00000000-#000000C0", ReelVideo.Width, 760))
            canvas.Composite(shade, 0, ReelVideo.Height - 760, CompositeOperator.Over);
        DrawFitted(canvas, Clean(title), Path.Combine(Fonts, "PlayfairDisplay.ttf"), 92, 300, MagickColors.White);
        DrawFitted(canvas, Clean(subtitle), Path.Combine(Fonts, "Poppins-Regular.ttf"), 46, 220, new MagickColor(255, 255, 255, 220));
        canvas.Write(output, MagickFormat.Png);
    }

    /// <summary>Small semi-transparent text, bottom-right, with a soft shadow (readable on any photo).</summary>
    private static void MakeWatermark(string text, string output)
    {
        using var canvas = new MagickImage(MagickColors.Transparent, ReelVideo.Width, ReelVideo.Height);
        var clean = Clean(text);
        if (clean.Length > 0)
            foreach (var (color, offset) in new[] { (new MagickColor(0, 0, 0, 110), 2), (new MagickColor(255, 255, 255, 190), 0) })
                new Drawables().Font(Path.Combine(Fonts, "Poppins-SemiBold.ttf")).FontPointSize(30).FillColor(color)
                    .Gravity(Gravity.Southeast).Text(40 - offset, 60 - offset, clean).Draw(canvas);
        canvas.Write(output, MagickFormat.Png);
    }

    /// <summary>Centred text, made smaller until it fits the width (long event names).</summary>
    private static void DrawFitted(MagickImage canvas, string text, string font, double size, int fromBottom, IMagickColor<byte> color)
    {
        if (text.Length == 0) return;
        const int maxWidth = ReelVideo.Width - 120;
        while (size > 24 && new Drawables().Font(font).FontPointSize(size).FontTypeMetrics(text)?.TextWidth > maxWidth)
            size -= 4;
        new Drawables().Font(font).FontPointSize(size).FillColor(color).Gravity(Gravity.South).Text(0, fromBottom, text).Draw(canvas);
    }

    /// <summary>
    /// Text comes from the admin (event name) and the guest (their name). ImageMagick gives a leading '@' (read a
    /// file) and '%' (property escapes) special meaning in some places, so keep only plain, printable text.
    /// </summary>
    public static string Clean(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Trim())
            if (!char.IsControl(ch) && ch is not ('%' or '\\' or '@')) sb.Append(ch);
        var s = sb.ToString().Trim();
        return s.Length > 60 ? s[..60].TrimEnd() + "…" : s;
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(options.Value.FfmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a); // no shell: nothing in the arguments is interpreted

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg");
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(options.Value.TimeoutMinutes));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        var errors = (await stderr).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}): {(errors.Length > 400 ? errors[^400..] : errors)}");
        log.LogInformation("ffmpeg finished in {Seconds:0.0} s", sw.Elapsed.TotalSeconds);
    }
}
