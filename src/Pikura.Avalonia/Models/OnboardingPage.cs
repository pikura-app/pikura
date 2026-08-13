using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Pikura.Avalonia.Models;

/// <summary>
/// A single page shown in the feature highlights / onboarding dialog.
/// </summary>
public sealed partial class OnboardingPage : ObservableObject
{
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconKey { get; set; } = "NewIcon";

    /// <summary>Single-media convenience setter — pages with just one screenshot/GIF can keep
    /// using this instead of populating <see cref="Screenshots"/> directly.</summary>
    public string? Screenshot
    {
        get => Screenshots.Count > 0 ? Screenshots[0] : null;
        set => Screenshots = value is null ? [] : [value];
    }

    /// <summary>All media items for this page — when there's more than one, the highlights
    /// window shows a small selector so the user can pick which one to view (each still
    /// autoplays if it's a GIF). <see cref="SelectedScreenshot"/> tracks which one is shown.</summary>
    public IReadOnlyList<string> Screenshots
    {
        get => _screenshots;
        set
        {
            _screenshots = value;
            SelectedScreenshot = value.Count > 0 ? value[0] : null;
            OnPropertyChanged(nameof(HasMultipleScreenshots));
        }
    }
    private IReadOnlyList<string> _screenshots = [];

    [ObservableProperty] private string? _selectedScreenshot;

    /// <summary>True once there's more than one media item to choose between.</summary>
    public bool HasMultipleScreenshots => Screenshots.Count > 1;

    /// <summary>Selects a specific media item — used both by manual dot clicks and by the
    /// highlights window's autoplay carousel (see FeatureHighlightsWindow.axaml.cs).</summary>
    public void SelectScreenshot(string path) => SelectedScreenshot = path;

    /// <summary>Advances to the next media item, wrapping back to the first. No-op for pages
    /// with 0 or 1 screenshots.</summary>
    public void AdvanceScreenshot()
    {
        if (Screenshots.Count < 2) return;
        var index = -1;
        for (var i = 0; i < Screenshots.Count; i++)
        {
            if (Screenshots[i] != SelectedScreenshot) continue;
            index = i;
            break;
        }
        SelectedScreenshot = Screenshots[(index + 1) % Screenshots.Count];
    }

    /// <summary>True for a brand-new feature (shows a "NEW" badge next to the title), as opposed
    /// to an improvement to something that already existed in Pikura.</summary>
    public bool IsNew { get; set; }
}
