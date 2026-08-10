using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Core.Data;
using Pikura.Core.Models;

namespace Pikura.Avalonia.ViewModels;

public partial class AnalyticsViewModel : ViewModelBase
{
    private readonly DownloadJobRepository _jobRepository;

    [ObservableProperty] private int _totalJobs;
    [ObservableProperty] private int _completedJobs;
    [ObservableProperty] private int _failedJobs;
    [ObservableProperty] private int _runningJobs;
    [ObservableProperty] private double _successRate;
    [ObservableProperty] private int _totalDownloadedItems;
    [ObservableProperty] private int _totalFailedItems;
    [ObservableProperty] private string _storageUsage = "Calculating...";
    
    public Dictionary<string, int> JobsByType { get; } = new();
    public Dictionary<string, int> JobsByStatus { get; } = new();
    public List<DownloadMetric> RecentActivity { get; } = new();
    public List<TopItem> TopArtists { get; } = new();
    public List<TopItem> TopTags { get; } = new();
    public List<TopItem> MostFrequentedArtists { get; } = new();

    public AnalyticsViewModel(DownloadJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
        
        _ = LoadAnalyticsAsync();
    }

    public Task ReloadAsync() => LoadAnalyticsAsync();

    [RelayCommand]
    private async Task LoadAnalyticsAsync()
    {
        await LoadStatisticsAsync();
        await LoadJobBreakdownAsync();
        await LoadRecentActivityAsync();
        await LoadTopItemsAsync();
        CalculateStorageUsage();
    }

    private async Task LoadStatisticsAsync()
    {
        var jobs = await _jobRepository.GetJobsAsync();
        
        TotalJobs = jobs.Count;
        CompletedJobs = jobs.Count(j => j.Status == JobStatus.Completed);
        FailedJobs = jobs.Count(j => j.Status == JobStatus.Failed);
        RunningJobs = jobs.Count(j => j.Status == JobStatus.Running);
        
        SuccessRate = TotalJobs > 0 ? (CompletedJobs * 100.0 / TotalJobs) : 0;
        
        TotalDownloadedItems = jobs.Sum(j => j.CompletedItems);
        TotalFailedItems = jobs.Sum(j => j.FailedItems);
    }

    private async Task LoadJobBreakdownAsync()
    {
        var jobs = await _jobRepository.GetJobsAsync();
        
        JobsByType.Clear();
        foreach (var job in jobs)
        {
            var typeName = job.Type.ToString();
            if (JobsByType.ContainsKey(typeName))
                JobsByType[typeName]++;
            else
                JobsByType[typeName] = 1;
        }
        
        JobsByStatus.Clear();
        foreach (var job in jobs)
        {
            var statusName = job.Status.ToString();
            if (JobsByStatus.ContainsKey(statusName))
                JobsByStatus[statusName]++;
            else
                JobsByStatus[statusName] = 1;
        }
        
        OnPropertyChanged(nameof(JobsByType));
        OnPropertyChanged(nameof(JobsByStatus));
    }

    private async Task LoadRecentActivityAsync()
    {
        var jobs = await _jobRepository.GetJobsAsync();
        var recentJobs = jobs
            .OrderByDescending(j => j.CreatedAt)
            .Take(20)
            .Select(j => new DownloadMetric
            {
                Name = j.Name,
                Type = j.Type.ToString(),
                Status = j.Status.ToString(),
                Date = j.CreatedAt,
                Items = j.TotalItems
            })
            .ToList();
        
        RecentActivity.Clear();
        foreach (var job in recentJobs)
        {
            RecentActivity.Add(job);
        }
        
        OnPropertyChanged(nameof(RecentActivity));
    }

    private async Task LoadTopItemsAsync()
    {
        var jobs = await _jobRepository.GetJobsAsync();

        // Most downloaded artists: aggregate completed items from all artist-type targets across all jobs
        var artistDownloads = jobs
            .SelectMany(j => j.Targets
                .Where(t => t.Type == TargetType.Artist && !string.IsNullOrEmpty(t.Name))
                .Select(t => new { Name = ExtractArtistName(t.Name), Downloaded = t.DownloadedItems }))
            .GroupBy(t => t.Name)
            .Select(g => new TopItem { Name = g.Key, Count = g.Sum(t => t.Downloaded) })
            .Where(t => t.Count > 0)
            .OrderByDescending(t => t.Count)
            .Take(10)
            .ToList();

        // Fall back to Artist-type job names if no targets found
        if (artistDownloads.Count == 0)
        {
            artistDownloads = jobs
                .Where(j => j.Type == DownloadJobType.Artist)
                .GroupBy(j => j.Name)
                .Select(g => new TopItem { Name = g.Key, Count = g.Sum(j => j.CompletedItems) })
                .OrderByDescending(t => t.Count)
                .Take(10)
                .ToList();
        }

        TopArtists.Clear();
        for (int i = 0; i < artistDownloads.Count; i++)
        {
            artistDownloads[i].Rank = $"#{i + 1}";
            TopArtists.Add(artistDownloads[i]);
        }

        // Most frequented artists: number of times downloaded (job visits), regardless of item count
        var frequentedArtists = jobs
            .SelectMany(j => j.Targets
                .Where(t => t.Type == TargetType.Artist && !string.IsNullOrEmpty(t.Name))
                .Select(t => ExtractArtistName(t.Name)))
            .GroupBy(name => name)
            .Select(g => new TopItem { Name = g.Key, Count = g.Count() })
            .OrderByDescending(t => t.Count)
            .Take(10)
            .ToList();

        if (frequentedArtists.Count == 0)
        {
            frequentedArtists = jobs
                .Where(j => j.Type == DownloadJobType.Artist)
                .GroupBy(j => j.Name)
                .Select(g => new TopItem { Name = g.Key, Count = g.Count() })
                .OrderByDescending(t => t.Count)
                .Take(10)
                .ToList();
        }

        MostFrequentedArtists.Clear();
        for (int i = 0; i < frequentedArtists.Count; i++)
        {
            frequentedArtists[i].Rank = $"#{i + 1}";
            MostFrequentedArtists.Add(frequentedArtists[i]);
        }

        // Aggregate by tags (from search jobs)
        var tagDownloads = jobs
            .Where(j => j.Type == DownloadJobType.Search)
            .GroupBy(j => j.Name)
            .Select(g => new TopItem { Name = g.Key, Count = g.Sum(j => j.CompletedItems) })
            .OrderByDescending(t => t.Count)
            .Take(10)
            .ToList();

        TopTags.Clear();
        foreach (var tag in tagDownloads)
            TopTags.Add(tag);

        OnPropertyChanged(nameof(TopArtists));
        OnPropertyChanged(nameof(MostFrequentedArtists));
        OnPropertyChanged(nameof(TopTags));
    }

    private static string ExtractArtistName(string targetName)
    {
        // Target names are typically "ArtworkTitle by ArtistName" or just "ArtistName"
        var byIndex = targetName.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        return byIndex >= 0 ? targetName[(byIndex + 4)..].Trim() : targetName.Trim();
    }

    private void CalculateStorageUsage()
    {
        // Placeholder - would need to scan download directory
        StorageUsage = "Not implemented";
    }
}

public class DownloadMetric
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public int Items { get; set; }
}

public class TopItem
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public string Rank { get; set; } = string.Empty;
}
