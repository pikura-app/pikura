using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Services;
using Pikura.Core.Data;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using Pikura.Core.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Avalonia.ViewModels;

/// <summary>A single message in the AI chat.</summary>
public partial class AiChatMessage : ObservableObject
{
    public string Role { get; init; } = "user";      // "user" | "assistant" | "system"
    [ObservableProperty] private string _content = string.Empty;
    public byte[]? ImageBytes { get; init; }         // Optional image to display inline
    public Bitmap? ImageSource
    {
        get
        {
            // Always create a fresh bitmap from the stored bytes. This avoids the thumbnail
            // disappearing when the chat control is detached and re-attached (Avalonia disposes
            // the previous Bitmap when the visual leaves the tree, so a cached one would stay dead).
            if (ImageBytes is { Length: > 0 })
            {
                try { using var ms = new MemoryStream(ImageBytes); return new Bitmap(ms); }
                catch { /* best-effort: leave null if bytes are invalid */ }
            }
            return null;
        }
    }

    public string? ArtworkId { get; init; }           // For "Open in viewer" quick action
    public string? ArtistId  { get; init; }           // For "Go to gallery" quick action
    public bool IsUser      => Role == "user";
    public bool IsAssistant => Role == "assistant";
    public bool IsSystem    => Role == "system";
    public bool HasImage    => ImageBytes != null && ImageBytes.Length > 0;
    public bool HasArtworkAction => !string.IsNullOrEmpty(ArtworkId);
    public bool HasArtistAction  => !string.IsNullOrEmpty(ArtistId);
    public bool HasAnyAction     => HasArtworkAction || HasArtistAction;
    public bool HasUrlAction     => !string.IsNullOrEmpty(PixivUrl);

    /// <summary>Pixiv URL for the quick-action "Open URL" button.</summary>
    public string? PixivUrl =>
        !string.IsNullOrEmpty(ArtworkId) ? $"https://www.pixiv.net/artworks/{ArtworkId}" :
        !string.IsNullOrEmpty(ArtistId)  ? $"https://www.pixiv.net/users/{ArtistId}" :
        null;

    /// <summary>Label shown on the message bubble — "You" / "Hoshi" / "System" instead of the raw role string.</summary>
    public string DisplayName => Role switch
    {
        "user" => "You",
        "assistant" => "Hoshi",
        _ => "System",
    };

    /// <summary>Right-aligns your own messages and left-aligns Hoshi's/system messages, mirroring a normal chat layout.</summary>
    public global::Avalonia.Layout.HorizontalAlignment BubbleAlignment =>
        IsUser ? global::Avalonia.Layout.HorizontalAlignment.Right : global::Avalonia.Layout.HorizontalAlignment.Left;
}

/// <summary>
/// ViewModel that backs the AI assistant panel.
/// Exposed as a singleton so both the viewer and the sidebar can bind to it.
/// </summary>
public partial class AiViewModel : ObservableObject
{
    private readonly OllamaService _ollama;
    private readonly LocalFavoritesService _favorites;
    private readonly PixivDownloadService _downloader;
    private readonly DownloadJobRepository _jobRepository;
    private readonly DownloadCoordinator _coordinator;
    private readonly HoshiSessionService _sessions;
    private readonly PixivClient _pixiv;
    private readonly PixivImageLoader _imageLoader;
    private readonly SettingsService _settings;
    private readonly ImageLookupService _imageLookup;
    private readonly AnimeTaggerService _tagger;

    [ObservableProperty] private bool _isPanelOpen;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isThinking;
    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _statusText = "Hoshi is off";
    [ObservableProperty] private bool _isModelReady;

    /// <summary>The artwork card currently in the viewer — set by the host (inline viewer flow).</summary>
    [ObservableProperty] private ArtworkCardViewModel? _currentCard;
    partial void OnCurrentCardChanged(ArtworkCardViewModel? value) => OnPropertyChanged(nameof(IsCurrentSubmissionMultiPage));

    /// <summary>True when the open artwork has more than one page — used to show the
    /// "Describe all pages" option only when there's actually more than one page to describe.</summary>
    public bool IsCurrentSubmissionMultiPage => CurrentCard is { PageCount: > 1 };
    /// <summary>
    /// The current page thumbnail bytes used for vision queries.
    /// When a standalone session is active, this mirrors the session's image bytes.
    /// </summary>
    public byte[]? CurrentImageBytes
    {
        get => _currentImageBytes;
        set
        {
            if (_currentImageBytes == value) return;
            _currentImageBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasImage));
            // Cache image into active session so it persists across restarts
            if (!_suppressSave && CurrentSession is { } s && value is { Length: > 0 })
            {
                s.ImageBytes = value;
                _ = _sessions.SaveAsync(s);
            }
        }
    }
    private byte[]? _currentImageBytes;

    /// <summary>True when an image is attached for vision queries.</summary>
    public bool HasImage => _currentImageBytes is { Length: > 0 };

    /// <summary>The active standalone session, or null when bound to the inline viewer's transient context.</summary>
    [ObservableProperty] private HoshiSession? _currentSession;

    /// <summary>All persisted sessions (sorted most-recent-first).</summary>
    public ObservableCollection<HoshiSession> Sessions => _sessions.Sessions;

    /// <summary>Sessions grouped by date for the sidebar.</summary>
    public List<SessionGroupViewModel> GroupedSessions => _sessions.GetGroupedSessions();

    public ObservableCollection<AiChatMessage> Messages { get; } = [];

    private CancellationTokenSource? _cts;       // for send
    private CancellationTokenSource? _enableCts;  // for enable
    private bool _nextSendWithImage;              // true = next SendAsync attaches image bytes
    private bool _sessionAutoNamed;               // true = session title was already auto-set
    private bool _suppressSave;                   // true during LoadSession to avoid saving []
    // Tracks the artist most recently discussed (via "who is the artist" or a recommended-artists
    // list) so follow-up questions like "what is their artist id" or "show more from them" resolve
    // without the user having to repeat the artist's name.
    private string? _lastMentionedArtistId;
    private string? _lastMentionedArtistName;

    // Tracks which artwork/artist results the Similar Art / Similar Artists buttons have
    // already shown for the current seed image, so repeated clicks surface fresh picks from
    // the recommendation pool instead of the same top-5 every time. Reset whenever the seed
    // (i.e. the artwork being viewed) changes.
    private string? _lastSimilarSeedId;
    private readonly HashSet<string> _shownSimilarWorkIds = new();
    private readonly HashSet<string> _shownSimilarArtistIds = new();

    public AiViewModel(
        OllamaService ollama,
        LocalFavoritesService favorites,
        PixivDownloadService downloader,
        DownloadJobRepository jobRepository,
        DownloadCoordinator coordinator,
        HoshiSessionService sessions,
        PixivClient pixiv,
        PixivImageLoader imageLoader,
        SettingsService settings,
        ImageLookupService imageLookup,
        AnimeTaggerService tagger)
    {
        _ollama        = ollama;
        _favorites     = favorites;
        _downloader    = downloader;
        _jobRepository = jobRepository;
        _coordinator   = coordinator;
        _sessions      = sessions;
        _pixiv         = pixiv;
        _imageLoader   = imageLoader;
        _settings      = settings;
        _imageLookup   = imageLookup;
        _tagger        = tagger;

        _ollama.StateChanged += (_, _) => Dispatcher.UIThread.Post(SyncOllamaState);
        SyncOllamaState();

        // Restore Hoshi enabled state from settings
        if (_settings.Current.HoshiEnabled)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500); // Small delay to ensure services are ready
                await _ollama.EnableAsync();
                Dispatcher.UIThread.Post(SyncOllamaState);
            });
        }

        // Auto-persist the current session whenever messages change
        Messages.CollectionChanged += (_, ce) =>
        {
            if (_suppressSave) return;
            // Subscribe to content changes on newly added messages (streaming)
            if (ce.NewItems != null)
            {
                foreach (var item in ce.NewItems)
                {
                    if (item is AiChatMessage msg)
                        msg.PropertyChanged += (_, _) => PersistCurrentSession();
                }
            }
            PersistCurrentSession();
        };
        
        // Notify when sessions collection changes to update grouped sessions
        _sessions.Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(GroupedSessions));

        // When the sessions directory is swapped (account switch), clear the
        // current session/messages so we don't keep showing the previous account's chat.
        _sessions.SessionsChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _suppressSave = true;
                try
                {
                    CurrentSession    = null;
                    Messages.Clear();
                    CurrentImageBytes = null;
                    _sessionAutoNamed = false;
                    _ollama.ClearHistory();
                }
                finally { _suppressSave = false; }
                OnPropertyChanged(nameof(GroupedSessions));
            });
        };

        // Restore the most recently active session (image + full chat history) so it
        // survives an app restart instead of only reappearing once the user re-opens
        // the same artwork. Sessions are already sorted most-recent-first.
        if (_sessions.Sessions.Count > 0)
            LoadSession(_sessions.Sessions[0]);
    }

    // ── Sessions ──────────────────────────────────────────────────────────────

    /// <summary>Creates a new empty session and switches to it.</summary>
    public HoshiSession StartNewSession(string? title = null)
    {
        var s = _sessions.CreateNew(title);
        LoadSession(s);
        return s;
    }

    /// <summary>
    /// Switches to the existing session for <paramref name="artworkId"/>, or creates a new one.
    /// Called whenever the inline viewer loads a new artwork / tab.
    /// </summary>
    public HoshiSession? SwitchToArtworkSession(ArtworkCardViewModel card)
    {
        // Always track the current card so intents (identify, info, etc.) work even when disabled
        CurrentCard = card;

        // Only create/restore session if Hoshi is enabled
        if (!IsEnabled)
            return null;

        // If the active session is still "blank" (freshly started via "+ New" — no artwork
        // attached and no messages yet), attach THIS artwork to it instead of switching to a
        // different per-artwork session. This lets the user click "+ New" then click an image
        // to start a session for that image, rather than always auto-creating one per artwork.
        if (CurrentSession is { PixivArtworkId: null or "" } pinned && Messages.Count == 0)
        {
            pinned.PixivArtworkId = card.Id;
            pinned.ImageSource = $"pixiv:{card.Id}";
            if (!_sessionAutoNamed || pinned.Title == "New chat")
            {
                pinned.Title = card.Title;
                _sessionAutoNamed = true;
            }
            _ = _sessions.SaveAsync(pinned);
            return pinned;
        }

        // Save the current session before switching
        if (CurrentSession is { } prev)
        {
            prev.Messages = Messages.Select(ToPersistedMessage).ToList();
            _ = _sessions.SaveAsync(prev);
        }

        // Find an existing session for this artwork
        var existing = _sessions.Sessions.FirstOrDefault(s => s.PixivArtworkId == card.Id);
        if (existing != null)
        {
            LoadSession(existing);
            return existing;
        }
        else
        {
            // Create a fresh session pre-loaded with the artwork ID and title
            var s = _sessions.CreateNew(card.Title);
            s.PixivArtworkId = card.Id;
            s.ImageSource = $"pixiv:{card.Id}";
            _suppressSave = true;
            try
            {
                CurrentSession = s;
                _sessionAutoNamed = true; // title already set
                Messages.Clear();
                _ollama.ClearHistory();
            }
            finally { _suppressSave = false; }
            _ = _sessions.SaveAsync(s);
            return s;
        }
    }

    /// <summary>
    /// Persists image bytes directly to <paramref name="session"/>, even if the user has
    /// since switched to a different artwork before the fetch that produced these bytes
    /// completed. Without this, a late-arriving image fetch would otherwise be dropped
    /// (session never gets an image) or — worse — get written into whatever session
    /// happens to be "current" by the time the fetch resolves, corrupting an unrelated
    /// session's image. Also updates the live <see cref="CurrentImageBytes"/> when
    /// <paramref name="session"/> is still the active one, so the viewer stays in sync.
    /// </summary>
    public void SetSessionImageBytes(HoshiSession session, byte[] bytes)
    {
        session.ImageBytes = bytes;
        _ = _sessions.SaveAsync(session);
        if (ReferenceEquals(CurrentSession, session))
        {
            _currentImageBytes = bytes;
            OnPropertyChanged(nameof(CurrentImageBytes));
            OnPropertyChanged(nameof(HasImage));
        }
    }

    /// <summary>Switches to an existing session: loads its image and messages into the view.</summary>
    public void LoadSession(HoshiSession session)
    {
        _suppressSave = true;
        try
        {
            CurrentSession = session;
            _sessionAutoNamed = session.Messages.Count > 0 || session.Title != "New chat";
            Messages.Clear();
            foreach (var m in session.Messages)
                Messages.Add(ToAiChatMessage(m));
            CurrentImageBytes = session.ImageBytes;
            // Reset Ollama conversation history so the model starts fresh for this session
            _ollama.ClearHistory();
        }
        finally { _suppressSave = false; }
    }

    private static PersistedMessage ToPersistedMessage(AiChatMessage msg)
    {
        var pm = new PersistedMessage
        {
            Role = msg.Role,
            Content = msg.Content,
            ArtworkId = msg.ArtworkId,
            ArtistId = msg.ArtistId
        };
        if (msg.ImageBytes is { Length: > 0 } bytes)
            pm.ImageBase64 = Convert.ToBase64String(bytes);
        return pm;
    }

    private static AiChatMessage ToAiChatMessage(PersistedMessage m)
    {
        try
        {
            return new AiChatMessage
            {
                Role = m.Role,
                Content = m.Content,
                ArtworkId = m.ArtworkId,
                ArtistId = m.ArtistId,
                ImageBytes = string.IsNullOrEmpty(m.ImageBase64) ? null : Convert.FromBase64String(m.ImageBase64)
            };
        }
        catch
        {
            // corrupt base64 — return message without image
            return new AiChatMessage { Role = m.Role, Content = m.Content, ArtworkId = m.ArtworkId, ArtistId = m.ArtistId };
        }
    }

    private void PersistCurrentSession()
    {
        if (CurrentSession is not { } s) return;
        s.Messages = Messages.Select(ToPersistedMessage).ToList();
        _ = _sessions.SaveAsync(s);
    }

    /// <summary>Explicitly saves the current session state.</summary>
    public async Task SaveCurrentSessionAsync()
    {
        if (CurrentSession is { } s)
        {
            s.Messages = Messages.Select(ToPersistedMessage).ToList();
            await _sessions.SaveAsync(s);
        }
    }

    /// <summary>Called when the Hoshi view is being unloaded/navigated away from.</summary>
    public async Task OnViewUnloadingAsync()
    {
        // Save the current session to prevent data loss
        await SaveCurrentSessionAsync();
    }

    /// <summary>Cancels any in-flight AI generation request.</summary>
    public void CancelPending()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Updates the current session's image and persists.</summary>
    public void SetSessionImage(byte[]? bytes, string? source = null, string? pixivArtworkId = null)
    {
        CurrentImageBytes = bytes;
        if (CurrentSession is { } s)
        {
            s.ImageBytes = bytes;
            s.ImageSource = source;
            if (pixivArtworkId != null) s.PixivArtworkId = pixivArtworkId;
            _ = _sessions.SaveAsync(s);
        }
    }

    /// <summary>Renames the current session and persists.</summary>
    public void RenameCurrentSession(string newTitle)
    {
        if (CurrentSession is not { } s) return;
        s.Title = string.IsNullOrWhiteSpace(newTitle) ? "Untitled" : newTitle.Trim();
        _ = _sessions.SaveAsync(s);
    }

    /// <summary>Deletes a session. If it was the current one, clears the view.</summary>
    public async Task DeleteSessionAsync(HoshiSession session)
    {
        var wasCurrent = CurrentSession?.Id == session.Id;
        await _sessions.DeleteAsync(session.Id);
        if (wasCurrent)
        {
            CurrentSession = null;
            Messages.Clear();
            CurrentImageBytes = null;
            _ollama.ClearHistory();
        }
    }

    /// <summary>Duplicates a session (preserves image + messages) and switches to the copy.</summary>
    public HoshiSession DuplicateSession(HoshiSession source)
    {
        var copy = _sessions.Duplicate(source);
        LoadSession(copy);
        return copy;
    }

    /// <summary>Deletes all sessions and clears the view.</summary>
    public async Task DeleteAllSessionsAsync()
    {
        var sessionIds = _sessions.Sessions.Select(s => s.Id).ToList();
        foreach (var id in sessionIds)
        {
            await _sessions.DeleteAsync(id);
        }
        CurrentSession = null;
        Messages.Clear();
        CurrentImageBytes = null;
        _ollama.ClearHistory();
    }

    // ── Toggle enable/disable ────────────────────────────────────────────────
    [RelayCommand]
    public Task ToggleEnabledAsync() => ToggleEnabledAsync(null);

    public async Task ToggleEnabledAsync(IProgress<string>? externalProgress)
    {
        if (IsEnabled)
        {
            Disable();
        }
        else
        {
            IsEnabled    = true;
            IsThinking   = true;
            StatusText   = "Starting…";

            var progress = new Progress<string>(msg =>
                Dispatcher.UIThread.Post(() => { StatusText = msg; externalProgress?.Report(msg); }));

            _enableCts?.Cancel();
            _enableCts = new CancellationTokenSource();
            await _ollama.EnableAsync(progress, _enableCts.Token);
            IsThinking = false;
            SyncOllamaState();

            if (_ollama.IsReady)
            {
                IsPanelOpen = true;
                AddSystemMessage("Hoshi 星 ready! I can describe images, suggest tags, download artwork, add to favorites, or move to a folder. What would you like to do?");
                
                // Save enabled state to settings
                _settings.Update(s => s.HoshiEnabled = true);
            }
        }
    }

    public void Disable()
    {
        _ollama.Disable();
        IsEnabled    = false;
        IsModelReady = false;
        IsPanelOpen  = false;
        
        // Save disabled state to settings
        _settings.Update(s => s.HoshiEnabled = false);
    }

    // ── Open/close panel without toggling enable ─────────────────────────────
    [RelayCommand]
    public void TogglePanel() => IsPanelOpen = IsEnabled && !IsPanelOpen;

    // ── Similar Art / Similar Artists quick actions ──────────────────────────
    // These are wired directly from the Hoshi toolbar buttons rather than routed through
    // SendAsync's free-text intent parser (TryHandleIntentAsync). That parser matches on
    // loose keyword co-occurrence (e.g. "artist" + "similar" anywhere in the message), so
    // the "Similar Art" prompt — which mentions "artists" only as an aside ("titles or
    // artists if you can") — was misclassified as a "Similar Artists" request. Calling the
    // underlying fetch directly avoids that ambiguity entirely.

    [RelayCommand]
    public async Task FindSimilarArtworksAsync()
    {
        if (IsThinking) return;
        AddUserMessage("Find artworks similar to this image");
        await RunRelatedFetchAsync(wantArtists: false);
    }

    [RelayCommand]
    public async Task FindSimilarArtistsAsync()
    {
        if (IsThinking) return;
        AddUserMessage("Find artists with a similar style to this image");
        await RunRelatedFetchAsync(wantArtists: true);
    }

    private async Task RunRelatedFetchAsync(bool wantArtists)
    {
        var seedId = CurrentCard?.Id ?? CurrentSession?.PixivArtworkId;
        if (string.IsNullOrEmpty(seedId))
        {
            AddAssistantMessage($"⚠ Open an artwork first — I need a starting point to find similar {(wantArtists ? "artists" : "artworks")}.");
            return;
        }

        // A new seed image means "start over" — clear what we've already shown so the
        // exclusion logic below doesn't bleed between unrelated artworks.
        if (seedId != _lastSimilarSeedId)
        {
            _lastSimilarSeedId = seedId;
            _shownSimilarWorkIds.Clear();
            _shownSimilarArtistIds.Clear();
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsThinking = true;
        try
        {
            AddAssistantMessage(wantArtists ? "Finding artists with similar work…" : "Finding similar artworks…");
            // Pull a larger pool than we'll show (Pixiv caps this endpoint at 180) so repeated
            // clicks have real variety to draw from instead of re-deriving the same top-5 from
            // a tiny 24-item response.
            var related = await _pixiv.GetRelatedWorksAsync(seedId, 90, ct);
            var illusts = related?.Illusts ?? [];
            if (illusts.Count == 0)
            {
                AddAssistantMessage("⚠ No related results found.");
                return;
            }

            if (wantArtists)
            {
                var candidates = illusts
                    .Where(i => !string.IsNullOrEmpty(i.UserId) && i.UserId != CurrentCard?.UserId)
                    .GroupBy(i => i.UserId)
                    .Select(g => g.First())
                    .ToList();

                if (candidates.Count == 0)
                {
                    AddAssistantMessage("⚠ No other artists found among related works.");
                    return;
                }

                var artists = PickUnseenAndShuffle(candidates, i => i.UserId!, _shownSimilarArtistIds, 5);
                await PostArtistsAsync(artists, ct);
            }
            else
            {
                var candidates = illusts.Where(i => !string.IsNullOrEmpty(i.Id)).ToList();
                var picked = PickUnseenAndShuffle(candidates, i => i.Id!, _shownSimilarWorkIds, 5);
                var works = picked.Select(i => i.ToArtworkPreview()).ToList();
                await PostWorksAsync(works);
            }
        }
        catch (OperationCanceledException) { /* cancelled by a newer request */ }
        catch (Exception ex)
        {
            AddSystemMessage($"✗ Failed to fetch related results: {ex.Message}");
        }
        finally
        {
            IsThinking = false;
        }
    }

    /// <summary>
    /// Randomly picks up to <paramref name="count"/> items from <paramref name="candidates"/>,
    /// preferring ones not already recorded in <paramref name="seen"/> so repeated calls (e.g.
    /// mashing the "Similar Art"/"Similar Artists" button) surface new results instead of the
    /// same top-N every time. Once every candidate has been shown at least once, <paramref
    /// name="seen"/> is reset so the pool becomes fully eligible again rather than going stale.
    /// </summary>
    private static List<T> PickUnseenAndShuffle<T>(
        List<T> candidates, Func<T, string> idSelector, HashSet<string> seen, int count)
    {
        var unseen = candidates.Where(c => !seen.Contains(idSelector(c))).ToList();
        if (unseen.Count == 0)
        {
            seen.Clear();
            unseen = candidates;
        }

        var picked = unseen.OrderBy(_ => Random.Shared.Next()).Take(count).ToList();

        // If there weren't enough fresh items to fill the batch, top up with already-seen ones
        // (still shuffled) rather than showing fewer results than requested.
        if (picked.Count < count)
        {
            var pickedIds = picked.Select(idSelector).ToHashSet();
            var extra = candidates
                .Where(c => !pickedIds.Contains(idSelector(c)))
                .OrderBy(_ => Random.Shared.Next())
                .Take(count - picked.Count);
            picked.AddRange(extra);
        }

        foreach (var p in picked) seen.Add(idSelector(p));
        return picked;
    }

    // ── Send message ─────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanSend))]
    public async Task SendAsync()
    {
        if (IsThinking) return; // ignore re-entrant calls (e.g. double-clicking a quick-action button)
        var text = InputText.Trim();
        if (string.IsNullOrEmpty(text)) return;

        InputText = string.Empty;
        AddUserMessage(text);

        // Auto-create a session on first message if none exists (e.g. inline viewer)
        if (CurrentSession == null)
        {
            var s = _sessions.CreateNew();
            if (CurrentCard != null)
            {
                s.PixivArtworkId = CurrentCard.Id;
                if (CurrentImageBytes is { Length: > 0 } img)
                {
                    s.ImageBytes = img;
                    s.ImageSource = $"pixiv:{CurrentCard.Id}";
                }
            }
            CurrentSession = s;
            _sessionAutoNamed = false;
        }

        // Auto-name the session from the first user message
        if (CurrentSession is { } sess && !_sessionAutoNamed)
        {
            _sessionAutoNamed = true;
            var autoTitle = CurrentCard != null
                ? CurrentCard.Title
                : (text.Length > 40 ? text[..40].TrimEnd() + "…" : text);
            if (!string.IsNullOrWhiteSpace(autoTitle) && sess.Title == "New chat")
                RenameCurrentSession(autoTitle);
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsThinking = true;

        try
        {
            // Check for quick-action intents before sending to model
            if (await TryHandleIntentAsync(text, ct))
            {
                IsThinking = false;
                return;
            }

            // Stream response — add bubble only after first token arrives
            AiChatMessage? assistantMsg = null;

            // Attach image when explicitly requested OR when an image is attached and
            // the message sounds like it's about the image (avoids sending it for every chat msg).
            var hasImage = CurrentImageBytes?.Length > 0;
            var lowerText = text.ToLowerInvariant();
            var looksImageRelated = hasImage && (
                lowerText.Contains("image") || lowerText.Contains("picture") ||
                lowerText.Contains("photo") || lowerText.Contains("artwork") ||
                lowerText.Contains("artist") || lowerText.Contains("art") ||
                lowerText.Contains("draw") || lowerText.Contains("paint") ||
                lowerText.Contains("character") || lowerText.Contains("style") ||
                lowerText.Contains("tag") || lowerText.Contains("this") ||
                lowerText.Contains("her") || lowerText.Contains("him") ||
                lowerText.Contains("they") || lowerText.Contains("it") ||
                lowerText.Contains("show") || lowerText.Contains("look") ||
                lowerText.Contains("see") || lowerText.Contains("what") ||
                lowerText.Contains("who") || lowerText.Contains("how") ||
                lowerText.Contains("color") || lowerText.Contains("colour") ||
                lowerText.Contains("nsfw") || lowerText.Contains("r-18") ||
                lowerText.Contains("background") || lowerText.Contains("scene") ||
                lowerText.Contains("text") || lowerText.Contains("japanese") ||
                Messages.Count <= 4  // always include for early messages in a session
            );
            var useImage = (_nextSendWithImage || looksImageRelated) && hasImage;
            _nextSendWithImage = false;
            var stream = useImage
                ? _ollama.ChatWithImageAsync(text, CurrentImageBytes!, ct)
                : _ollama.ChatAsync(text, ct);

            // Coalesce rapid token arrivals instead of round-tripping to the UI thread (and
            // re-triggering LinkTextBlock's markdown/link rebuild) on every single token — a
            // fast local model can emit dozens of tokens/sec, and dispatching each one
            // individually was a real source of UI stutter during long streamed responses
            // (e.g. Describe), which read as the app "almost crashing". Flushing at most every
            // ~40ms keeps the response feeling live while cutting UI work by an order of
            // magnitude for fast streams.
            var pending = new System.Text.StringBuilder();
            var lastFlush = System.Diagnostics.Stopwatch.StartNew();
            const int flushIntervalMs = 40;

            async Task FlushAsync(bool force = false)
            {
                if (pending.Length == 0) return;
                if (!force && lastFlush.ElapsedMilliseconds < flushIntervalMs) return;

                var toAppend = pending.ToString();
                pending.Clear();
                lastFlush.Restart();

                if (assistantMsg == null)
                {
                    var msg = new AiChatMessage { Role = "assistant", Content = toAppend };
                    assistantMsg = msg;
                    if (Dispatcher.UIThread.CheckAccess())
                        Messages.Add(msg);
                    else
                        await Dispatcher.UIThread.InvokeAsync(() => Messages.Add(msg));
                }
                else
                {
                    var captured = assistantMsg;
                    if (Dispatcher.UIThread.CheckAccess())
                        captured.Content += toAppend;
                    else
                        await Dispatcher.UIThread.InvokeAsync(() => captured.Content += toAppend);
                }
            }

            await foreach (var chunk in stream.WithCancellation(ct))
            {
                if (ct.IsCancellationRequested) break;
                pending.Append(chunk);
                await FlushAsync();
            }
            await FlushAsync(force: true);

            // If model returned nothing, show a fallback
            if (assistantMsg == null && !ct.IsCancellationRequested)
                AddAssistantMessage("…");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AddSystemMessage($"Error: {ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsThinking = false);
            // Save session after response completes
            await SaveCurrentSessionAsync();
        }
    }

    private bool CanSend() => IsModelReady && !IsThinking && !string.IsNullOrWhiteSpace(InputText);

    /// <summary>Marks the next SendAsync to include the current image bytes (vision query).</summary>
    public void RequestImageSend() => _nextSendWithImage = true;

    // Helper methods for image fetching intents
    private async Task<ArtworkCardViewModel?> FetchArtworkByIdAsync(string id, CancellationToken ct = default)
    {
        if (!ulong.TryParse(id, out var artworkId))
            return null;

        // Use the single-artwork endpoint (/ajax/illust/{id}) — it doesn't require a userId,
        // unlike GetArtworksMetadataAsync which needs one in the URL path and 404s without it.
        var b = await _pixiv.GetArtworkDetailAsync(artworkId.ToString(), ct);
        if (b == null) return null;

        var preview = new ArtworkPreview
        {
            Id = b.IllustId ?? artworkId.ToString(),
            Title = b.IllustTitle ?? artworkId.ToString(),
            UserName = b.UserName ?? string.Empty,
            UserId = b.UserId ?? string.Empty,
            ThumbnailUrl = b.ThumbnailUrl,
            PageCount = b.PageCount > 0 ? b.PageCount : 1,
            IllustType = b.IllustType,
            XRestrict = b.XRestrict,
            AiType = b.AiType,
            Width = b.Width,
            Height = b.Height,
            BookmarkCount = b.BookmarkCount,
            LikeCount = b.LikeCount,
            ViewCount = b.ViewCount,
            Tags = b.Tags?.Tags?.Select(t => t.Tag ?? string.Empty).ToList() ?? []
        };
        return new ArtworkCardViewModel(preview);
    }

    /// <summary>
    /// Resolves the artwork that best represents the current chat context.
    /// Prefers the currently viewed card, then the most recent assistant message
    /// that references an artwork, then the session's stored Pixiv artwork ID.
    /// </summary>
    internal async Task<ArtworkCardViewModel?> ResolveContextArtworkAsync(CancellationToken ct = default)
    {
        if (CurrentCard is { } card)
            return card;

        var lastArtworkMsg = Messages.LastOrDefault(m => m.IsAssistant && !string.IsNullOrEmpty(m.ArtworkId));
        if (lastArtworkMsg != null)
        {
            try { return await FetchArtworkByIdAsync(lastArtworkMsg.ArtworkId!, ct); }
            catch { /* fall through */ }
        }

        if (CurrentSession?.PixivArtworkId is { Length: > 0 } sessionId)
        {
            try { return await FetchArtworkByIdAsync(sessionId, ct); }
            catch { /* fall through */ }
        }

        return null;
    }

    private async Task<ArtworkCardViewModel?> FetchRandomByArtistAsync(string artistIdentifier, bool useRecent = false)
    {
        // Try to parse as artist ID first, then search by name
        string? artistId = null;
        if (ulong.TryParse(artistIdentifier, out var parsedId))
        {
            artistId = parsedId.ToString();
        }
        else
        {
            // Search for user by name
            var users = await _pixiv.SearchArtistsAsync(artistIdentifier);
            var first = users?.FirstOrDefault();
            if (first != null)
                artistId = first.UserId;
        }

        if (string.IsNullOrEmpty(artistId))
            return null;

        // Get user's artworks
        var artworksResponse = await _pixiv.GetUserIllustsAsync(artistId, 0, 48, CancellationToken.None);
        var artworks = artworksResponse?.Illusts ?? new List<ArtworkPreview>();
        if (!artworks.Any())
            return null;

        // Pick random or most recent
        var selected = useRecent ? artworks.First() : artworks[new Random().Next(artworks.Count)];
        var card = new ArtworkCardViewModel(selected);
        return card;
    }

    private async Task<ArtworkCardViewModel?> FetchRandomArtworkAsync()
    {
        // Use discovery artworks and pick random
        var discovery = await _pixiv.GetDiscoveryArtworksAsync();
        if (discovery?.Thumbnails?.Illust == null || !discovery.Thumbnails.Illust.Any())
            return null;

        var selected = discovery.Thumbnails.Illust[new Random().Next(discovery.Thumbnails.Illust.Count)];
        
        // Get the artwork ID and fetch full metadata
        if (ulong.TryParse(selected.Id, out var artworkId))
        {
            return await FetchArtworkByIdAsync(artworkId.ToString());
        }
        
        return null;
    }

    // ── Quick action buttons ─────────────────────────────────────────────────
    [RelayCommand]
    public async Task DescribeImageAsync()
    {
        if (!await EnsureImageBytesAsync()) return;
        _nextSendWithImage = true;
        InputText = "Describe this image in detail. Include the art style, subject, mood, and any notable elements.";
        await SendAsync();
    }

    [RelayCommand]
    public async Task SuggestTagsAsync()
    {
        if (!await EnsureImageBytesAsync()) return;

        // Prefer the local ONNX tagger if enabled and installed; it produces much better
        // Danbooru-style tags than a general-purpose LLM.
        if (_settings.Current.HoshiUseAnimeTagger)
        {
            var model = KnownAnimeTaggerModels.GetByKey(_settings.Current.HoshiAnimeTaggerModel)
                ?? KnownAnimeTaggerModels.WdSwinV2TaggerV3;

            if (AnimeTaggerService.IsModelInstalled(model))
            {
                await GenerateTagsWithTaggerAsync(model);
                return;
            }

            StatusText = "Anime tagger model is not installed. Install it in Settings → Hoshi AI.";
            // Fall through to the LLM fallback so the user still gets some result.
        }

        _nextSendWithImage = true;
        InputText = "Suggest Pixiv-style tags for this image. Include both Japanese and English tags (Pixiv tags are usually Japanese). Example format: アニメ (anime), 女の子 (girl), ポートレート (portrait), 金髪 (blonde hair), 夕日 (sunset).";
        await SendAsync();
    }

    private async Task GenerateTagsWithTaggerAsync(TaggerModelInfo model)
    {
        IsThinking = true;
        StatusText = "Running anime tagger…";
        try
        {
            var result = await _tagger.TagImageAsync(
                CurrentImageBytes!,
                model,
                _settings.Current.HoshiAnimeTaggerThreshold,
                _settings.Current.HoshiAnimeTaggerMaxTags);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("**Danbooru-style tags**");
            sb.AppendLine();
            if (result.Character.Count > 0)
                sb.AppendLine($"Character: {string.Join(", ", result.Character.Select(t => t.Name))}");
            if (result.Copyright.Count > 0)
                sb.AppendLine($"Copyright: {string.Join(", ", result.Copyright.Select(t => t.Name))}");
            if (result.Artist.Count > 0)
                sb.AppendLine($"Artist: {string.Join(", ", result.Artist.Select(t => t.Name))}");
            if (result.General.Count > 0)
                sb.AppendLine($"General: {string.Join(", ", result.General.Select(t => t.Name))}");
            if (result.Meta.Count > 0)
                sb.AppendLine($"Meta: {string.Join(", ", result.Meta.Select(t => t.Name))}");

            var tagText = sb.ToString().Trim();

            Messages.Add(new AiChatMessage
            {
                Role = "user",
                Content = "Suggest tags for this image.",
                ImageBytes = CurrentImageBytes
            });
            Messages.Add(new AiChatMessage
            {
                Role = "assistant",
                Content = tagText
            });
            PersistCurrentSession();
        }
        catch (Exception ex)
        {
            // Surface the failure in the chat itself — StatusText alone isn't enough since the
            // finally block below resets it right after, making tagger errors invisible otherwise.
            AddSystemMessage($"✗ Anime tagger failed: {ex.Message}");
        }
        finally
        {
            IsThinking = false;
            StatusText = IsModelReady ? "Ready" : "Hoshi is off";
        }
    }

    /// <summary>
    /// Runs the vision model over every page of a multi-page submission, instead of just the
    /// currently-displayed one. Local vision models (moondream, etc. via Ollama) only accept a
    /// single image per query, so this fetches each page individually and posts one bubble per
    /// page — there's no way to hand the model all pages "at once" the way a person would flip
    /// through them, but this at least covers pages the user hasn't manually navigated to.
    /// </summary>
    [RelayCommand]
    public async Task DescribeAllPagesAsync()
    {
        if (IsThinking) return;
        var card = CurrentCard;
        if (card == null)
        {
            AddAssistantMessage("⚠ Open an artwork first — I need to know which submission's pages to describe.");
            return;
        }

        if (card.PageCount <= 1)
        {
            // Nothing to page through — same as the regular Describe action.
            await DescribeImageAsync();
            return;
        }

        AddUserMessage($"Describe all {card.PageCount} pages of this submission");

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsThinking = true;
        try
        {
            if (!_ollama.IsReady)
            {
                AddSystemMessage("⚠ Hoshi isn't ready yet — enable it in Settings first.");
                return;
            }

            var pages = await _pixiv.GetArtworkPagesAsync(card.Id, ct);
            if (pages.Count == 0)
            {
                AddAssistantMessage("⚠ Could not load this submission's pages.");
                return;
            }

            // Cap how many pages actually go through the vision model — a long manga
            // chapter would otherwise take minutes and flood the chat with results.
            const int maxPages = 10;
            var toDescribe = pages.Take(maxPages).ToList();
            AddAssistantMessage(pages.Count > maxPages
                ? $"Describing the first {maxPages} of {pages.Count} pages…"
                : $"Describing all {pages.Count} pages…");

            for (int i = 0; i < toDescribe.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = toDescribe[i];
                var url = page.Urls.Regular ?? page.Urls.Small ?? page.Urls.Original ?? page.Urls.ThumbMini;
                if (string.IsNullOrEmpty(url)) continue;

                byte[]? bytes;
                try { bytes = await _imageLoader.FetchBytesAsync(url, ct); }
                catch { bytes = null; }
                if (bytes is not { Length: > 0 })
                {
                    AddSystemMessage($"✗ Page {i + 1}: could not load image.");
                    continue;
                }

                var sb = new System.Text.StringBuilder();
                await foreach (var chunk in _ollama.ChatWithImageAsync(
                    "Describe this image in detail. Include the art style, subject, mood, and any notable elements.",
                    bytes, ct))
                {
                    sb.Append(chunk);
                }

                var msg = new AiChatMessage
                {
                    Role = "assistant",
                    Content = $"**Page {i + 1}/{pages.Count}**\n{sb}",
                    ImageBytes = bytes
                };
                await Dispatcher.UIThread.InvokeAsync(() => Messages.Add(msg));
            }
            PersistCurrentSession();
        }
        catch (OperationCanceledException) { /* cancelled by a newer request */ }
        catch (Exception ex)
        {
            AddSystemMessage($"✗ Failed to describe pages: {ex.Message}");
        }
        finally
        {
            IsThinking = false;
        }
    }

    [RelayCommand]
    public async Task AskR18Async()
    {
        if (!await EnsureImageBytesAsync()) return;
        _nextSendWithImage = true;
        InputText = "Is this image NSFW or R-18? Answer yes or no and briefly explain why.";
        await SendAsync();
    }

    /// <summary>
    /// Guarantees <see cref="CurrentImageBytes"/> is populated before a vision
    /// query is sent. The inline viewer normally seeds this from the thumbnail
    /// the moment a card is opened; this is the belt-and-suspenders fallback
    /// for: (a) the seed task is still racing the user's button click, or
    /// (b) the standalone Hoshi tab where no card is attached.
    /// Returns false if no image is available — the caller surfaces that as
    /// a friendly status message rather than letting the model hallucinate
    /// "I can't see the image" against a text-only prompt.
    /// </summary>
    private async Task<bool> EnsureImageBytesAsync()
    {
        if (CurrentImageBytes is { Length: > 0 }) return true;

        // No bytes yet but we know which card is open — pull its thumbnail
        // directly. FetchBytesAsync hits the memory cache when the card was
        // already drawn in the grid, so this is effectively instant.
        if (CurrentCard is { } card && !string.IsNullOrEmpty(card.ThumbnailUrl))
        {
            try
            {
                var bytes = string.IsNullOrEmpty(card.ThumbnailUrl) ? null : await _imageLoader.FetchBytesAsync(card.ThumbnailUrl);
                if (bytes is { Length: > 0 })
                {
                    CurrentImageBytes = bytes;
                    return true;
                }
            }
            catch { /* fall through to status message */ }
        }

        AddSystemMessage("⚠ No image attached yet — wait for the artwork to finish loading, or drag an image into the chat.");
        return false;
    }

    [RelayCommand]
    public void ClearChat()
    {
        Messages.Clear();
        _ollama.ClearHistory();
        if (_ollama.IsReady)
            AddSystemMessage("Chat cleared.");
    }

    /// <summary>
    /// Downloads an artwork using the DownloadJob pipeline so it appears in History
    /// (Active → Completed/Failed) just like downloads triggered from the artwork viewer.
    /// Reports progress as chat messages instead of failing silently.
    /// </summary>
    public async Task DownloadArtworkWithJobAsync(ArtworkCardViewModel card, CancellationToken ct = default)
    {
        AddAssistantMessage($"⏳ Downloading {card.Title}…");

        var target = new DownloadTarget
        {
            TargetId     = card.Artwork.Id,
            Name         = card.Artwork.Title,
            ThumbnailUrl = card.Artwork.ThumbnailUrl,
            UserName     = card.Artwork.UserName,
            UserId       = card.Artwork.UserId,
            Type         = TargetType.Artwork,
            Status       = TargetStatus.Running,
        };
        var job = new DownloadJob
        {
            Name      = card.Artwork.Title,
            Type      = DownloadJobType.ImageId,
            Status    = JobStatus.Running,
            StartedAt = DateTime.UtcNow,
            Targets   = [target],
        };

        await _jobRepository.SaveJobAsync(job);
        Console.Error.WriteLine($"[Hoshi] NotifyJobStarted: {job.Id} '{job.Name}'");
        _coordinator.NotifyJobStarted(job);
        await Task.Delay(50); // let HistoryViewModel's UI-thread Post run before download begins

        try
        {
            var paths = await _downloader.DownloadArtworkAsync(card.Artwork, ct: ct);
            target.Status = TargetStatus.Completed;
            target.DownloadedItems = paths.Count;
            job.Status = JobStatus.Completed;
            job.OutputFolder = paths.Count > 0 ? System.IO.Path.GetDirectoryName(paths[0]) : null;

            if (paths.Count == 0)
            {
                AddAssistantMessage($"⚠ No files were downloaded for {card.Title}.");
            }
            else
            {
                var folder = job.OutputFolder ?? "(unknown)";
                var fileWord = paths.Count == 1 ? "file" : "files";
                AddAssistantMessage($"✓ Downloaded {paths.Count} {fileWord} for {card.Title}\nSaved to: {folder}");
            }
        }
        catch (OperationCanceledException)
        {
            target.Status = TargetStatus.Cancelled;
            job.Status = JobStatus.Cancelled;
        }
        catch (Exception ex)
        {
            target.Status = TargetStatus.Failed;
            target.ErrorMessage = ex.Message;
            job.Status = JobStatus.Failed;
            AddSystemMessage($"✗ Download failed for {card.Title}: {ex.Message}");
        }
        finally
        {
            job.CompletedAt = DateTime.UtcNow;
            await _jobRepository.SaveJobAsync(job);
            Console.Error.WriteLine($"[Hoshi] NotifyJobSaved: {job.Id} status={job.Status}");
            _coordinator.NotifyJobSaved(job);
        }
    }

    // ── Intent detection: handle known commands without calling the model ────
    private async Task<bool> TryHandleIntentAsync(string input, CancellationToken ct)
    {
        var lower = input.ToLowerInvariant();

        // Download
        if (lower.Contains("download"))
        {
            var card = await ResolveContextArtworkAsync(ct);
            if (card != null)
            {
                await DownloadArtworkWithJobAsync(card, ct);
                return true;
            }

            AddAssistantMessage("⚠ No image available to download. Please fetch an image first using commands like 'show image by ID', 'random artwork', or 'random artist [name]'.");
            return true;
        }

        // Add to favorites
        if ((lower.Contains("favorite") || lower.Contains("favourite")) && lower.Contains("add") && CurrentCard != null)
        {
            if (!_favorites.IsFavorite(CurrentCard.Id))
            {
                _favorites.Add(CurrentCard.Artwork);
                AddAssistantMessage($"Added {CurrentCard.Title} to local favorites ★");
            }
            else
            {
                AddAssistantMessage($"{CurrentCard.Title} is already in your local favorites.");
            }
            return true;
        }

        // Remove from favorites
        if ((lower.Contains("favorite") || lower.Contains("favourite")) && lower.Contains("remove") && CurrentCard != null)
        {
            if (_favorites.IsFavorite(CurrentCard.Id))
            {
                _favorites.Remove(CurrentCard.Id);
                AddAssistantMessage($"Removed {CurrentCard.Title} from local favorites.");
            }
            else
            {
                AddAssistantMessage($"{CurrentCard.Title} is not in your favorites.");
            }
            return true;
        }

        // Set folder
        if (lower.Contains("folder") && lower.Contains("move") && CurrentCard != null)
        {
            var idx = lower.IndexOf(" to ", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var folder = input[(idx + 4)..].Trim().Trim('"', '\'');
                if (!string.IsNullOrEmpty(folder))
                {
                    if (!_favorites.IsFavorite(CurrentCard.Id))
                        _favorites.Add(CurrentCard.Artwork);
                    _favorites.SetFolder(CurrentCard.Id, folder);
                    AddAssistantMessage($"Moved {CurrentCard.Title} to folder \"{folder}\".");
                    return true;
                }
            }
        }

        // Show image by ID
        if (lower.Contains("show") && lower.Contains("image") && (lower.Contains("id") || lower.Contains("by id")))
        {
            var match = System.Text.RegularExpressions.Regex.Match(input, @"(\d+)");
            if (match.Success)
            {
                AddAssistantMessage($"Fetching image by ID: {match.Value}…");
                try
                {
                    var card = await FetchArtworkByIdAsync(match.Value);
                    if (card != null)
                    {
                        // Set as current card and load image bytes
                        CurrentCard = card;
                        var bytes = string.IsNullOrEmpty(card.ThumbnailUrl) ? null : await _imageLoader.FetchBytesAsync(card.ThumbnailUrl);
                        if (bytes != null)
                        {
                            if (CurrentSession != null)
                                SetSessionImage(bytes);
                            else
                                CurrentImageBytes = bytes;
                            
                            // Add message with image thumbnail
                            var msg = new AiChatMessage 
                            { 
                                Role = "assistant", 
                                Content = $"✓ Found: {card.Title} by {card.UserName}",
                                ImageBytes = bytes
                            };
                            Messages.Add(msg);
                        }
                        else
                        {
                            AddAssistantMessage($"✓ Found: {card.Title} by {card.UserName}");
                        }
                    }
                    else
                    {
                        AddAssistantMessage($"⚠ Could not find image with ID {match.Value}");
                    }
                }
                catch (Exception ex)
                {
                    AddSystemMessage($"✗ Failed to fetch image: {ex.Message}");
                }
                return true;
            }
        }

        // Show random/recent image from artist
        if ((lower.Contains("random") || lower.Contains("recent")) && lower.Contains("artist"))
        {
            var useRecent = lower.Contains("recent");
            // Extract artist identifier (ID or name)
            var match = System.Text.RegularExpressions.Regex.Match(input, @"artist\s+(.+?)(?:\s|$)");
            if (match.Success)
            {
                var artistIdentifier = match.Groups[1].Value.Trim().Trim('"', '\'');
                AddAssistantMessage($"Fetching {(useRecent ? "recent" : "random")} image from artist: {artistIdentifier}…");
                try
                {
                    var card = await FetchRandomByArtistAsync(artistIdentifier, useRecent);
                    if (card != null)
                    {
                        CurrentCard = card;
                        var bytes = string.IsNullOrEmpty(card.ThumbnailUrl) ? null : await _imageLoader.FetchBytesAsync(card.ThumbnailUrl);
                        if (bytes != null)
                        {
                            if (CurrentSession != null)
                                SetSessionImage(bytes);
                            else
                                CurrentImageBytes = bytes;
                            
                            // Add message with image thumbnail
                            var msg = new AiChatMessage 
                            { 
                                Role = "assistant", 
                                Content = $"✓ Found: {card.Title} by {card.UserName}",
                                ImageBytes = bytes
                            };
                            Messages.Add(msg);
                        }
                        else
                        {
                            AddAssistantMessage($"✓ Found: {card.Title} by {card.UserName}");
                        }
                    }
                    else
                    {
                        AddAssistantMessage($"⚠ Could not find artworks for artist: {artistIdentifier}");
                    }
                }
                catch (Exception ex)
                {
                    AddSystemMessage($"✗ Failed to fetch artist artwork: {ex.Message}");
                }
                return true;
            }
        }

        // ── Pixiv API metadata queries ────────────────────────────────────────
        // Detect questions about the current artwork that the Pixiv API can answer
        // directly — no vision model needed.
        var isArtistQ   = lower.Contains("who is the artist") || lower.Contains("who made") || lower.Contains("who drew") || lower.Contains("who created") || lower.Contains("who is the creator") || lower.Contains("who is the author") || lower.Contains("who drew this") || lower.Contains("artist name") || lower.Contains("artist?");
        var isDateQ     = (lower.Contains("when") && (lower.Contains("upload") || lower.Contains("post") || lower.Contains("publish") || lower.Contains("release") || lower.Contains("made") || lower.Contains("create"))) || lower.Contains("upload date") || lower.Contains("posted date") || lower.Contains("release date");
        var isStatsQ    = (lower.Contains("how many") && (lower.Contains("view") || lower.Contains("like") || lower.Contains("bookmark"))) || lower.Contains("view count") || lower.Contains("like count") || lower.Contains("bookmark count") || (lower.Contains("how popular"));
        var isTagQ      = (lower.Contains("what") && lower.Contains("tag")) || lower.Contains("list the tag") || lower.Contains("show the tag") || lower.Contains("what are the tag");
        // Vision models have no access to Pixiv's actual metadata — asking them "what is the
        // title" just makes them hallucinate something from the pixels (e.g. a stray number).
        // Pull it from the API like every other metadata question here instead.
        var isTitleQ    = lower.Contains("title") || lower.Contains("what is this called") || lower.Contains("what's this called") || lower.Contains("artwork name") || lower.Contains("name of this artwork") || lower.Contains("name of the artwork") || lower.Contains("what is it called");
        var isAboutQ    = lower.Contains("tell me about") || lower.StartsWith("info ") || lower == "info" || lower.StartsWith("about this") || lower.Contains("artwork info") || lower.Contains("artwork detail") || lower.Contains("pixiv info");
        var looksLikeMetaQ = isArtistQ || isDateQ || isStatsQ || isTagQ || isTitleQ || isAboutQ;

        // Resolve a card for the metadata query: use CurrentCard, or recover from session's stored
        // artwork ID — but only when the message actually looks like a metadata question. Doing this
        // unconditionally on every message (even "hello") wastes a network call, and if it ever fails
        // (deleted artwork, network hiccup) would otherwise surface as an unrelated chat error.
        ArtworkCardViewModel? metaCardResolved = CurrentCard;
        if (looksLikeMetaQ && metaCardResolved == null && CurrentSession?.PixivArtworkId is { Length: > 0 } sessionArtworkId)
        {
            // Best-effort only — swallow failures (deleted artwork, network hiccup, etc.) so an
            // unrelated Pixiv lookup error never surfaces as a chat error for a question the user
            // didn't ask. AiViewModel has no ILogger, so this is intentionally silent.
            try { metaCardResolved = await FetchArtworkByIdAsync(sessionArtworkId); }
            catch { /* ignore */ }
        }

        var isPixivMetaQ = looksLikeMetaQ && metaCardResolved != null;

        if (isPixivMetaQ && metaCardResolved is { } metaCard)
        {
            // Update CurrentCard so subsequent vision queries also have it
            if (CurrentCard == null) CurrentCard = metaCard;

            // Create the placeholder message up-front so PropertyChanged subscription fires correctly
            var placeholder = new AiChatMessage { Role = "assistant", Content = "⏳ Looking up artwork info from Pixiv…" };
            await Dispatcher.UIThread.InvokeAsync(() => Messages.Add(placeholder));

            try
            {
                var body = await _pixiv.GetArtworkDetailAsync(metaCard.Id, ct);

                if (body == null)
                {
                    placeholder.Content = $"⚠ Could not load details for this artwork (ID: {metaCard.Id}). The artwork may be private or unavailable.";
                    return true;
                }
                else
                {
                    // Fetch artist profile for extra info
                    var artistUserId = body.UserId ?? metaCard.UserId;
                    PixivUserInfo? artistInfo = null;
                    if (!string.IsNullOrEmpty(artistUserId))
                        artistInfo = await _pixiv.GetArtistAsync(artistUserId, ct);

                    // Remember this artist so follow-ups like "what is their artist id" or
                    // "show more from them" resolve without re-asking who the artist is.
                    _lastMentionedArtistId   = artistUserId;
                    _lastMentionedArtistName = body.UserName ?? metaCard.UserName;

                    // Build response from what the user actually asked
                    var sb = new System.Text.StringBuilder();

                    if (isAboutQ || (isArtistQ && isDateQ) || (isArtistQ && isStatsQ))
                    {
                        // Full summary
                        sb.AppendLine($"**\"{body.IllustTitle ?? metaCard.Title}\"**");
                        sb.AppendLine($"🎨 **Artist:** {body.UserName ?? metaCard.UserName} (ID: {artistUserId}) — pixiv.net/users/{artistUserId}");
                        if (artistInfo?.Comment is { Length: > 0 } bio)
                            sb.AppendLine($"ℹ️ Bio: {bio}");
                        if (body.CreateDate is { Length: > 0 } cd)
                        {
                            if (DateTime.TryParse(cd, out var dt))
                                sb.AppendLine($"📅 **Uploaded:** {dt:MMMM d, yyyy} ({(DateTime.UtcNow - dt).Days} days ago)");
                            else
                                sb.AppendLine($"📅 **Uploaded:** {cd}");
                        }
                        sb.AppendLine($"👁 **Views:** {body.ViewCount:N0}   ❤️ **Likes:** {body.LikeCount:N0}   🔖 **Bookmarks:** {body.BookmarkCount:N0}");
                        if (body.XRestrict == 1) sb.AppendLine("🔞 Rated R-18");
                        if (body.AiType == 2)    sb.AppendLine("🤖 AI-generated");
                        if (body.PageCount > 1)  sb.AppendLine($"📄 {body.PageCount} pages");
                        var tags = body.Tags?.Tags.Select(t => t.Tag).Where(t => !string.IsNullOrEmpty(t)).ToList();
                        if (tags?.Count > 0)
                            sb.AppendLine($"🏷 **Tags:** {string.Join(", ", tags)}");
                        sb.AppendLine($"🔗 pixiv.net/artworks/{metaCard.Id}");
                    }
                    else if (isArtistQ)
                    {
                        sb.AppendLine($"🎨 **Artist:** {body.UserName ?? metaCard.UserName}");
                        sb.AppendLine($"🆔 User ID: {artistUserId}");
                        sb.AppendLine($"🔗 pixiv.net/users/{artistUserId}");
                        if (artistInfo?.Comment is { Length: > 0 } bio2)
                            sb.AppendLine($"ℹ️ Bio: {bio2}");
                        if (artistInfo?.IsFollowed == true)
                            sb.AppendLine("✅ You follow this artist.");
                    }
                    else if (isDateQ)
                    {
                        if (body.CreateDate is { Length: > 0 } cd2)
                        {
                            if (DateTime.TryParse(cd2, out var dt2))
                                sb.AppendLine($"📅 Uploaded on **{dt2:MMMM d, yyyy}** ({(DateTime.UtcNow - dt2).Days} days ago)");
                            else
                                sb.AppendLine($"📅 Upload date: {cd2}");
                        }
                        else
                            sb.AppendLine("📅 Upload date not available.");
                    }
                    else if (isStatsQ)
                    {
                        sb.AppendLine($"📊 **Stats for \"{body.IllustTitle ?? metaCard.Title}\"**");
                        sb.AppendLine($"👁 Views: {body.ViewCount:N0}");
                        sb.AppendLine($"❤️ Likes: {body.LikeCount:N0}");
                        sb.AppendLine($"🔖 Bookmarks: {body.BookmarkCount:N0}");
                        sb.AppendLine($"💬 Comments: {body.CommentCount:N0}");
                    }
                    else if (isTagQ)
                    {
                        var tags2 = body.Tags?.Tags.Select(t => t.Tag).Where(t => !string.IsNullOrEmpty(t)).ToList();
                        if (tags2?.Count > 0)
                            sb.AppendLine($"🏷 **Tags:** {string.Join(", ", tags2)}");
                        else
                            sb.AppendLine("🏷 No tags found.");
                    }
                    else if (isTitleQ)
                    {
                        sb.AppendLine($"📌 **Title:** {body.IllustTitle ?? metaCard.Title}");
                        sb.AppendLine($"🔗 pixiv.net/artworks/{metaCard.Id}");
                    }

                    placeholder.Content = sb.ToString().TrimEnd();

                    return true;
                }
            }
            catch (OperationCanceledException) { return true; }
            catch (Exception ex)
            {
                placeholder.Content = $"⚠ API error: {ex.Message}";
                return true;
            }
        }

        // ── Related artworks / artists, and context follow-ups ──────────────────
        // These reuse whatever artist/artwork was last discussed in this chat (tracked via
        // _lastMentionedArtistId/Name) so "what is their artist id" or "show more from them"
        // work without repeating the artist's name.
        var isRecommendedArtistsQ = lower.Contains("artist") &&
            (lower.Contains("recommended") || lower.Contains("similar") || lower.Contains("other"));
        var isRecommendedWorksQ = !isRecommendedArtistsQ &&
            (lower.Contains("similar") || lower.Contains("recommended") || lower.Contains("related")) &&
            (lower.Contains("image") || lower.Contains("artwork") || lower.Contains("work") || lower.Contains("picture"));
        var isMoreByArtistQ = !isRecommendedArtistsQ && !isRecommendedWorksQ &&
            (lower.Contains("other work") || lower.Contains("more work") || lower.Contains("works by") || lower.Contains("more from"));
        var isArtistIdFollowUpQ = lower.Contains("artist id") ||
            ((lower.Contains("their") || lower.Contains("his") || lower.Contains("her")) && lower.Contains("id"));
        var isLinkFollowUpQ = !isRecommendedWorksQ && !isMoreByArtistQ &&
            (lower.Contains("link") || (lower.Contains("url") && lower.Contains("pixiv")));

        if (isRecommendedArtistsQ || isRecommendedWorksQ || isMoreByArtistQ || isArtistIdFollowUpQ || isLinkFollowUpQ)
        {
            var seedId = CurrentCard?.Id ?? CurrentSession?.PixivArtworkId;

            if (isArtistIdFollowUpQ)
            {
                var id   = _lastMentionedArtistId   ?? CurrentCard?.UserId;
                var name = _lastMentionedArtistName ?? CurrentCard?.UserName;
                AddAssistantMessage(id is { Length: > 0 }
                    ? $"🆔 **{name ?? "Artist"}'s** user ID is **{id}**.\n🔗 pixiv.net/users/{id}"
                    : "⚠ I don't have an artist in context yet — ask about an artwork's artist first, or open one in the viewer.");
                return true;
            }

            if (isLinkFollowUpQ)
            {
                AddAssistantMessage(seedId is { Length: > 0 }
                    ? $"🔗 https://www.pixiv.net/artworks/{seedId}"
                    : "⚠ No artwork loaded to link to.");
                return true;
            }

            if (isMoreByArtistQ)
            {
                var artistId = _lastMentionedArtistId ?? CurrentCard?.UserId;
                if (string.IsNullOrEmpty(artistId))
                {
                    AddAssistantMessage("⚠ I don't know which artist you mean yet — ask \"who is the artist\" first, or open an artwork.");
                    return true;
                }
                AddAssistantMessage("Fetching more works by this artist…");
                try
                {
                    var resp = await _pixiv.GetUserIllustsAsync(artistId, 0, 24, ct);
                    var works = resp?.Illusts?.Take(5).ToList() ?? [];
                    if (works.Count == 0)
                        AddAssistantMessage("⚠ No other works found for this artist.");
                    else
                        await PostWorksAsync(works);
                }
                catch (Exception ex)
                {
                    AddSystemMessage($"✗ Failed to fetch artist's works: {ex.Message}");
                }
                return true;
            }

            // Recommended works / recommended artists both derive from the related-works feed
            if (string.IsNullOrEmpty(seedId))
            {
                AddAssistantMessage("⚠ Open an artwork first — I need a starting point to find similar works or artists.");
                return true;
            }

            AddAssistantMessage(isRecommendedArtistsQ ? "Finding artists with similar work…" : "Finding similar artworks…");
            try
            {
                var related = await _pixiv.GetRelatedWorksAsync(seedId, 24, ct);
                var illusts = related?.Illusts ?? [];
                if (illusts.Count == 0)
                {
                    AddAssistantMessage("⚠ No related results found.");
                    return true;
                }

                if (isRecommendedArtistsQ)
                {
                    var artists = illusts
                        .Where(i => !string.IsNullOrEmpty(i.UserId) && i.UserId != CurrentCard?.UserId)
                        .GroupBy(i => i.UserId)
                        .Select(g => g.First())
                        .Take(5)
                        .ToList();

                    if (artists.Count == 0)
                    {
                        AddAssistantMessage("⚠ No other artists found among related works.");
                        return true;
                    }

                    await PostArtistsAsync(artists, ct);
                }
                else
                {
                    // Recommended/similar works — one bubble per result, each with its own thumbnail + link
                    var works = illusts.Take(5).Select(i => i.ToArtworkPreview()).ToList();
                    await PostWorksAsync(works);
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"✗ Failed to fetch related results: {ex.Message}");
            }
            return true;
        }

        // Show random artwork
        if (lower.Contains("random") && lower.Contains("artwork"))
        {
            AddAssistantMessage("Fetching a random artwork…");
            try
            {
                var card = await FetchRandomArtworkAsync();
                if (card != null)
                {
                    CurrentCard = card;
                    var bytes = string.IsNullOrEmpty(card.ThumbnailUrl) ? null : await _imageLoader.FetchBytesAsync(card.ThumbnailUrl);
                    if (bytes != null)
                    {
                        if (CurrentSession != null)
                            SetSessionImage(bytes);
                        else
                            CurrentImageBytes = bytes;
                        
                        // Add message with image thumbnail
                        var msg = new AiChatMessage 
                        { 
                            Role = "assistant", 
                            Content = $"✓ Found: {card.Title} by {card.UserName}",
                            ImageBytes = bytes
                        };
                        Messages.Add(msg);
                    }
                    else
                    {
                        AddAssistantMessage($"✓ Found: {card.Title} by {card.UserName}");
                    }
                }
                else
                {
                    AddAssistantMessage("⚠ Could not fetch a random artwork");
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"✗ Failed to fetch random artwork: {ex.Message}");
            }
            return true;
        }

        // ── Character / source identification ────────────────────────────────
        var isIdentifyQ = lower.Contains("who is") || lower.Contains("identify") ||
                          lower.Contains("what character") || lower.Contains("which character") ||
                          lower.Contains("character name") || lower.Contains("what series") ||
                          lower.Contains("source?") || lower.Contains("what anime") ||
                          lower.Contains("what game") || lower.Contains("what manga") ||
                          lower == "identify" || lower == "source" || lower == "characters";

        if (isIdentifyQ)
        {
            var pixivId = CurrentCard?.Id ?? CurrentSession?.PixivArtworkId;
            var hasImg  = CurrentImageBytes is { Length: > 0 };

            if (string.IsNullOrEmpty(pixivId) && !hasImg)
            {
                AddAssistantMessage("⚠ No image or artwork loaded. Open an artwork or attach an image first, then ask me to identify it.");
                return true;
            }

            var placeholder = new AiChatMessage { Role = "assistant", Content = "🔍 Looking up character and source info…" };
            await Dispatcher.UIThread.InvokeAsync(() => Messages.Add(placeholder));

            try
            {
                ImageLookupResult? result = null;

                // Prefer Pixiv ID lookup via Danbooru (fast, no image upload needed)
                if (!string.IsNullOrEmpty(pixivId))
                    result = await _imageLookup.LookupByPixivIdAsync(pixivId, ct);

                // Fall back to SauceNAO reverse image search if we have bytes
                if (result == null && hasImg)
                    result = await _imageLookup.LookupByImageBytesAsync(CurrentImageBytes!, ct: ct);

                var sb = new System.Text.StringBuilder();

                if (result == null)
                {
                    // External lookup failed — fall back to Pixiv metadata we already have
                    var card = CurrentCard;
                    if (card != null)
                    {
                        sb.AppendLine($"🔍 **Image Identification** *(via Pixiv metadata — not indexed on Danbooru/SauceNAO)*");
                        sb.AppendLine($"🎨 **Artist:** {card.UserName}");
                        sb.AppendLine($"📌 **Title:** {card.Title}");
                        if (card.Tags?.Count > 0)
                        {
                            var tagList = string.Join(", ", card.Tags.Take(20));
                            sb.AppendLine($"🏷 **Pixiv Tags:** {tagList}");
                        }
                        sb.AppendLine($"🔗 **Source:** https://www.pixiv.net/artworks/{card.Id}");
                        sb.AppendLine();
                        sb.AppendLine("ℹ️ Character names aren't available — this artwork isn't in Danbooru or SauceNAO's database. Try asking Hoshi to **describe** the image for a visual breakdown.");
                        placeholder.Content = sb.ToString().TrimEnd();
                    }
                    else
                    {
                        placeholder.Content = "⚠ Could not identify this image. It may not be indexed on Danbooru/SauceNAO yet.";
                    }
                    return true;
                }

                sb.AppendLine($"🔍 **Image Identification** *(via {result.Provider})*");

                if (!string.IsNullOrEmpty(result.CharacterTags))
                    sb.AppendLine($"👤 **Characters:** {result.CharacterTags}");
                else
                    sb.AppendLine("👤 **Characters:** Not identified");

                if (!string.IsNullOrEmpty(result.CopyrightTags))
                    sb.AppendLine($"📖 **Series/Copyright:** {result.CopyrightTags}");

                if (!string.IsNullOrEmpty(result.ArtistTags))
                    sb.AppendLine($"🎨 **Artist:** {result.ArtistTags}");

                if (!string.IsNullOrEmpty(result.GeneralTags))
                    sb.AppendLine($"🏷 **Tags:** {result.GeneralTags}");

                if (!string.IsNullOrEmpty(result.SourceUrl))
                    sb.AppendLine($"🔗 **Source:** {result.SourceUrl}");

                if (result.Similarity < 1.0)
                    sb.AppendLine($"📊 **Confidence:** {result.Similarity:F1}%");

                // Enrich with Pixiv tags if we didn't get character tags from external source
                if (string.IsNullOrEmpty(result.CharacterTags) && CurrentCard?.Tags?.Count > 0)
                {
                    var pixivTags = string.Join(", ", CurrentCard.Tags.Take(15));
                    sb.AppendLine($"🏷 **Pixiv Tags:** {pixivTags}");
                }

                placeholder.Content = sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                placeholder.Content = $"✗ Identification failed: {ex.Message}";
            }
            return true;
        }

        return false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private void SyncOllamaState()
    {
        IsEnabled    = _ollama.IsEnabled;
        StatusText   = _ollama.StatusText;
        IsModelReady = _ollama.IsReady;
        SendCommand.NotifyCanExecuteChanged();
    }

    // Add directly when already on the UI thread so the bubble appears in the
    // chat list *before* any subsequent awaits start streaming the response.
    // Dispatcher.Post was queueing the user/system message at default priority
    // and the queued add was racing — or being clobbered by — the streaming
    // assistant message that arrives a few ms later, leaving the user with a
    // chat that shows only the answer with no record of what they asked.
    private void AddUserMessage(string text)      => AddMessage("user", text);
    private void AddAssistantMessage(string text) => AddMessage("assistant", text);
    private void AddSystemMessage(string text)    => AddMessage("system", text);

    private void AddMessage(string role, string text)
    {
        var msg = new AiChatMessage { Role = role, Content = text };
        if (Dispatcher.UIThread.CheckAccess())
            Messages.Add(msg);
        else
            Dispatcher.UIThread.Post(() => Messages.Add(msg));
    }

    /// <summary>Posts one chat bubble per artist, each with a profile thumbnail and a
    /// "Gallery" quick action. Used for "recommended artists" results.</summary>
    private async Task PostArtistsAsync(IReadOnlyList<RecommendIllustEntry> artists, CancellationToken ct)
    {
        var top = artists[0];
        foreach (var a in artists)
        {
            // Fetch public profile so we can show the artist's avatar next to the result.
            byte[]? bytes = null;
            string? imageUrl = null;
            try
            {
                var info = await _pixiv.GetArtistAsync(a.UserId!, ct);
                imageUrl = info?.ImageBigUrl ?? info?.ImageUrl;
                if (!string.IsNullOrEmpty(imageUrl))
                    bytes = await _imageLoader.FetchBytesAsync(imageUrl);
            }
            catch { /* profile/thumbnail is best-effort — still show text/button if it fails */ }

            var msg = new AiChatMessage
            {
                Role = "assistant",
                Content = $"🎨 **{a.UserName}** (ID: {a.UserId})\n🔗 pixiv.net/users/{a.UserId}",
                ImageBytes = bytes,
                ArtistId = a.UserId
            };
            if (Dispatcher.UIThread.CheckAccess())
                Messages.Add(msg);
            else
                await Dispatcher.UIThread.InvokeAsync(() => Messages.Add(msg));
        }
        PersistCurrentSession();

        // Remember the top one so follow-up questions like "what is their artist id" resolve.
        _lastMentionedArtistId   = top.UserId;
        _lastMentionedArtistName = top.UserName;
    }

    /// <summary>Posts one chat bubble per artwork, each with its own thumbnail and a clickable
    /// Pixiv link — used for "recommended works", "similar images", and "more from this artist"
    /// results, so the answer looks like a real result list instead of plain text.</summary>
    private async Task PostWorksAsync(IReadOnlyList<ArtworkPreview> works)
    {
        foreach (var w in works)
        {
            byte[]? bytes = null;
            if (!string.IsNullOrEmpty(w.ThumbnailUrl))
                try { bytes = await _imageLoader.FetchBytesAsync(w.ThumbnailUrl); }
                catch { /* thumbnail is best-effort — still show the text/link if it fails */ }

            var msg = new AiChatMessage
            {
                Role = "assistant",
                Content = $"**{w.Title}** by {w.UserName}\n🔗 pixiv.net/artworks/{w.Id}",
                ImageBytes = bytes,
                ArtworkId = w.Id,
                ArtistId = w.UserId
            };
            if (Dispatcher.UIThread.CheckAccess())
                Messages.Add(msg);
            else
                await Dispatcher.UIThread.InvokeAsync(() => Messages.Add(msg));
        }
        PersistCurrentSession();
    }

    // ── Chat result quick-actions ────────────────────────────────────────────
    // Bound by the message bubble template to let users jump straight from a
    // recommended/similar work result into the inline viewer or the artist's gallery.

    // The buttons that trigger these commands are hosted inside ItemsControl item templates,
    // so binding directly to the ViewModel command is fragile. The views use code-behind
    // click handlers that first switch to the Gallery view, then call these commands.

    [RelayCommand]
    public async Task OpenArtworkInViewerAsync(string? artworkId)
    {
        if (string.IsNullOrEmpty(artworkId)) return;
        try
        {
            var galleryVm = AppServices.Get<GalleryViewModel>();
            await galleryVm.LoadArtworkByIdCommand.ExecuteAsync(artworkId);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"✗ Could not open artwork: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task GoToArtistGalleryAsync(string? artistId)
    {
        if (string.IsNullOrEmpty(artistId)) return;
        try
        {
            var galleryVm = AppServices.Get<GalleryViewModel>();
            await galleryVm.LoadArtistByIdCommand.ExecuteAsync(artistId);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"✗ Could not open artist gallery: {ex.Message}");
        }
    }
}
