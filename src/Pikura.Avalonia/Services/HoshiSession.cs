using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Pikura.Avalonia.Services;

/// <summary>A persisted Hoshi conversation: image + messages.</summary>
public sealed class HoshiSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New chat";
    /// <summary>Optional reference (file path, URL, or Pixiv artwork id) describing where the image came from.</summary>
    public string? ImageSource { get; set; }
    /// <summary>Pixiv artwork ID when the session image was loaded from the inline viewer. Used for API lookups.</summary>
    public string? PixivArtworkId { get; set; }
    public List<PersistedMessage> Messages { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Image bytes are NOT persisted in the JSON — they live in a sidecar .img file.</summary>
    [JsonIgnore]
    public byte[]? ImageBytes { get; set; }

    /// <summary>True if this session has an image attached.</summary>
    [JsonIgnore]
    public bool HasImage => ImageBytes is { Length: > 0 };

    /// <summary>True if this session is selected for bulk operations (not persisted).</summary>
    [JsonIgnore]
    public bool IsSelected { get; set; }

    /// <summary>Display string for the session list (title + relative date).</summary>
    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var local = UpdatedAt.ToLocalTime();
            var age = DateTime.Now - local;
            if (age < TimeSpan.FromMinutes(1)) return "just now";
            if (age < TimeSpan.FromHours(1))  return $"{(int)age.TotalMinutes}m ago";
            if (age.TotalDays < 1 && local.Date == DateTime.Today) return $"{(int)age.TotalHours}h ago";
            if (age < TimeSpan.FromDays(7))   return local.ToString("ddd h:mmtt").ToLower();
            return local.ToString("MMM d, yyyy");
        }
    }
}

/// <summary>JSON-serializable message snapshot (decoupled from AiChatMessage which has UI bindings).</summary>
public sealed class PersistedMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTime At { get; set; } = DateTime.UtcNow;

    /// <summary>Base64-encoded inline thumbnail (e.g. recommended artwork or artist avatar).</summary>
    public string? ImageBase64 { get; set; }

    /// <summary>Pixiv artwork ID for the "Open in viewer" quick action.</summary>
    public string? ArtworkId { get; set; }

    /// <summary>Pixiv artist/user ID for the "Go to gallery" quick action.</summary>
    public string? ArtistId { get; set; }
}
