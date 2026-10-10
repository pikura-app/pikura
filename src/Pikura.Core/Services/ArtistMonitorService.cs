using Microsoft.Extensions.Logging;
using Pikura.Core.Data;
using Pikura.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Core.Services;

/// <summary>
/// Monitors followed artists for new submissions and raises events.
/// </summary>
public sealed class ArtistMonitorService : IDisposable
{
    private readonly PixivClient _client;
    private readonly ArtistMonitorRepository _repository;
    private readonly ILogger<ArtistMonitorService> _logger;
    private readonly Timer _checkTimer;

    // Default check interval: 30 minutes
    private TimeSpan _checkInterval = TimeSpan.FromMinutes(30);
    private bool _isRunning;

    /// <summary>
    /// Event raised when new submissions are detected.
    /// </summary>
    public event EventHandler<NewSubmissionsEventArgs>? NewSubmissionsDetected;

    public ArtistMonitorService(
        PixivClient client,
        ArtistMonitorRepository repository,
        ILogger<ArtistMonitorService> logger)
    {
        _client = client;
        _repository = repository;
        _logger = logger;

        // Timer checks periodically but actual work is done async
        _checkTimer = new Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Gets or sets the interval between checks.
    /// </summary>
    public TimeSpan CheckInterval
    {
        get => _checkInterval;
        set
        {
            _checkInterval = value;
            if (_isRunning)
            {
                _checkTimer.Change(_checkInterval, _checkInterval);
            }
        }
    }

    /// <summary>
    /// Starts monitoring.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        _isRunning = true;
        _checkTimer.Change(TimeSpan.Zero, _checkInterval);
        _logger.LogInformation("Artist monitor started with interval: {Interval}", _checkInterval);
    }

    /// <summary>
    /// Stops monitoring.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _checkTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogInformation("Artist monitor stopped");
    }

    private void OnTimerTick(object? state)
    {
        if (!_isRunning) return;

        // Fire and forget - don't block timer
        _ = Task.Run(async () =>
        {
            try
            {
                await CheckForNewSubmissionsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for new submissions");
            }
        });
    }

    /// <summary>
    /// Checks all monitored artists for new submissions.
    /// </summary>
    public async Task CheckForNewSubmissionsAsync(CancellationToken ct = default)
    {
        // Get artists that need checking (haven't been checked in last interval)
        var artistsToCheck = await _repository.GetArtistsToCheckAsync(_checkInterval, ct);

        if (artistsToCheck.Count == 0)
        {
            _logger.LogDebug("No artists to check");
            return;
        }

        _logger.LogInformation("Checking {Count} artists for new submissions", artistsToCheck.Count);

        foreach (var artist in artistsToCheck)
        {
            try
            {
                await CheckArtistAsync(artist, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check artist {UserId}", artist.UserId);
            }

            // Small delay to avoid rate limiting
            await Task.Delay(1000, ct);
        }
    }

    private async Task CheckArtistAsync(MonitoredArtist artist, CancellationToken ct)
    {
        _logger.LogDebug("Checking artist {UserId} ({UserName})", artist.UserId, artist.UserName);

        // Get recent submissions from artist
        var profile = await _client.GetUserProfileAllAsync(artist.UserId, ct);
        if (profile == null)
        {
            _logger.LogWarning("Could not get profile for artist {UserId}", artist.UserId);
            return;
        }

        // Get artwork metadata
        var artworkIds = profile.AllArtworkIds().Take(10).ToList(); // Check last 10
        if (artworkIds.Count == 0)
        {
            await _repository.UpdateLastCheckedAsync(artist.UserId, null, ct);
            return;
        }

        // Get metadata to check dates
        var metadata = await _client.GetArtworksMetadataAsync(artist.UserId, artworkIds, true, ct);

        // Find new submissions (not in our known list)
        var newSubmissions = new List<NewSubmission>();
        var mostRecentId = artworkIds.FirstOrDefault();

        foreach (var (artworkId, artwork) in metadata)
        {
            // Check if we already know about this
            var isKnown = await _repository.IsKnownSubmissionAsync(artist.UserId, artworkId, ct);
            if (!isKnown)
            {
                newSubmissions.Add(new NewSubmission
                {
                    ArtworkId = artworkId,
                    Title = artwork.Title,
                    ArtistId = artist.UserId,
                    ArtistName = artist.UserName,
                    CreatedAt = artwork.CreateDate ?? DateTimeOffset.UtcNow,
                    ThumbnailUrl = artwork.ThumbnailUrl
                });

                // Record as known
                await _repository.RecordKnownSubmissionAsync(artist.UserId, artworkId, artwork.Title, ct);
            }
        }

        // Update last checked
        await _repository.UpdateLastCheckedAsync(artist.UserId, mostRecentId, ct);

        // Raise event if new submissions found
        if (newSubmissions.Count > 0)
        {
            _logger.LogInformation("Found {Count} new submissions from {ArtistName}",
                newSubmissions.Count, artist.UserName);

            NewSubmissionsDetected?.Invoke(this, new NewSubmissionsEventArgs(artist, newSubmissions));
        }
    }

    /// <summary>
    /// Adds all followed artists to monitoring.
    /// </summary>
    public async Task AddAllFollowedArtistsAsync(CancellationToken ct = default)
    {
        try
        {
            var self = await _client.ResolveSelfAsync();
            if (self == null)
            {
                _logger.LogWarning("Not logged in, cannot get followed artists");
                return;
            }

            // Get all followed artists
            var response = await _client.GetFollowedArtistsAsync(self.Value.UserId, limit: 100);

            foreach (var artist in response.Users)
            {
                await _repository.AddMonitoredArtistAsync(
                    artist.UserId.ToString(),
                    artist.UserName,
                    ct);
            }

            _logger.LogInformation("Added {Count} followed artists to monitoring", response.Users.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add followed artists to monitoring");
        }
    }

    /// <summary>
    /// Adds a specific artist to monitoring.
    /// </summary>
    public async Task AddArtistAsync(string userId, string userName, CancellationToken ct = default)
    {
        await _repository.AddMonitoredArtistAsync(userId, userName, ct);
    }

    /// <summary>
    /// Removes an artist from monitoring.
    /// </summary>
    public async Task RemoveArtistAsync(string userId, CancellationToken ct = default)
    {
        await _repository.RemoveMonitoredArtistAsync(userId, ct);
    }

    /// <summary>
    /// Gets all monitored artists.
    /// </summary>
    public async Task<List<MonitoredArtist>> GetMonitoredArtistsAsync(CancellationToken ct = default)
    {
        return await _repository.GetMonitoredArtistsAsync(ct);
    }

    public void Dispose()
    {
        _checkTimer.Dispose();
    }
}

/// <summary>
/// Event args for new submissions detection.
/// </summary>
public class NewSubmissionsEventArgs : EventArgs
{
    public MonitoredArtist Artist { get; }
    public IReadOnlyList<NewSubmission> NewSubmissions { get; }

    public NewSubmissionsEventArgs(MonitoredArtist artist, List<NewSubmission> submissions)
    {
        Artist = artist;
        NewSubmissions = submissions;
    }
}

/// <summary>
/// Represents a new submission from an artist.
/// </summary>
public class NewSubmission
{
    public string ArtworkId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string ArtistId { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string? ThumbnailUrl { get; set; }
}
