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
    bool AllowSingleDownload, bool AllowBulkDownload, int MatchCount, int ProbableCount, DateTimeOffset? LastSearchedAt);

public sealed record GuestPhotoDto(Guid Id, Guid CollectionId, int Width, int Height, DateTimeOffset? TakenAt, string ThumbUrl, string WebUrl);

public sealed record GuestCollectionDto(Guid Id, string Name, int PhotoCount);

public sealed record ZipEntrySource(Guid PhotoId, string StorageKey, string FileName, string CollectionName);

/// <summary>Thrown when the selfie isn't usable; the API returns 422 with the code + friendly reason.</summary>
public sealed class SelfieRejectedException(string code, string reason) : Exception(reason)
{
    public string Code { get; } = code;
}
