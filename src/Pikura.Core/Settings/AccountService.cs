using System.Text.Json;

namespace Pikura.Core.Settings;

/// <summary>
/// Manages multiple Pixiv account profiles.
/// Profiles are persisted to <c>%APPDATA%\Pikura\accounts.json</c>.
/// Sensitive fields are encrypted via <see cref="CredentialStore"/>.
/// Switching accounts updates the active <see cref="SettingsService"/> session
/// so all downstream services pick up the new credentials immediately.
/// </summary>
public sealed class AccountService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly SettingsService _settings;
    private readonly Pikura.Core.Services.LocalFavoritesService? _favoritesService;
    private readonly Lock _gate = new();

    private List<AccountProfile> _profiles = new();

    public IReadOnlyList<AccountProfile> Profiles => _profiles;
    public AccountProfile? ActiveProfile { get; private set; }

    public event EventHandler? ProfilesChanged;
    public event EventHandler? ActiveProfileChanged;

    public AccountService(SettingsService settings, Pikura.Core.Services.LocalFavoritesService? favoritesService = null, string? overridePath = null)
    {
        _settings = settings;
        _favoritesService = favoritesService;
        _path = overridePath ?? DefaultPath();
        Load();
        SyncFromSettings();
        // Ensure LocalFavoritesService points to the active profile's file on startup,
        // even when the profile already existed in accounts.json (SyncFromSettings returns early).
        if (ActiveProfile != null)
            _favoritesService?.SwitchUser(ActiveProfile.UserId);
    }

    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pikura", "accounts.json");

    // ── Profile CRUD ──────────────────────────────────────────────────────────

    /// <summary>
    /// Adds or updates a profile matching the current settings session.
    /// Called after a successful login.
    /// </summary>
    public AccountProfile UpsertFromCurrentSession()
    {
        lock (_gate)
        {
            var s = _settings.Current;
            var existing = _profiles.FirstOrDefault(p => p.UserId == s.UserId);
            if (existing != null)
            {
                existing.UserName     = s.UserName;
                existing.IsPremium    = s.IsPremium;
                existing.PhpSessId    = s.PhpSessId;
                existing.RefreshToken = s.RefreshToken;
                existing.LastUsedAt   = DateTime.UtcNow;
                ActiveProfile         = existing;
            }
            else
            {
                var profile = new AccountProfile
                {
                    UserId       = s.UserId,
                    UserName     = s.UserName,
                    IsPremium    = s.IsPremium,
                    PhpSessId    = s.PhpSessId,
                    RefreshToken = s.RefreshToken,
                    LastUsedAt   = DateTime.UtcNow,
                };
                _profiles.Add(profile);
                ActiveProfile = profile;
            }
            Save();
        }
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        return ActiveProfile!;
    }

    /// <summary>Switches the active account and updates the session in <see cref="SettingsService"/>.</summary>
    public void SwitchTo(string profileId)
    {
        AccountProfile? profile;
        lock (_gate)
        {
            profile = _profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile is null) return;
            ActiveProfile = profile;
            profile.LastUsedAt = DateTime.UtcNow;
            Save();
        }

        _settings.Update(s =>
        {
            s.PhpSessId    = profile.PhpSessId;
            s.RefreshToken = profile.RefreshToken;
            s.UserId       = profile.UserId;
            s.UserName     = profile.UserName;
            s.IsPremium    = profile.IsPremium;
        });

        _favoritesService?.SwitchUser(profile.UserId);
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Persists updated <see cref="AccountSettings"/> for the given profile.
    /// </summary>
    public void SaveAccountSettings(string profileId, AccountSettings accountSettings)
    {
        lock (_gate)
        {
            var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile is null) return;
            profile.Settings = accountSettings;
            Save();
        }
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Effective-value resolvers ─────────────────────────────────────────────
    // Each method returns the per-account override when enabled, else the global.

    public string GetEffectiveDownloadRoot()       => Effective(s => s.DownloadRoot,    g => g.DownloadRoot);
    public string GetEffectiveFolderTemplate()     => Effective(s => s.FolderTemplate,  g => g.FolderTemplate);
    public string GetEffectiveFilenameTemplate()   => Effective(s => s.FilenameTemplate, g => g.FilenameTemplate);
    public bool   GetEffectiveFilterAiGenerated()  => Effective(s => s.FilterAiGenerated, g => g.FilterAiGenerated);
    public bool   GetEffectiveSkipR18()            => Effective(s => s.SkipR18,          g => g.R18Mode == R18Mode.Off);
    public bool   GetEffectiveSkipR18G()           => Effective(s => s.SkipR18G,         g => g.R18Type == R18TypeFilter.R18);
    public bool   GetEffectiveSeparateR18Folder()  => Effective(s => s.SeparateR18Folder, g => g.SeparateR18Folder);
    public int    GetEffectiveMaxConcurrent()      => Effective(s => s.MaxConcurrentDownloads, g => g.MaxConcurrentDownloads);

    private T Effective<T>(Func<AccountSettings, T?> pick, Func<AppSettings, T> fallback)
        where T : struct
    {
        var acct = ActiveProfile?.Settings;
        if (acct is { UseAccountSettings: true })
        {
            var v = pick(acct);
            if (v.HasValue) return v.Value;
        }
        return fallback(_settings.Current);
    }

    private string Effective(Func<AccountSettings, string?> pick, Func<AppSettings, string> fallback)
    {
        var acct = ActiveProfile?.Settings;
        if (acct is { UseAccountSettings: true })
        {
            var v = pick(acct);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return fallback(_settings.Current);
    }

    /// <summary>Removes a profile. If it was active, clears the current session.</summary>
    public void Remove(string profileId)
    {
        lock (_gate)
        {
            var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile is null) return;
            _profiles.Remove(profile);
            if (ActiveProfile?.Id == profileId)
            {
                ActiveProfile = _profiles.FirstOrDefault();
                if (ActiveProfile != null)
                    SwitchTo(ActiveProfile.Id);
                else
                    _settings.Update(s => { s.PhpSessId = string.Empty; s.UserId = null; s.UserName = null; });
            }
            Save();
        }
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return;
            try
            {
                using var fs = File.OpenRead(_path);
                var loaded = JsonSerializer.Deserialize<List<AccountProfile>>(fs, JsonOptions) ?? new();
                foreach (var p in loaded)
                {
                    p.PhpSessId    = CredentialStore.Unprotect(p.PhpSessId);
                    p.RefreshToken = CredentialStore.Unprotect(p.RefreshToken);
                }
                _profiles = loaded;
                // Restore the active profile to the most recently used one
                ActiveProfile = _profiles
                    .OrderByDescending(p => p.LastUsedAt ?? DateTime.MinValue)
                    .FirstOrDefault();
            }
            catch { _profiles = new(); }
        }
    }

    private void Save()
    {
        var toWrite = _profiles.Select(p => new AccountProfile
        {
            Id           = p.Id,
            UserId       = p.UserId,
            UserName     = p.UserName,
            IsPremium    = p.IsPremium,
            PhpSessId    = CredentialStore.Protect(p.PhpSessId),
            RefreshToken = CredentialStore.Protect(p.RefreshToken),
            Settings     = p.Settings,
            CreatedAt    = p.CreatedAt,
            LastUsedAt   = p.LastUsedAt,
        }).ToList();

        using var fs = File.Create(_path);
        JsonSerializer.Serialize(fs, toWrite, JsonOptions);
    }

    /// <summary>
    /// On first run there may be an existing session in settings.json with no
    /// accounts.json yet — migrate it into a profile automatically.
    /// </summary>
    private void SyncFromSettings()
    {
        var s = _settings.Current;
        if (!s.IsConfigured) return;
        if (_profiles.Any(p => p.UserId == s.UserId)) return;

        var profile = new AccountProfile
        {
            UserId       = s.UserId,
            UserName     = s.UserName,
            IsPremium    = s.IsPremium,
            PhpSessId    = s.PhpSessId,
            RefreshToken = s.RefreshToken,
            LastUsedAt   = DateTime.UtcNow,
        };
        lock (_gate)
        {
            _profiles.Add(profile);
            ActiveProfile = profile;
            Save();
        }
        _favoritesService?.SwitchUser(profile.UserId);
    }
}
