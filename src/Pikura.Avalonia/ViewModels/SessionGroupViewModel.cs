using CommunityToolkit.Mvvm.ComponentModel;
using Pikura.Avalonia.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pikura.Avalonia.ViewModels;

/// <summary>
/// Represents a group of sessions for a specific date.
/// </summary>
public partial class SessionGroupViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded = true;
    
    public DateTime Date { get; }
    public string DisplayDate { get; }
    public List<HoshiSession> Sessions { get; }
    public bool IsPinnedGroup { get; set; }
    
    public SessionGroupViewModel(DateTime date, IEnumerable<HoshiSession> sessions)
    {
        Date = date.Date;
        DisplayDate = GetDisplayDate(date);
        Sessions = sessions.OrderByDescending(s => s.UpdatedAt).ToList();
    }
    
    private static string GetDisplayDate(DateTime date)
    {
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);
        
        var loc = Pikura.Avalonia.Localization.LocalizationService.Instance;
        if (date.Date == today.Date)
            return loc["Hoshi_Today"];
        if (date.Date == yesterday.Date)
            return loc["Hoshi_Yesterday"];
        
        // If within the last week, show day name
        if (today - date.Date <= TimeSpan.FromDays(7))
            return date.ToString("dddd", loc.Culture);
        
        // Otherwise show date
        return date.ToString("MMM dd, yyyy");
    }
}
