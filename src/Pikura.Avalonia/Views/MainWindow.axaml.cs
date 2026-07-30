using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Core.Settings;
using Pikura.Core.Services;

namespace Pikura.Avalonia.Views;

public partial class MainWindow : Window
{
    private TrayIcon? _trayIcon;

    // Track normal window bounds before maximize (Avalonia doesn't have RestoreBounds)
    private double _normalWidth;
    private double _normalHeight;
    private int _normalX;
    private int _normalY;

    public MainWindow()
    {
        InitializeComponent();

        // Windows needs a system resize frame for Aero Snap (Win+Arrow / drag-to-edge).
        // macOS and Linux keep the current borderless custom chrome.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            WindowDecorations = WindowDecorations.BorderOnly;

        Loaded += OnLoaded;
        Closing += OnClosing;
        PropertyChanged += OnWindowPropertyChanged;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(WindowState))
        {
            UpdateCaptionIcons();

            // Before maximizing, save the current normal bounds
            if (WindowState == WindowState.Normal)
            {
                _normalWidth = Width;
                _normalHeight = Height;
                _normalX = Position.X;
                _normalY = Position.Y;
            }
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Restore window size, position, and state
        try
        {
            var settings = AppServices.Get<SettingsService>();
            var s = settings.Current;

            // Get screen size for bounds checking
            var screens = Screens;
            var primaryScreen = screens.Primary;
            var screenBounds = primaryScreen?.Bounds ?? new PixelRect(0, 0, 1920, 1080);

            // Restore size with minimums
            var targetWidth = s.WindowWidth >= 1000 ? s.WindowWidth : 1200;
            var targetHeight = s.WindowHeight >= 600 ? s.WindowHeight : 800;

            // Cap size to screen size
            targetWidth = Math.Min(targetWidth, screenBounds.Width * 0.9);
            targetHeight = Math.Min(targetHeight, screenBounds.Height * 0.9);

            Width = targetWidth;
            Height = targetHeight;

            // Restore position with bounds checking
            if (s.WindowX >= 0 && s.WindowY >= 0)
            {
                var targetX = (int)s.WindowX;
                var targetY = (int)s.WindowY;

                // Ensure window is at least partially visible on screen
                // Allow 100px margin so user can grab the edge
                var minVisible = 100;
                if (targetX + targetWidth < minVisible) targetX = minVisible; // Too far left
                if (targetX > screenBounds.Width - minVisible) targetX = screenBounds.Width - minVisible; // Too far right
                if (targetY + targetHeight < minVisible) targetY = minVisible; // Too far up
                if (targetY > screenBounds.Height - minVisible) targetY = 50; // Too far down, reset to top

                Position = new PixelPoint(targetX, targetY);
            }
            else
            {
                // Center on screen if no saved position
                var centerX = (screenBounds.Width - (int)targetWidth) / 2;
                var centerY = (screenBounds.Height - (int)targetHeight) / 2;
                Position = new PixelPoint(screenBounds.X + Math.Max(0, centerX), screenBounds.Y + Math.Max(0, centerY));
            }

            // Initialize normal bounds from saved settings (before any maximize)
            _normalWidth = targetWidth;
            _normalHeight = targetHeight;
            _normalX = Position.X;
            _normalY = Position.Y;

            // Restore window state (Maximized only - don't start minimized)
            if (s.WindowState == 2) // Maximized
            {
                WindowState = WindowState.Maximized;
            }

            // Ensure sidebar is visible on startup (not collapsed)
            if (RootGrid?.ColumnDefinitions.Count > 0)
            {
                var col = RootGrid.ColumnDefinitions[0];
                if (col.Width.Value < 220)
                {
                    col.Width = new GridLength(220);
                    if (SidebarBorder != null) SidebarBorder.IsVisible = true;
                }
            }
        }
        catch
        {
            Width  = 1200;
            Height = 800;
        }

        // Build tray icon programmatically (Avalonia 12 requires this approach)
        BuildTrayIcon();

        // Initialize services that need the window reference
        try
        {
            var filePicker = AppServices.Get<FilePickerService>();
            filePicker.Initialize(this);

            var dialogService = AppServices.Get<DialogService>();
            dialogService.Initialize(this);
        }
        catch { /* Services may not be available during design time */ }

        // Subscribe to account switches so the chip refreshes
        try
        {
            var accountService = AppServices.Get<AccountService>();
            accountService.ActiveProfileChanged += (_, _) =>
            {
                var vm = DataContext as ViewModels.MainWindowViewModel;
                Dispatcher.UIThread.Post(() =>
                    vm?.GetType()
                        .GetMethod("RefreshUserChip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        ?.Invoke(vm, null));
            };
        }
        catch { }

        // Subscribe to changelog notification from ViewModel
        if (DataContext is ViewModels.MainWindowViewModel mainVm)
        {
            mainVm.PropertyChanged += async (_, ev) =>
            {
                if (ev.PropertyName == nameof(ViewModels.MainWindowViewModel.ChangelogAvailable)
                    && mainVm.ChangelogAvailable)
                {
                    await ShowChangelogDialogAsync(mainVm);
                }
            };
        }

        // On macOS, move the hamburger to the right edge of the sidebar column
        // (traffic lights occupy the left ~75px, so Column=0 right-aligned keeps it safe)
        if (OperatingSystem.IsMacOS())
        {
            HamburgerBtn.SetValue(Grid.ColumnProperty, 0);
            HamburgerBtn.HorizontalAlignment = HorizontalAlignment.Right;
            HamburgerBtn.Margin = new Thickness(0, 4, 6, 0);
        }

        LoadGalleryView();
    }

    private async Task ShowChangelogDialogAsync(ViewModels.MainWindowViewModel mainVm)
    {
        try
        {
            var dialog = new Dialogs.ChangelogDialog(
                mainVm.ChangelogVersion,
                mainVm.ChangelogNotes,
                mainVm.ChangelogReleaseUrl);
            await dialog.ShowDialog(this);
            mainVm.DismissChangelogCommand.Execute(null);
        }
        catch { /* non-fatal */ }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            var settings = AppServices.Get<SettingsService>();
            var currentState = WindowState;

            settings.Update(s =>
            {
                // Save window state (0=Normal, 1=Minimized, 2=Maximized)
                s.WindowState = currentState switch
                {
                    WindowState.Normal => 0,
                    WindowState.Minimized => 1,
                    WindowState.Maximized => 2,
                    _ => 0
                };

                // Save size only if not minimized
                if (currentState != WindowState.Minimized)
                {
                    // If maximized, save the normal bounds (tracked before maximize)
                    if (currentState == WindowState.Maximized)
                    {
                        s.WindowWidth = _normalWidth > 0 ? _normalWidth : 1200;
                        s.WindowHeight = _normalHeight > 0 ? _normalHeight : 800;
                        s.WindowX = _normalX;
                        s.WindowY = _normalY;
                    }
                    else
                    {
                        s.WindowWidth = Width;
                        s.WindowHeight = Height;
                        s.WindowX = Position.X;
                        s.WindowY = Position.Y;
                    }
                }
            });

            var s = settings.Current;
            if (s.CloseToTray || s.KeepSchedulesRunningInBackground)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            // The app is actually exiting now. Hoshi session saves are fire-and-forget
            // during normal use (streaming chat, image seeding, etc.) — without this
            // flush, a save still in flight when the process terminates is lost, and
            // the session looks empty/stale the next time the app starts.
            //
            // IMPORTANT: this runs on the UI thread (Closing is a normal event), and
            // SaveCurrentSessionAsync's awaits (a SemaphoreSlim + File.WriteAllTextAsync)
            // don't use ConfigureAwait(false) — their continuations want to resume on the
            // UI thread's synchronization context. Blocking that same thread with
            // .GetResult() while a continuation is queued for it is a deadlock: the app
            // just hangs on close instead of exiting. Task.Run escapes the UI thread's
            // sync context so the save can actually complete before we block on it.
            try
            {
                Task.Run(() => AppServices.Get<AiViewModel>().SaveCurrentSessionAsync()).GetAwaiter().GetResult();
            }
            catch { /* best-effort */ }
        }
        catch { }
    }

    // Cached view instances — reused across navigation so attached controls never miss events
    private Pikura.Avalonia.Views.Gallery.GalleryView? _galleryView;
    private Pikura.Avalonia.Views.Rankings.EnhancedRankingsView? _rankingsView;
    private Pikura.Avalonia.Views.Discover.DiscoverView? _discoverView;
    private Pikura.Avalonia.Views.Settings.SettingsView? _settingsView;
    private Pikura.Avalonia.Views.Bookmarks.BookmarksView? _bookmarksView;
    private Pikura.Avalonia.Views.Hoshi.HoshiView? _hoshiView;
    private Pikura.Avalonia.Views.Analytics.AnalyticsView? _analyticsView;
    private Pikura.Avalonia.Views.History.HistoryView? _historyView;

    private void SetSectionTitle(string section) => Title = $"Pikura — {section}";

    public void LoadGalleryView()
    {
        try
        {
            var vm = AppServices.Get<Pikura.Avalonia.ViewModels.GalleryViewModel>();
            _galleryView ??= new Pikura.Avalonia.Views.Gallery.GalleryView { DataContext = vm };
            MainContentControl.Content = _galleryView;
            SetSectionTitle("Gallery");
        }
        catch (Exception ex)
        {
            var msg = ex.ToString();
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Pikura", "gallery_crash.txt");
            System.IO.File.WriteAllText(logPath, msg);
            MainContentControl.Content = new TextBlock { Text = "Gallery — sign in first", FontSize = 18, Foreground = Brush.Parse("#9CA3AF") };
        }
    }

    private void GalleryButton_Click(object? sender, RoutedEventArgs e) => LoadGalleryView();

    private void HomeButton_Click(object? sender, RoutedEventArgs e) => LoadGalleryView();

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = AppServices.Get<Pikura.Avalonia.ViewModels.SettingsViewModel>();
            _settingsView ??= new Pikura.Avalonia.Views.Settings.SettingsView { DataContext = vm };
            MainContentControl.Content = _settingsView;
            SetSectionTitle("Settings");
        }
        catch
        {
            MainContentControl.Content = new TextBlock { Text = "Settings", FontSize = 18, Foreground = Brush.Parse("#9CA3AF") };
            SetSectionTitle("Settings");
        }
    }

    private void RankingsButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = AppServices.Get<Pikura.Avalonia.ViewModels.EnhancedRankingsViewModel>();
            _rankingsView ??= new Pikura.Avalonia.Views.Rankings.EnhancedRankingsView { DataContext = vm };
            MainContentControl.Content = _rankingsView;
            SetSectionTitle("Rankings");
        }
        catch
        {
            MainContentControl.Content = new TextBlock { Text = "Rankings — sign in first", FontSize = 18, Foreground = Brush.Parse("#9CA3AF"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        }
    }

    private void DiscoverButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = AppServices.Get<Pikura.Avalonia.ViewModels.DiscoverViewModel>();
            _discoverView ??= new Pikura.Avalonia.Views.Discover.DiscoverView { DataContext = vm };
            MainContentControl.Content = _discoverView;
            SetSectionTitle("Discover");
            vm.OnNavigatedTo();
        }
        catch (Exception ex)
        {
            MainContentControl.Content = new TextBlock { Text = $"Discover — error: {ex.Message}", FontSize = 18, Foreground = Brush.Parse("#9CA3AF"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        }
    }

    private void BookmarksButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = AppServices.Get<Pikura.Avalonia.ViewModels.BookmarksViewModel>();
            _bookmarksView ??= new Pikura.Avalonia.Views.Bookmarks.BookmarksView { DataContext = vm };
            MainContentControl.Content = _bookmarksView;
            SetSectionTitle("Bookmarks");
            vm.OnNavigatedTo();
        }
        catch (Exception ex)
        {
            MainContentControl.Content = new TextBlock { Text = $"Bookmarks — error: {ex.Message}", FontSize = 18, Foreground = Brush.Parse("#9CA3AF"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        }
    }

    private async void JobsButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = AppServices.Get<HistoryViewModel>();
            var isFirstLoad = _historyView == null;
            _historyView ??= new History.HistoryView { DataContext = vm };
            MainContentControl.Content = _historyView;
            SetSectionTitle("Jobs");
            // Only reload on first visit to prevent re-render flicker when switching tabs
            if (isFirstLoad)
                await vm.ReloadAsync();
        }
        catch (Exception ex)
        {
            MainContentControl.Content = new TextBlock { Text = $"Jobs — error: {ex.Message}", FontSize = 18, Foreground = Brush.Parse("#9CA3AF"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        }
    }

    private void AnalyticsButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = AppServices.Get<AnalyticsViewModel>();
            _analyticsView ??= new Pikura.Avalonia.Views.Analytics.AnalyticsView { DataContext = vm };
            MainContentControl.Content = _analyticsView;
            SetSectionTitle("Analytics");
        }
        catch (Exception ex)
        {
            MainContentControl.Content = new TextBlock { Text = $"Analytics — error: {ex.Message}", FontSize = 18, Foreground = Brush.Parse("#9CA3AF"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            SetSectionTitle("Analytics");
        }
    }

    private void BatchDownloadButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = AppServices.Get<BatchDownloadViewModel>();
            MainContentControl.Content = new BatchDownloadView { DataContext = vm };
            SetSectionTitle("Batch Download");
        }
        catch (Exception ex)
        {
            MainContentControl.Content = new TextBlock { Text = $"Batch Download — error: {ex.Message}", FontSize = 18, Foreground = Brush.Parse("#9CA3AF"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        }
    }

    private void ArtistsButton_Click(object? sender, RoutedEventArgs e)
    {
        MainContentControl.Content = new TextBlock { Text = "Artists — coming soon", FontSize = 18, Foreground = Brush.Parse("#9CA3AF"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        SetSectionTitle("Artists");
    }

    internal void HoshiButton_Click(object? sender, RoutedEventArgs e) => LoadHoshiView();

    /// <summary>
    /// Switches the main content to the full Hoshi tab. AiViewModel is a singleton (shared with
    /// the inline viewer's embedded Hoshi panel), so this just re-hosts the same chat session in
    /// the bigger view — no session transfer needed.
    /// </summary>
    public void LoadHoshiView()
    {
        try
        {
            var vm = AppServices.Get<Pikura.Avalonia.ViewModels.AiViewModel>();
            _hoshiView ??= new Pikura.Avalonia.Views.Hoshi.HoshiView { DataContext = vm };
            MainContentControl.Content = _hoshiView;
            SetSectionTitle("Hoshi");
        }
        catch (Exception ex)
        {
            MainContentControl.Content = new TextBlock { Text = $"Hoshi — {ex.Message}", FontSize = 14, Foreground = Brush.Parse("#9CA3AF"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Margin = new global::Avalonia.Thickness(20) };
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void ResizeBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // On Windows the window has a real (invisible) native resize frame via
        // WindowDecorations.BorderOnly, which owns edge hit-testing, Aero Snap, and Snap
        // Layouts. Handling resize manually here too raced with that native frame and was
        // the cause of the right/bottom edge clipping and inexact snapping — so only do the
        // manual 8px-edge fallback on macOS/Linux, where the window is fully undecorated.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pos  = e.GetCurrentPoint(this).Position;
        var w    = Bounds.Width;
        var h    = Bounds.Height;
        const int edge = 8;
        bool left   = pos.X <= edge;
        bool right  = pos.X >= w - edge;
        bool top    = pos.Y <= edge;
        bool bottom = pos.Y >= h - edge;

        var dir = (left, right, top, bottom) switch
        {
            (true,  false, true,  false) => WindowEdge.NorthWest,
            (false, true,  true,  false) => WindowEdge.NorthEast,
            (true,  false, false, true ) => WindowEdge.SouthWest,
            (false, true,  false, true ) => WindowEdge.SouthEast,
            (true,  false, false, false) => WindowEdge.West,
            (false, true,  false, false) => WindowEdge.East,
            (false, false, true,  false) => WindowEdge.North,
            (false, false, false, true ) => WindowEdge.South,
            _ => (WindowEdge?)null
        };

        if (dir.HasValue)
        {
            e.Handled = true;
            BeginResizeDrag(dir.Value, e);
        }
    }

    private void MinimizeBtn_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeBtn_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void FullscreenBtn_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : WindowState.FullScreen;
    }

    private void InstallAndRestartBtn_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as ViewModels.MainWindowViewModel;
        if (vm is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await vm.InstallAndRestartAsync();
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    vm.UpdateStatusText = $"Install failed: {ex.Message}");
            }
        });
    }

    private void CloseBtn_Click(object? sender, RoutedEventArgs e)
        => Close();

    private void UpdateCaptionIcons()
    {
        // Cross-platform Unicode glyphs (Segoe MDL2 isn't available on Linux/macOS).
        // Maximize: U+25A1 (white square) = maximize, U+29C9 (two joined squares) = restore
        if (MaximizeBtn is { } max)
            max.Content = WindowState == WindowState.Maximized ? "\u29C9" : "\u2610";
        // Fullscreen: U+26F6 (square four corners) = enter, U+2922 (NE-SW arrow) = exit
        if (FullscreenBtn is { } fs)
            fs.Content = WindowState == WindowState.FullScreen ? "\u2922" : "\u26F6";
    }

    private void AccountChip_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var accountService = AppServices.Get<AccountService>();
            var profiles = accountService.Profiles;

            AccountList.Items.Clear();
            foreach (var profile in profiles)
            {
                var p = profile; // capture
                var btn = new Button
                {
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new Border
                            {
                                Background = Brushes.SteelBlue, CornerRadius = new CornerRadius(10),
                                Width = 20, Height = 20,
                                Child = new TextBlock
                                {
                                    Text = (p.DisplayLabel.Length > 0 ? p.DisplayLabel[0].ToString().ToUpper() : "?"),
                                    FontSize = 10, FontWeight = FontWeight.Bold,
                                    Foreground = Brushes.White,
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center,
                                }
                            },
                            new StackPanel
                            {
                                Spacing = 1,
                                Children =
                                {
                                    new TextBlock { Text = p.DisplayLabel, FontSize = 12, FontWeight = FontWeight.SemiBold },
                                    new TextBlock { Text = p.UserId != null ? $"ID: {p.UserId}" : "Not verified", FontSize = 10,
                                                    Foreground = new SolidColorBrush(Color.Parse("#888888")) }
                                }
                            }
                        }
                    },
                    Background = accountService.ActiveProfile?.Id == p.Id
                        ? new SolidColorBrush(Color.Parse("#1A5599FF"))
                        : Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(6),
                    CornerRadius = new CornerRadius(4),
                };
                btn.Click += async (_, _) =>
                {
                    AppServices.Get<AccountService>().SwitchTo(p.Id);
                    AccountChipBtn.Flyout?.Hide();
                    RefreshUserChipFromView();
                    await RefreshGalleryAsync();
                };
                AccountList.Items.Add(btn);
            }
        }
        catch { /* non-fatal */ }
    }

    // The two platform-specific helpers (DoLinuxNativeWebDialogLoginAsync + manual
    // cookie fallback) that used to live here were ~120 lines of code duplicated in
    // SettingsViewModel. They're now in Services/PixivLoginService.cs, which picks
    // the right backend per OS (WebView2 on Windows, Playwright Chromium on Linux,
    // manual cookie only if the primary backend errors).
    private async void AddAccountBtn_Click(object? sender, RoutedEventArgs e)
    {
        AccountChipBtn.Flyout?.Hide();
        try
        {
            var login = Services.AppServices.Get<Services.PixivLoginService>();
            var result = await login.LoginAsync(this, clearCookies: true);
            if (result.Success)
            {
                RefreshUserChipFromView();
                await RefreshGalleryAsync();
            }
        }
        catch { /* non-fatal */ }
    }

    private void RefreshUserChipFromView()
    {
        var vm = DataContext as ViewModels.MainWindowViewModel;
        vm?.GetType().GetMethod("RefreshUserChip",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.Invoke(vm, null);
    }

    private async Task RefreshGalleryAsync()
    {
        try
        {
            var galleryVm = AppServices.Get<ViewModels.GalleryViewModel>();
            await galleryVm.SwitchAccountAsync();
        }
        catch { /* non-fatal */ }
    }

    // ── Tray icon (programmatic) ──────────────────────────────────────────────
    private void BuildTrayIcon()
    {
        try
        {
            var openItem = new NativeMenuItem("Open Pikura");
            openItem.Click += (_, _) => ShowFromTray();

            var pauseItem = new NativeMenuItem("Pause schedules");
            pauseItem.Click += (_, _) =>
            {
                try { AppServices.Get<Pikura.Core.Services.ScheduleExecutorService>().Stop(); }
                catch { }
            };

            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += (_, _) =>
            {
                // Unsubscribing OnClosing bypasses the hide-to-tray branch so Exit always
                // really quits — but that also skips the Hoshi session flush inside it, so
                // any in-flight fire-and-forget chat save gets lost. Flush it here instead.
                try { AppServices.Get<AiViewModel>().SaveCurrentSessionAsync().GetAwaiter().GetResult(); }
                catch { /* best-effort */ }
                Closing -= OnClosing;
                Close();
            };

            var menu = new NativeMenu();
            menu.Add(openItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(pauseItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                ToolTipText = "Pikura",
                Icon        = new WindowIcon(global::Avalonia.Platform.AssetLoader.Open(new Uri("avares://Pikura/Assets/pikura-logo.png"))),
                Menu        = menu,
                IsVisible   = true,
            };
            _trayIcon.Clicked += (_, _) => ShowFromTray();

            // Register so Avalonia lifecycle knows about it
            TrayIcon.SetIcons(global::Avalonia.Application.Current!, new TrayIcons { _trayIcon });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TrayIcon init failed: {ex.Message}");
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void HamburgerBtn_Click(object? sender, RoutedEventArgs e)
    {
        var col = RootGrid.ColumnDefinitions[0];
        if (col.Width.Value > 0)
        {
            col.Width = new GridLength(0);
            SidebarBorder.IsVisible = false;
        }
        else
        {
            col.Width = new GridLength(220);
            SidebarBorder.IsVisible = true;
        }
    }
}