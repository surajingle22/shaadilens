using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Guests;

/// <summary>Who is calling, taken from the guest JWT. GuestId is null for an anonymous Full-gallery viewer.</summary>
public sealed record GuestContext(Guid EventId, Guid LinkId, Guid? GuestId, ShareLinkType LinkType);

public sealed record EventBrandingDto(string Name, DateOnly? Date, string? Venue, string ThemeColor, string? TitleFont, string? CoverUrl);

/// <summary>What the landing page needs before the guest does anything.</summary>
public sealed record PublicLinkDto(
    EventBrandingDto Event, ShareLinkType LinkType, bool RequiresPin, bool RequireRegistration,
    RegistrationFields RegistrationFields, bool SelfieRequired, bool FaceSearchEnabled);

public sealed record RegisterRequest(string? Name, string? Phone, string? Email, bool Consent);

public sealed record GuestSessionDto(
    EventBrandingDto Event, ShareLinkType LinkType, string? GuestName, bool HasSelfie,
    bool AllowSingleDownload, bool AllowBulkDownload, int MatchCount, int ProbableCount, DateTimeOffset? LastSearchedAt,
    int SelfieCount, int MaxSelfies, NotificationStatusDto Notifications);

/// <param name="PushPublicKey">VAPID key for PushManager.subscribe; null = Web Push not set up on this server.</param>
/// <param name="EmailAvailable">SMTP is set up and the guest gave an email address.</param>
public sealed record NotificationStatusDto(string? PushPublicKey, bool PushOn, bool EmailAvailable, bool EmailOn);

/// <param name="Subscription">The browser's PushSubscription as JSON (null = no push / turn push off).</param>
public sealed record NotificationRequest(System.Text.Json.JsonElement? Subscription, bool Email);

public sealed record GuestPhotoDto(Guid Id, Guid CollectionId, int Width, int Height, DateTimeOffset? TakenAt, string ThumbUrl, string WebUrl);

public sealed record GuestCollectionDto(Guid Id, string Name, int PhotoCount);

/// <summary>One "story" bubble: a collection's highlights (picked by the admin), first photo as the cover.</summary>
public sealed record HighlightStoryDto(Guid CollectionId, string Name, IReadOnlyList<GuestPhotoDto> Photos);

public sealed record ZipEntrySource(Guid PhotoId, string StorageKey, string FileName, string CollectionName);

/// <summary>Thrown when the selfie isn't usable; the API returns 422 with the code + friendly reason.</summary>
public sealed class SelfieRejectedException(string code, string reason) : Exception(reason)
{
    public string Code { get; } = code;
}
