using FluentValidation;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Events;

public sealed record FaceSettingsDto(float MatchThreshold, float ProbableThreshold, int MinFaceSizePx, float MinDetScore, float MaxSelfieYaw)
{
    public static FaceSettingsDto From(FaceSettings s) => new(s.MatchThreshold, s.ProbableThreshold, s.MinFaceSizePx, s.MinDetScore, s.MaxSelfieYaw);
}

public sealed record CollectionDto(Guid Id, string Name, int SortOrder, bool IsHidden, int PhotoCount);

public sealed record EventSummaryDto(
    Guid Id, string Name, string Slug, DateOnly? Date, string? Venue, bool IsPublished,
    int PhotoCount, int GuestCount, DateTimeOffset CreatedAt);

public sealed record EventDto(
    Guid Id, string Name, string Slug, DateOnly? Date, string? Venue, string ThemeColor, string? TitleFont,
    bool WatermarkEnabled, string? WatermarkText, bool IsPublished, bool FaceSearchEnabled, Guid? CoverPhotoId, string? CoverUrl,
    DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt, FaceSettingsDto FaceSettings,
    IReadOnlyList<CollectionDto> Collections);

public sealed record SaveEventRequest(
    string Name, DateOnly? Date, string? Venue, string? ThemeColor, string? TitleFont,
    bool WatermarkEnabled, string? WatermarkText, bool FaceSearchEnabled, DateTimeOffset? ExpiresAt);

public sealed record SaveCollectionRequest(string Name, bool IsHidden);

public sealed record EventStatsDto(
    int Photos, int Processed, int Pending, int Failed, int Faces, int Guests, int Matches,
    IReadOnlyDictionary<PhotoStatus, int> ByStatus);

public sealed class SaveEventRequestValidator : AbstractValidator<SaveEventRequest>
{
    public SaveEventRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Venue).MaximumLength(200);
        RuleFor(x => x.ThemeColor).Matches("^#[0-9a-fA-F]{6}$").When(x => x.ThemeColor is not null)
            .WithMessage("Theme colour must look like #b0004a");
        RuleFor(x => x.WatermarkText).MaximumLength(80);
    }
}

public sealed class SaveCollectionRequestValidator : AbstractValidator<SaveCollectionRequest>
{
    public SaveCollectionRequestValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
}
