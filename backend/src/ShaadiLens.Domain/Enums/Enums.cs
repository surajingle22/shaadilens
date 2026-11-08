namespace ShaadiLens.Domain.Enums;

/// <summary>Photo processing pipeline: Uploaded → Resized → FacesDetected → Matched (compared with every registered guest), or Failed.</summary>
public enum PhotoStatus
{
    Uploaded = 0,
    Resized = 1,
    FacesDetected = 2,
    Matched = 3,
    Failed = 9,
}

public enum ShareLinkType
{
    /// <summary>Guest sees only photos they're in (selfie search).</summary>
    Guest = 0,
    /// <summary>Selfie only, no registration form.</summary>
    VipGuest = 1,
    /// <summary>Whole gallery, by collection.</summary>
    Full = 2,
    /// <summary>Whole gallery + hearts for the couple's album picks (Phase 2).</summary>
    Selection = 3,
}

[Flags]
public enum RegistrationFields
{
    None = 0,
    Name = 1,
    Phone = 2,
    Email = 4,
}

public enum ReferenceSource
{
    Selfie = 0,
    ConfirmedMatch = 1,
}

public enum MatchStatus
{
    /// <summary>Score ≥ MatchThreshold: shown in "Your photos".</summary>
    Auto = 0,
    /// <summary>ProbableThreshold ≤ score &lt; MatchThreshold: shown in "Might be you?".</summary>
    Probable = 1,
    /// <summary>Guest (or admin) said "That's me". Never changed by automatic matching.</summary>
    Confirmed = 2,
    /// <summary>Guest said "Not me" — never shown again.</summary>
    Rejected = 3,
}

/// <summary>Image sizes we store per photo.</summary>
public enum PhotoVariant
{
    Thumb = 0,
    Web = 1,
    Original = 2,
}

public static class ShareLinkTypeRules
{
    /// <summary>Full and Selection links show every visible photo of the event.</summary>
    public static bool ShowsWholeGallery(this ShareLinkType type) => type is ShareLinkType.Full or ShareLinkType.Selection;

    /// <summary>Guest and VIP links find "your photos" from a selfie.</summary>
    public static bool NeedsSelfie(this ShareLinkType type) => type is ShareLinkType.Guest or ShareLinkType.VipGuest;
}
