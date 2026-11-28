using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

/// <summary>Novel reader tabs (one <see cref="NovelReaderViewModel"/> per open novel).</summary>
public partial class NovelReaderHostViewModel(PixivClient client, PixivImageLoader loader, SettingsService settings, bool canExpand = true) : ObservableObject
{
    public ObservableCollection<NovelReaderViewModel> Tabs { get; } = [];
    [ObservableProperty] private NovelReaderViewModel? _selectedTab;
    public event EventHandler? CloseRequested;
    public event EventHandler? ExpandRequested;
    public event EventHandler<string>? FullscreenRequested;
    public event EventHandler? PanelToggleRequested;
    public bool HasManyTabs => Tabs.Count > 1;
    public bool HasTabs => Tabs.Count > 0;
    public bool CanExpand => canExpand;
    [ObservableProperty] private bool _isExpanded;

    partial void OnSelectedTabChanged(NovelReaderViewModel? value)
    {
        foreach (var t in Tabs) t.IsActiveTab = t == value;
    }

    [RelayCommand] private void TogglePanel() => PanelToggleRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void Expand() => ExpandRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void Fullscreen() { if (SelectedTab?.Novel?.Id is { } id) FullscreenRequested?.Invoke(this, id); }

    [RelayCommand]
    private void CloseAll()
    {
        Tabs.Clear();
        SelectedTab = null;
        OnPropertyChanged(nameof(HasManyTabs));
        OnPropertyChanged(nameof(HasTabs));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Open(string novelId, bool newTab = false)
    {
        var existing = Tabs.FirstOrDefault(t => t.Novel?.Id == novelId);
        if (existing is not null) { SelectedTab = existing; return; }
        if (!newTab && SelectedTab is { } current) { _ = current.LoadAsync(novelId); return; }
        var tab = new NovelReaderViewModel(client, loader, settings) { CanExpand = canExpand };
        tab.CloseRequested += (_, _) => CloseTab(tab);
        tab.ExpandRequested += (_, _) => ExpandRequested?.Invoke(this, EventArgs.Empty);
        tab.FullscreenRequested += (_, _) => { if (tab.Novel?.Id is { } id) FullscreenRequested?.Invoke(this, id); };
        Tabs.Add(tab);
        OnPropertyChanged(nameof(HasManyTabs));
        OnPropertyChanged(nameof(HasTabs));
        SelectedTab = tab;
        _ = tab.LoadAsync(novelId);
    }

    [RelayCommand] private void Select(NovelReaderViewModel tab) => SelectedTab = tab;

    [RelayCommand]
    public void CloseTab(NovelReaderViewModel tab)
    {
        var i = Tabs.IndexOf(tab);
        if (i < 0) return;
        Tabs.RemoveAt(i);
        OnPropertyChanged(nameof(HasManyTabs));
        OnPropertyChanged(nameof(HasTabs));
        if (Tabs.Count == 0) { SelectedTab = null; CloseRequested?.Invoke(this, EventArgs.Empty); return; }
        if (SelectedTab == tab) SelectedTab = Tabs[Math.Min(i, Tabs.Count - 1)];
    }
}
