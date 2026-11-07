using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Domain.Entities;

/// <summary>A link (and QR code) the admin hands out. The token in /g/{token} identifies it.</summary>
public class ShareLink
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    public ShareLinkType Type { get; set; }
    /// <summary>Admin-facing name, e.g. "Guests QR on tables".</summary>
    public string? Label { get; set; }
    /// <summary>32 random URL-safe characters (~190 bits) — unguessable.</summary>
    public string Token { get; set; } = "";
    /// <summary>PBKDF2 hash of the 6-digit PIN; null = no PIN.</summary>
    public string? PinHash { get; set; }
    public bool RequireRegistration { get; set; } = true;
    public RegistrationFields RegistrationFields { get; set; } = RegistrationFields.Name;
    public bool AllowSingleDownload { get; set; } = true;
    public bool AllowBulkDownload { get; set; } = true;
    public int? SelectionLimit { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Event? Event { get; set; }

    public bool IsUsable(DateTimeOffset now) => IsActive && (ExpiresAt is null || ExpiresAt > now);
}
