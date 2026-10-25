using System.Text.Json;

namespace Pikura.Core.Settings;

/// <summary>
/// Thread-safe JSON-backed loader/saver for <see cref="AppSettings"/>.
/// Holds an in-memory current snapshot exposed via <see cref="Current"/>.
/// Saves are debounced onto a background writer and written atomically via
/// settings.json.tmp → File.Replace (leaving settings.json.bak) so a crash
/// mid-write can never corrupt the file.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly TimeSpan _debounce;

    private readonly object _saveSync = new();
    private bool _savePending;
    private bool _saveInFlight;
    private bool _saveCancelled;
    private Task? _saveLoop;

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

    public SettingsService(string? overridePath = null, TimeSpan? debounceInterval = null)
    {
        _path = overridePath ?? DefaultPath();
        _debounce = debounceInterval ?? TimeSpan.FromMilliseconds(250);
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
            AppSettings? loaded = null;
            var used = _path;
            try
            {
                if (File.Exists(_path))
                {
                    using var fs = File.OpenRead(_path);
                    loaded = JsonSerializer.Deserialize<AppSettings>(fs, JsonOptions);
                }
            }
            catch { /* fall through to .bak */ }
            if (loaded == null)
            {
                var bak = _path + ".bak";
                try
                {
                    if (File.Exists(bak))
                    {
                        using var fs = File.OpenRead(bak);
                        loaded = JsonSerializer.Deserialize<AppSettings>(fs, JsonOptions);
                        used = bak;
                    }
                }
                catch { /* defaults */ }
            }
            if (loaded == null) { Current = new AppSettings(); return; }
            if (used != _path)
                System.Diagnostics.Debug.WriteLine($"[Settings] Loaded from backup {used}");

            var saveAfterLoad = false;
            // Decrypt sensitive fields (handles both legacy plaintext and ENC: prefixed values)
            loaded.PhpSessId    = CredentialStore.Unprotect(loaded.PhpSessId);
            loaded.RefreshToken = CredentialStore.Unprotect(loaded.RefreshToken);
            loaded.CfClearance  = CredentialStore.Unprotect(loaded.CfClearance);
            loaded.MigrateLegacyBlocklists();

            // Thumbnail fetching (especially through VPN/proxy software) relies on the
            // system proxy being honored. Older settings had this off by default.
            if (!loaded.UseProxy && string.IsNullOrWhiteSpace(loaded.ProxyAddress))
            {
                loaded.UseProxy = true;
                saveAfterLoad = true;
            }

            Current = loaded;
            if (saveAfterLoad) Save();
        }
    }

    /// <summary>Schedules a background save (debounced — multiple calls coalesce).</summary>
    public void Save()
    {
        lock (_saveSync)
        {
            if (_saveCancelled) return;
            _savePending = true;
            if (_saveLoop is { IsCompleted: false }) return;
            _saveLoop = Task.Run(SaveLoopAsync);
        }
    }

    /// <summary>Mutate in memory and schedule a save.</summary>
    public void Update(Action<AppSettings> mutator)
    {
        lock (_gate)
        {
            mutator(Current);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        Save();
    }

    /// <summary>
    /// Writes immediately when a save is pending and waits for any in-flight
    /// write. Call on every process-exit path.
    /// </summary>
    public void Flush()
    {
        Task? loop;
        lock (_saveSync)
        {
            if (_saveCancelled) return;
            if (_savePending && !_saveInFlight)
            {
                _savePending = false;
                WriteNow();
            }
            loop = _saveLoop;
        }
        if (loop != null)
        {
            try { loop.Wait(TimeSpan.FromSeconds(10)); } catch { }
            lock (_saveSync)
            {
                if (_savePending && !_saveInFlight)
                {
                    _savePending = false;
                    WriteNow();
                }
            }
        }
    }

    /// <summary>Stops all further saves for the rest of the process lifetime (used before the
    /// data directory is deleted, so nothing can recreate settings.json afterwards).</summary>
    public void CancelPendingSaves()
    {
        lock (_saveSync)
        {
            _savePending = false;
            _saveCancelled = true;
        }
    }

    private async Task SaveLoopAsync()
    {
        while (true)
        {
            await Task.Delay(_debounce).ConfigureAwait(false);
            lock (_saveSync)
            {
                if (!_savePending || _saveCancelled)
                {
                    _savePending = false;
                    return;
                }
                _savePending = false;
                _saveInFlight = true;
            }
            WriteNow();
            lock (_saveSync) { _saveInFlight = false; }
        }
    }

    /// <summary>Synchronous atomic write: tmp → WriteThrough+flush → File.Replace/Move.</summary>
    private void WriteNow()
    {
        AppSettings snapshot;
        lock (_gate) snapshot = ShallowCopyWithEncryptedCredentials(Current);

        var tmp = _path + ".tmp";
        var bak = _path + ".bak";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(fs, snapshot, JsonOptions);
                fs.Flush(flushToDisk: true);
            }
            const int retries = 4;
            for (var i = 0; ; i++)
            {
                try
                {
                    if (File.Exists(_path))
                        File.Replace(tmp, _path, bak);
                    else
                        File.Move(tmp, _path);
                    break;
                }
                catch (IOException) when (i < retries)
                {
                    // Agent process may hold a read handle — brief retry.
                    Thread.Sleep(50 * (i + 1));
                }
            }
        }
        catch { try { File.Delete(tmp); } catch { } }
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

    public void Dispose()
    {
        Flush();
    }
}
