using Microsoft.EntityFrameworkCore;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Guests;

/// <summary>
/// "That's me" / "Not me" — from the guest or from the admin's Match Review. Both go through here so the
/// rules are the same:
///
///   Confirmed  → shown in "Your photos" forever, AND the confirmed face's embedding becomes a new
///                GuestReference (Source = ConfirmedMatch). The next "Find more" searches with it too:
///                a side profile or a photo with a veil that the selfie alone didn't find now finds
///                similar photos. This is called query expansion.
///   Rejected   → never shown again; if it had been confirmed, its reference is removed again (otherwise a
///                wrong face would keep pulling in more wrong photos).
///
/// Automatic matching never overwrites Confirmed/Rejected rows (see PgvectorFaceMatcher).
/// </summary>
public sealed class MatchFeedbackService(IAppDbContext db, TimeProvider time)
{
    /// <summary>A guest can't collect more references than this (keeps search time predictable).</summary>
    public const int MaxConfirmedReferences = 50;

    public async Task SetDecisionAsync(GuestMatch match, MatchStatus decision, string actor, CancellationToken ct)
    {
        if (decision is not (MatchStatus.Confirmed or MatchStatus.Rejected))
            throw new BadRequestException("Status must be Confirmed or Rejected.");
        if (match.Status == decision) return;

        var reference = await db.GuestReferences
            .FirstOrDefaultAsync(r => r.GuestId == match.GuestId && r.FaceId == match.FaceId, ct);

        if (decision == MatchStatus.Confirmed && reference is null)
        {
            var count = await db.GuestReferences.CountAsync(r => r.GuestId == match.GuestId && r.Source == ReferenceSource.ConfirmedMatch, ct);
            var embedding = await db.Faces.Where(f => f.Id == match.FaceId).Select(f => f.Embedding).FirstOrDefaultAsync(ct);
            if (embedding is not null && count < MaxConfirmedReferences)
            {
                db.GuestReferences.Add(new GuestReference
                {
                    GuestId = match.GuestId, FaceId = match.FaceId, Embedding = embedding,
                    Source = ReferenceSource.ConfirmedMatch, CreatedAt = time.GetUtcNow(),
                });
            }
        }
        else if (decision == MatchStatus.Rejected && reference is { Source: ReferenceSource.ConfirmedMatch })
        {
            db.GuestReferences.Remove(reference);
        }

        match.Status = decision;
        db.AuditLogs.Add(new AuditLog
        {
            Actor = actor, Action = decision == MatchStatus.Confirmed ? "match_confirmed" : "match_rejected",
            EntityId = match.PhotoId.ToString(),
        });
        await db.SaveChangesAsync(ct);
    }
}
