namespace ShaadiLens.Application.Reels;

/// <summary>One of the guest's photos, with the guest's own face in it (from their GuestMatch).</summary>
/// <param name="FaceX">Centre of the guest's face, 0..1 of the photo width (the video crop is centred on it).</param>
/// <param name="FaceY">Centre of the guest's face, 0..1 of the photo height.</param>
/// <param name="FacePx">Face size in pixels on the 2048px web image: bigger = they are the subject, not a bystander.</param>
/// <param name="FaceSharpness">Laplacian variance of the face crop.</param>
/// <param name="EyeAspectRatio">~0.3 open, &lt; 0.17 closed; null = unknown.</param>
public sealed record ReelCandidate(
    Guid PhotoId, Guid CollectionId, DateTimeOffset When, float FaceX, float FaceY, float FacePx, float FaceSharpness,
    float? EyeAspectRatio, float Yaw, bool Confirmed, bool IsHighlight, Guid? BurstGroupId);

/// <summary>One shot of the reel: which photo, and where to centre the 9:16 crop.</summary>
public sealed record ReelShot(Guid PhotoId, float FocusX, float FocusY);

/// <summary>
/// Picks the photos for a guest's reel. Plain C#, no database — easy to test.
///
///   1. score every photo: big face (they're the subject) × sharp face × eyes open (+ small bonus for photos the
///      guest confirmed and for the admin's highlights)
///   2. one photo per burst (near-duplicates make a boring video)
///   3. spread over the collections (Haldi, Sangeet, Wedding…) — round-robin, best first
///   4. play them in the order they were taken
/// </summary>
public static class ReelPlanner
{
    public const float ClosedEyesBelow = 0.17f;

    public static double Score(ReelCandidate c)
    {
        var s = Math.Log(1 + c.FacePx);                               // 120 px → 4.8, 600 px → 6.4
        s *= Math.Clamp(c.FaceSharpness / 150.0, 0.2, 1.0);           // soft faces count less
        if (c.EyeAspectRatio < ClosedEyesBelow && Math.Abs(c.Yaw) <= 35) s *= 0.3;
        if (c.Confirmed) s *= 1.15;
        if (c.IsHighlight) s *= 1.2;
        return s;
    }

    public static IReadOnlyList<ReelShot> Pick(IEnumerable<ReelCandidate> candidates, int count)
    {
        var scored = candidates.Select(c => (c, score: Score(c))).ToList();

        // One per burst: keep the best-scoring frame of each group of near-duplicates.
        var unique = scored
            .GroupBy(x => x.c.BurstGroupId ?? x.c.PhotoId)
            .Select(g => g.MaxBy(x => x.score))
            .ToList();

        // Round-robin over collections, each collection's photos best first; collections with the best photo go first.
        var queues = unique
            .GroupBy(x => x.c.CollectionId)
            .Select(g => new Queue<(ReelCandidate c, double score)>(g.OrderByDescending(x => x.score).ThenBy(x => x.c.PhotoId)))
            .OrderByDescending(q => q.Peek().score)
            .ToList();
        var picked = new List<ReelCandidate>();
        while (picked.Count < count && queues.Any(q => q.Count > 0))
            foreach (var q in queues)
                if (q.Count > 0 && picked.Count < count) picked.Add(q.Dequeue().c);

        return picked.OrderBy(c => c.When).ThenBy(c => c.PhotoId)
            .Select(c => new ReelShot(c.PhotoId, c.FaceX, c.FaceY))
            .ToList();
    }
}
