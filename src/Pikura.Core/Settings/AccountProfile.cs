namespace Pikura.Core.Settings;

/// <summary>
/// Per-account overrides for download settings.
/// Any property left <c>null</c> means "use the global setting".
/// </summary>
public sealed class AccountSettings
{
    public bool UseAccountSettings { get; set; } = false;

    // Storage
    public string? DownloadRoot      { get; set; }
    public string? FolderTemplate    { get; set; }
    public string? FilenameTemplate  { get; set; }

    // Filtering
    public bool? FilterAiGenerated { get; set; }
    public bool? SkipR18           { get; set; }
    public bool? SkipR18G          { get; set; }
    public bool? SeparateR18Folder { get; set; }

    // Control
    public int? MaxConcurrentDownloads { get; set; }

    // Download behavior
    public bool? AllowRedownload { get; set; }
}

/// <summary>
/// Represents a single Pixiv account that has been signed into Pikura.
/// Sensitive fields (<see cref="PhpSessId"/>, <see cref="RefreshToken"/>) are
/// stored encrypted via <see cref="CredentialStore"/> — only plaintext values
/// are ever held in memory.
/// </summary>
public sealed class AccountProfile
{
    /// <summary>Stable identifier for this profile (generated once on creation).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Pixiv user ID resolved after successful login.</summary>
    public string? UserId { get; set; }

    /// <summary>Display name shown in the account switcher.</summary>
    public string? UserName { get; set; }

    /// <summary>Whether this account has a Pixiv Premium subscription (resolved after a successful session check).</summary>
    public bool IsPremium { get; set; }

    /// <summary>Pixiv PHPSESSID cookie — plaintext in memory, encrypted on disk.</summary>
    public string PhpSessId { get; set; } = string.Empty;

    /// <summary>OAuth refresh token for App API — plaintext in memory, encrypted on disk.</summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Per-account download settings overrides. Null = use global settings.</summary>
    public AccountSettings? Settings { get; set; }

    /// <summary>When this profile was first added.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this profile last signed in successfully.</summary>
    public DateTime? LastUsedAt { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PhpSessId);

    public string DisplayLabel => UserName ?? UserId ?? "Unknown account";
}
