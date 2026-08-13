using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views.Dialogs;
using System.Diagnostics;
using System.IO;

namespace Pikura.Avalonia.Views.Settings;

public partial class SettingsView : UserControl
{
    private TabControl? _tabControl;

    public SettingsView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        LayoutUpdated += OnLayoutUpdated;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _tabControl ??= this.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (_tabControl is not null)
            _tabControl.SelectionChanged += OnTabControlSelectionChanged;
        UpdateScrollViewerMaxHeights();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_tabControl is not null)
            _tabControl.SelectionChanged -= OnTabControlSelectionChanged;
    }

    private void OnTabControlSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => UpdateScrollViewerMaxHeights();

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateScrollViewerMaxHeights();
    private void OnLayoutUpdated(object? sender, System.EventArgs e) => UpdateScrollViewerMaxHeights();

    private void UpdateScrollViewerMaxHeights()
    {
        _tabControl ??= this.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (_tabControl is null) return;

        var tabControlHeight = _tabControl.Bounds.Height;
        if (tabControlHeight <= 0) return;

        // The selected-content host gives us the real area reserved for tab content.
        // Try the standard template name first, then fall back to matching the currently
        // selected content.
        double availableHeight;
        var contentHost = _tabControl.GetVisualDescendants()
            .OfType<ContentPresenter>()
            .FirstOrDefault(cp => cp.Name == "PART_SelectedContentHost");

        if (contentHost is null)
        {
            var selectedContent = _tabControl.SelectedItem switch
            {
                TabItem tabItem => tabItem.Content,
                _ => _tabControl.SelectedContent
            };
            contentHost = _tabControl.GetVisualDescendants()
                .OfType<ContentPresenter>()
                .FirstOrDefault(cp => cp.Content == selectedContent);
        }

        if (contentHost is not null && contentHost.Bounds.Height > 0)
        {
            // Use the smaller of the host's own height and the space below its top edge.
            // This works whether the template uses a star row or auto-sizes to content.
            availableHeight = tabControlHeight - contentHost.Bounds.Top;
            availableHeight = Math.Min(availableHeight, contentHost.Bounds.Height);
        }
        else
        {
            // Last resort: subtract the header panel's height from the TabControl height.
            var header = _tabControl.GetVisualDescendants()
                .OfType<TabStrip>()
                .FirstOrDefault()
                ?? _tabControl.GetVisualDescendants()
                    .FirstOrDefault(c => c.Name is "PART_TabStrip" or "PART_ItemsPresenter");
            var headerHeight = header?.Bounds.Height ?? 0;
            availableHeight = tabControlHeight - headerHeight;
        }

        if (availableHeight <= 0) return;

        // Cap each tab's ScrollViewer so it cannot grow beyond the real available area.
        // This prevents the TabControl from auto-sizing to its content and clipping the
        // bottom of the tab.
        foreach (var tabItem in _tabControl.Items.OfType<TabItem>())
        {
            if (tabItem.Content is not ScrollViewer scrollViewer) continue;

            if (Math.Abs(scrollViewer.MaxHeight - availableHeight) > 0.5)
                scrollViewer.MaxHeight = availableHeight;
        }
    }

    private void OnPixivLocaleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not string locale) return;
        if (DataContext is SettingsViewModel vm)
            vm.SetPixivLocaleCommand.Execute(locale);
    }

    private void OnAppLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not string language) return;
        if (DataContext is SettingsViewModel vm)
            vm.SetAppLanguageCommand.Execute(language);
    }

    private void OnFolderTokenSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        if (DataContext is not SettingsViewModel vm) return;

        var content = item.Content?.ToString() ?? "";
        // Extract token from format: "%token% — Description"
        var token = content.Split('—')[0].Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith('%')) return;

        vm.FolderTemplate += token;

        // Reset selection
        cb.SelectedIndex = -1;
    }

    private void OnFilenameTokenSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        if (DataContext is not SettingsViewModel vm) return;

        var content = item.Content?.ToString() ?? "";
        var token = content.Split('—')[0].Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith('%')) return;

        vm.FilenameTemplate += token;
        cb.SelectedIndex = -1;
    }

    private void OnMangaFilenameTokenSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        if (DataContext is not SettingsViewModel vm) return;

        var content = item.Content?.ToString() ?? "";
        var token = content.Split('—')[0].Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith('%')) return;

        vm.FilenameMangaFormat += token;
        cb.SelectedIndex = -1;
    }

    private void OnInfoFilenameTokenSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        if (DataContext is not SettingsViewModel vm) return;

        var content = item.Content?.ToString() ?? "";
        var token = content.Split('—')[0].Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith('%')) return;

        vm.FilenameInfoFormat += token;
        cb.SelectedIndex = -1;
    }

    private void OnR18TypeButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string type) return;
        if (DataContext is not SettingsViewModel vm) return;
        vm.R18Type = type;
    }

    private void OnR18ModeButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string mode) return;
        if (DataContext is not SettingsViewModel vm) return;
        vm.R18Mode = mode;
    }

    private void OnOverwriteModeButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string modeStr) return;
        if (DataContext is not SettingsViewModel vm) return;
        if (int.TryParse(modeStr, out var mode))
            vm.OverwriteMode = mode;
    }

    private void OnCopyAppLogPath(object? sender, RoutedEventArgs e)
        => CopyTextToClipboard(SettingsViewModel.AppLogPath);

    private void OnCopyCrashLogPath(object? sender, RoutedEventArgs e)
        => CopyTextToClipboard(SettingsViewModel.CrashLogPath);

    private void CopyTextToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        var dt = new global::Avalonia.Input.DataTransfer();
        dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(text));
        _ = clipboard.SetDataAsync(dt);
    }

    private void OnOpenLogFolder(object? sender, RoutedEventArgs e)
    {
        var folder = SettingsViewModel.AppDataFolder;
        if (Directory.Exists(folder))
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void OnGitHubPageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/pikura-app/pikura") { UseShellExecute = true });
        }
        catch { /* best-effort — no browser available */ }
    }

    /// <summary>
    /// Shows the FULL published release history (every GitHub release, newest first), not just
    /// the current version — per explicit user request that this button should "showcase the
    /// entire published history rather than just the current version". The auto-popup shown
    /// right after an update (see MainWindowViewModel.CheckChangelogAsync /
    /// MainWindow.ShowChangelogDialogAsync) intentionally still only shows the latest release.
    /// </summary>
    private async void OnViewChangelogClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var updateCheck = AppServices.Get<Pikura.Core.Services.UpdateCheckService>();
            var releases = await updateCheck.FetchAllReleasesAsync();

            if (releases.Count == 0)
            {
                // Offline / GitHub unreachable — fall back to whatever we know locally so the
                // button still does something useful instead of silently no-op'ing.
                var version = Pikura.Core.Services.UpdateCheckService.CurrentVersion;
                var local = MainWindowViewModel.GetLocalReleaseNotes(version);
                if (local != null) releases.Add(local);
            }
            if (releases.Count == 0) return;

            var dialog = new ChangelogDialog(releases, "https://github.com/pikura-app/pikura/releases");
            if (TopLevel.GetTopLevel(this) is Window owner)
                await dialog.ShowDialog(owner);
        }
        catch { /* non-fatal */ }
    }

    private void OnRemoveOverlayImageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string path) return;
        if (DataContext is not SettingsViewModel vm) return;

        var index = vm.OverlayService.ImagePaths.IndexOf(path);
        if (index >= 0)
            vm.OverlayService.RemoveImage(index);
    }

    private async void OnEditOverlayImageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string path) return;
        if (DataContext is not SettingsViewModel vm) return;

        var overlay = vm.OverlayService;
        var item = overlay.ImageItems.FirstOrDefault(i => i.Path == path);
        if (item == null) return;

        try
        {
            var bytes = await overlay.FetchImageBytesAsync(path);
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window == null) return;

            var preview = new BackgroundPreviewWindow(path, bytes, item.Entry);
            await preview.ShowDialog(window);

            if (preview.Result is { } result)
            {
                // Apply the per-image settings back to the entry
                item.Entry.Opacity = result.Opacity;
                item.Entry.Brightness = result.Brightness;
                item.Entry.Darkness = result.Darkness;
                item.Entry.PanX = result.PanX;
                item.Entry.PanY = result.PanY;
                item.Entry.Zoom = result.Zoom;

                // Persist per-image entries and reload the current overlay image
                overlay.PersistEntries();
            }
        }
        catch { /* non-fatal */ }
    }

    private void OnClearAllOverlayImagesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.OverlayService.ClearImages();
    }

    private async void OnOverlayImageTitleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not OverlayImageItem item) return;
        var userId = item.Entry.UserId;
        if (string.IsNullOrWhiteSpace(userId)) return;

        try
        {
            // Navigate to the Gallery view and load the artist
            var mainWindow = TopLevel.GetTopLevel(this) as Pikura.Avalonia.Views.MainWindow;
            mainWindow?.LoadGalleryView();

            var galleryVm = AppServices.Get<GalleryViewModel>();
            await galleryVm.LoadArtistByIdAsync(userId);

            // If we have an artwork ID, open it in a new tab
            var illustId = item.Entry.IllustId;
            if (!string.IsNullOrWhiteSpace(illustId))
                await galleryVm.OpenArtworkByIdInNewTabAsync(illustId);
        }
        catch { /* non-fatal */ }
    }
}
