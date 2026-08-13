using System.Text.Json;

namespace Pikura.Core.Settings;

/// <summary>
/// Thread-safe JSON-backed loader/saver for <see cref="AppSettings"/>.
/// Holds an in-memory current snapshot exposed via <see cref="Current"/>.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised on the calling thread after every <see cref="Update"/> call.</summary>
    public event EventHandler? Changed;

    private bool _activeIncognito;

    /// <summary>
    /// The incognito state actually in effect right now — checked by everything that
    /// records viewing history. Distinct from <see cref="AppSettings.IncognitoModeEnabled"/>
    /// (the persistent "on by default" setting from Settings → Advanced): this in-memory
    /// flag is what the Viewed tab's toolbar toggle flips for the current session only, and
    /// it always resets to <see cref="AppSettings.IncognitoModeEnabled"/> on the next launch
    /// rather than persisting its own value. Changing the persistent setting also updates
    /// this immediately, so turning it on in Settings takes effect right away.
    /// </summary>
    public bool ActiveIncognitoEnabled
    {
        get => _activeIncognito;
        set
        {
            if (_activeIncognito == value) return;
            _activeIncognito = value;
            ActiveIncognitoChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised whenever <see cref="ActiveIncognitoEnabled"/> changes (from either the persistent setting or the session-only toggle).</summary>
    public event EventHandler? ActiveIncognitoChanged;

    public SettingsService(string? overridePath = null)
    {
        _path = overridePath ?? DefaultPath();
        Load();
        _activeIncognito = Current.IncognitoModeEnabled;
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pikura");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                Current = new AppSettings();
                return;
            }
            try
            {
                using var fs = File.OpenRead(_path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(fs, JsonOptions) ?? new AppSettings();
                // Decrypt sensitive fields (handles both legacy plaintext and ENC: prefixed values)
                loaded.PhpSessId    = CredentialStore.Unprotect(loaded.PhpSessId);
                loaded.RefreshToken = CredentialStore.Unprotect(loaded.RefreshToken);
                loaded.CfClearance  = CredentialStore.Unprotect(loaded.CfClearance);
                // Migrate legacy blacklists into the unified blocklist format.
                loaded.MigrateLegacyBlocklists();
                Current = loaded;
            }
            catch
            {
                // Corrupt file; reset.
                Current = new AppSettings();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            var toWrite = ShallowCopyWithEncryptedCredentials(Current);
            using var fs = File.Create(_path);
            JsonSerializer.Serialize(fs, toWrite, JsonOptions);
        }
    }

    /// <summary>Mutate and persist atomically.</summary>
    public void Update(Action<AppSettings> mutator)
    {
        lock (_gate)
        {
            mutator(Current);
            var toWrite = ShallowCopyWithEncryptedCredentials(Current);
            using var fs = File.Create(_path);
            JsonSerializer.Serialize(fs, toWrite, JsonOptions);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Returns a copy of <paramref name="s"/> where <c>PhpSessId</c> and
    /// <c>RefreshToken</c> are replaced with their encrypted equivalents.
    /// The in-memory <see cref="Current"/> always holds plaintext.
    /// </summary>
    private static AppSettings ShallowCopyWithEncryptedCredentials(AppSettings s)
    {
        // System.Text.Json round-trip is the safest shallow-copy for a POCO this large.
        var json = JsonSerializer.Serialize(s, JsonOptions);
        var copy = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)!;
        copy.PhpSessId    = CredentialStore.Protect(s.PhpSessId);
        copy.RefreshToken = CredentialStore.Protect(s.RefreshToken);
        copy.CfClearance  = CredentialStore.Protect(s.CfClearance);
        return copy;
    }
}
