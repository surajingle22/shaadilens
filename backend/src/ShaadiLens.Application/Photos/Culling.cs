using System.Globalization;
using System.Numerics;
using ShaadiLens.Application.Faces;
using ShaadiLens.Domain.Entities;

namespace ShaadiLens.Application.Photos;

/// <summary>
/// Thresholds for the culling report ("which photos can the photographer skip?"). Config section "Culling".
/// The defaults were measured on real wedding photos (see docs/AI-PIPELINE.md §4g).
/// </summary>
public sealed class CullingOptions
{
    public const string Section = "Culling";

    /// <summary>Photo with people is "blurry" when even its SHARPEST face scores below this (Laplacian variance, 112px crop).</summary>
    public float BlurryFaceBelow { get; set; } = 60;
    /// <summary>Photo without faces (decor, venue) is "blurry" below this whole-image score (512px).</summary>
    public float BlurryImageBelow { get; set; } = 25;
    /// <summary>Eye aspect ratio below this = eyes closed (open ≈ 0.25–0.35).</summary>
    public float ClosedEyesBelow { get; set; } = 0.17f;
    /// <summary>Landmarks on side profiles are unreliable: ignore faces turned further than this (degrees).</summary>
    public float MaxYawForEyes { get; set; } = 35;
    /// <summary>Ignore small faces for "eyes closed" (px on the 2048px web image)…</summary>
    public float MinFacePxForEyes { get; set; } = 60;
    /// <summary>…and faces much smaller than the main subject (a blinking guest in the background doesn't ruin a portrait).</summary>
    public float MainFaceRatio { get; set; } = 0.5f;
    /// <summary>Perceptual hashes differing in at most this many of 64 bits = same picture. Different scenes differ in ~32.</summary>
    public int DuplicateMaxBits { get; set; } = 10;
    /// <summary>Burst shots must be taken within this many seconds of each other (when both have an EXIF time).</summary>
    public int BurstWindowSeconds { get; set; } = 60;
}

/// <summary>Turns the face-service's raw numbers into the culling fields of a photo.</summary>
public static class Culling
{
    /// <summary>
    /// Fill the photo's culling fields from the analysis. <paramref name="faces"/> = the faces that were KEPT
    /// (size/confidence filters already applied), with <see cref="Face.EyeAspectRatio"/> set.
    /// </summary>
    public static void Apply(Photo photo, IReadOnlyCollection<Face> faces, FaceAnalysisResult result, CullingOptions o)
    {
        photo.Sharpness = result.Sharpness is { } s ? (float)s : null;
        photo.PerceptualHash = ParseHash(result.Phash);
        photo.BlurScore = faces.Count == 0 ? null : faces.Max(f => f.QualityScore);
        photo.ClosedEyesCount = CountClosedEyes(faces, o);
    }

    public static int CountClosedEyes(IReadOnlyCollection<Face> faces, CullingOptions o)
    {
        if (faces.Count == 0) return 0;
        var minPx = Math.Max(o.MinFacePxForEyes, faces.Max(f => f.FaceSizePx) * o.MainFaceRatio);
        return faces.Count(f => f.EyeAspectRatio is { } ear && ear < o.ClosedEyesBelow
                                && Math.Abs(f.Yaw) <= o.MaxYawForEyes && f.FaceSizePx >= minPx);
    }

    /// <summary>16 hex chars → 64 bits (stored as a signed bigint; only the bits matter).</summary>
    public static long? ParseHash(string? hex) =>
        hex is { Length: 16 } && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? unchecked((long)v) : null;

    /// <summary>Number of differing bits = how different two pictures look (0 = identical layout).</summary>
    public static int Distance(long a, long b) => BitOperations.PopCount(unchecked((ulong)(a ^ b)));

    public static bool IsBlurry(Photo p, CullingOptions o) =>
        p.FaceCount > 0 ? p.BlurScore < o.BlurryFaceBelow : p.Sharpness < o.BlurryImageBelow;

    /// <summary>
    /// Mark the keeper of one burst group: the admin's own pick if there is one, otherwise <see cref="BestFirst"/>.
    /// A "group" of a single photo isn't a burst any more and is dissolved.
    /// </summary>
    public static void PickBest(IReadOnlyCollection<Photo> members)
    {
        if (members.Count < 2)
        {
            foreach (var m in members) { m.BurstGroupId = null; m.IsBurstBest = false; m.BurstPickedByAdmin = false; }
            return;
        }
        var best = members.FirstOrDefault(m => m.BurstPickedByAdmin) ?? BestFirst(members).First();
        foreach (var m in members)
        {
            m.IsBurstBest = m == best;
            if (m != best) m.BurstPickedByAdmin = false; // two merged groups may both have had a pick
        }
    }

    /// <summary>"Best of the burst": eyes open first, then the sharpest.</summary>
    public static IOrderedEnumerable<Photo> BestFirst(IEnumerable<Photo> burst) =>
        burst.OrderBy(p => p.ClosedEyesCount)
             .ThenByDescending(p => p.BlurScore ?? p.Sharpness ?? 0)
             .ThenBy(p => p.Id);
}
