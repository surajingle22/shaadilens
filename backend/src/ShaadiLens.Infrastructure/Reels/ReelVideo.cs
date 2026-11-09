using System.Globalization;

namespace ShaadiLens.Infrastructure.Reels;

/// <summary>Where to cut a 9:16 frame out of a photo so the guest's face is in it.</summary>
/// <param name="FocusX">The face position inside the cut-out frame, 0..1 (the slow zoom moves towards it).</param>
public readonly record struct ReelCrop(int X, int Y, int Width, int Height, float FocusX, float FocusY);

/// <summary>
/// The maths and the FFmpeg command for a reel. Pure functions (no files, no processes) so they can be tested.
///
/// A reel is N photos, each shown for <c>SecondsPerPhoto</c> with a slow "Ken Burns" zoom, joined by cross-fades:
///
///   photo 1  |=========|
///   photo 2         |=========|            each overlap = one cross-fade (xfade)
///   photo 3                |=========|
///            0        2.4      4.8      → total = N·D − (N−1)·T
/// </summary>
public static class ReelVideo
{
    public const int Width = 1080;   // 9:16 portrait, the format of Instagram Reels / WhatsApp status
    public const int Height = 1920;
    public const int Fps = 30;
    /// <summary>Frames are prepared 1.5× larger than the video so zooming in doesn't make them soft or jumpy.</summary>
    public const double Oversample = 1.5;
    public const double Zoom = 0.10; // +10% over one photo

    public static int FrameWidth => (int)(Width * Oversample);
    public static int FrameHeight => (int)(Height * Oversample);

    /// <summary>
    /// The largest 9:16 rectangle that fits in the photo, centred on the face horizontally and with the face a bit
    /// above the middle (portrait-style composition), clamped to the photo edges.
    /// </summary>
    public static ReelCrop Crop(int photoWidth, int photoHeight, float faceX, float faceY)
    {
        const double aspect = 9.0 / 16.0;
        int w, h;
        if ((double)photoWidth / photoHeight > aspect) { h = photoHeight; w = (int)Math.Round(h * aspect); }
        else { w = photoWidth; h = (int)Math.Round(w / aspect); }
        w = Math.Min(w, photoWidth);
        h = Math.Min(h, photoHeight);

        var x = (int)Math.Round(Math.Clamp(faceX * photoWidth - w / 2.0, 0, photoWidth - w));
        var y = (int)Math.Round(Math.Clamp(faceY * photoHeight - h * 0.4, 0, photoHeight - h));
        return new ReelCrop(x, y, w, h,
            (float)Math.Clamp((faceX * photoWidth - x) / w, 0, 1),
            (float)Math.Clamp((faceY * photoHeight - y) / h, 0, 1));
    }

    public static double TotalSeconds(int photos, double secondsPerPhoto, double crossfade) =>
        photos * secondsPerPhoto - (photos - 1) * crossfade;

    /// <summary>
    /// Arguments for one ffmpeg run. Inputs, in order: the N prepared frames, the title overlay (PNG, shown at the
    /// start), the watermark overlay (PNG, whole video), and optionally a music file (looped, faded out).
    /// </summary>
    public static IReadOnlyList<string> Arguments(
        IReadOnlyList<(string Path, float FocusX, float FocusY)> frames, string titlePng, string watermarkPng, string? music,
        string output, double secondsPerPhoto, double crossfade)
    {
        if (frames.Count < 2) throw new ArgumentException("A reel needs at least 2 photos", nameof(frames));
        var inv = CultureInfo.InvariantCulture;
        string N(double v) => v.ToString("0.###", inv);

        var n = frames.Count;
        var d = (int)Math.Round(secondsPerPhoto * Fps); // frames per photo
        var total = TotalSeconds(n, secondsPerPhoto, crossfade);
        const double titleSeconds = 2.6;

        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var f in frames) args.AddRange(["-i", f.Path]);
        args.AddRange(["-loop", "1", "-framerate", Fps.ToString(inv), "-t", N(titleSeconds), "-i", titlePng]);
        args.AddRange(["-loop", "1", "-framerate", Fps.ToString(inv), "-i", watermarkPng]);
        if (music is not null) args.AddRange(["-stream_loop", "-1", "-i", music]);

        var g = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var (_, fx, fy) = frames[i];
            // zoompan: `on` = output frame number, `zoom` = current zoom; x/y = top-left of the visible window,
            // kept on the face and inside the picture. Even photos zoom in, odd ones zoom out — feels less mechanical.
            var z = i % 2 == 0 ? $"1+{N(Zoom)}*on/{d - 1}" : $"{N(1 + Zoom)}-{N(Zoom)}*on/{d - 1}";
            g.Add($"[{i}:v]zoompan=z='{z}'" +
                  $":x='max(0,min(iw-iw/zoom,{N(fx)}*iw-iw/zoom/2))'" +
                  $":y='max(0,min(ih-ih/zoom,{N(fy)}*ih-ih/zoom/2))'" +
                  $":d={d}:s={Width}x{Height}:fps={Fps},setsar=1,format=yuv420p[v{i}]");
        }
        var prev = "v0";
        for (var i = 1; i < n; i++)
        {
            g.Add($"[{prev}][v{i}]xfade=transition=fade:duration={N(crossfade)}:offset={N(i * (secondsPerPhoto - crossfade))}[x{i}]");
            prev = $"x{i}";
        }
        g.Add($"[{n}:v]format=rgba,fade=t=out:st={N(titleSeconds - 0.8)}:d=0.8:alpha=1[title]");
        g.Add($"[{prev}][title]overlay=0:0:eof_action=pass[vt]");
        g.Add($"[vt][{n + 1}:v]overlay=0:0:shortest=1,format=yuv420p[vout]");
        if (music is not null)
            g.Add($"[{n + 2}:a]atrim=0:{N(total)},afade=t=in:st=0:d=1,afade=t=out:st={N(Math.Max(0, total - 2))}:d=2[aout]");

        args.AddRange(["-filter_complex", string.Join(';', g), "-map", "[vout]"]);
        if (music is not null) args.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "160k"]);
        args.AddRange([
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "22", "-pix_fmt", "yuv420p", "-r", Fps.ToString(inv),
            "-t", N(total), "-movflags", "+faststart", output,
        ]);
        return args;
    }
}
