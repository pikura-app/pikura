using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Pikura.Avalonia.Services;

/// <summary>A persisted Hoshi conversation: image + messages.</summary>
public sealed class HoshiSession : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New chat";
    /// <summary>Owning pixiv user id (null = legacy shared space).</summary>
    public string? UserId { get; set; }
    /// <summary>Optional reference (file path, URL, or Pixiv artwork id) describing where the image came from.</summary>
    public string? ImageSource { get; set; }
    /// <summary>Pixiv artwork ID when the session image was loaded from the inline viewer. Used for API lookups.</summary>
    public string? PixivArtworkId { get; set; }
    /// <summary>Pinned sessions sort into their own group at the top.</summary>
    public bool Pinned { get; set; }

    /// <summary>Archived chats are hidden from the normal list; persisted, never deleted.</summary>
    public bool Archived
    {
        get => _archived;
        set => SetProperty(ref _archived, value);
    }
    private bool _archived;

    /// <summary>Folder assignment (null = unfiled).</summary>
    public string? FolderId
    {
        get => _folderId;
        set => SetProperty(ref _folderId, value);
    }
    private string? _folderId;
    /// <summary>SHA-256 key of the session image in the content-addressed store.</summary>
    public string? ImageSha { get; set; }
    /// <summary>Message count from the DB (headers don't carry the messages).</summary>
    public int MessageCount { get; set; }
    public List<PersistedMessage> Messages { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Loaded lazily via LoadImageAsync when the session opens.</summary>
    [JsonIgnore]
    public byte[]? ImageBytes { get; set; }

    /// <summary>True if this session has an image attached.</summary>
    [JsonIgnore]
    public bool HasImage => ImageBytes is { Length: > 0 } || ImageSha != null;

    /// <summary>Transient search-hit snippet shown in the results list.</summary>
    [JsonIgnore]
    public string? SearchSnippet { get; set; }

    /// <summary>True if this session is selected for bulk operations (not persisted).</summary>
    [JsonIgnore]
    public bool IsSelected { get; set; }
    /// <summary>True while this is the open session — drives the sidebar accent bar.</summary>
    public bool IsCurrent { get; set; }
    /// <summary>Right-aligned relative time for the flat sidebar rows.</summary>
    [JsonIgnore]
    public string RelativeTime
    {
        get
        {
            var age = DateTime.Now - UpdatedAt.ToLocalTime();
            if (age.TotalHours < 1) return $"{Math.Max(0,(int)age.TotalMinutes)}m";
            if (age.TotalDays < 1) return $"{(int)age.TotalHours}h";
            return UpdatedAt.ToLocalTime().ToString("M/d");
        }
    }

    /// <summary>Display string for the session list (title + relative date).</summary>
    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var loc = Pikura.Avalonia.Localization.LocalizationService.Instance;
            var local = UpdatedAt.ToLocalTime();
            var age = DateTime.Now - local;
            if (age < TimeSpan.FromMinutes(1)) return loc["Hoshi_JustNow"];
            if (age < TimeSpan.FromHours(1))  return string.Format(loc["Hoshi_MinutesAgo"], (int)age.TotalMinutes);
            if (age.TotalDays < 1 && local.Date == DateTime.Today) return string.Format(loc["Hoshi_HoursAgo"], (int)age.TotalHours);
            if (age < TimeSpan.FromDays(7))   return local.ToString("ddd h:mmtt", loc.Culture).ToLower();
            return local.ToString("MMM d, yyyy", loc.Culture);
        }
    }
}

/// <summary>JSON-serializable message snapshot (decoupled from AiChatMessage which has UI bindings).</summary>
public sealed class PersistedMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTime At { get; set; } = DateTime.UtcNow;

    /// <summary>SHA-256 key of an inline thumbnail in the image store.</summary>
    public string? ImageSha { get; set; }

    /// <summary>Legacy-only: base64 inline thumbnail (kept so old JSON still deserializes).</summary>
    public string? ImageBase64 { get; set; }

    /// <summary>Pixiv artwork ID for the "Open in viewer" quick action.</summary>
    public string? ArtworkId { get; set; }

    /// <summary>Pixiv artist/user ID for the "Go to gallery" quick action.</summary>
    public string? ArtistId { get; set; }
}
