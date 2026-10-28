using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views.Hoshi;

public partial class HoshiView : UserControl
{
    private AiViewModel? VM => DataContext as AiViewModel;

    public HoshiView()
    {
        InitializeComponent();
        // Refresh session headers when the page becomes visible — startup load can
        // race the account resolve and leave the sidebar empty.
        AttachedToVisualTree += (_, _) => { if (VM != null) _ = VM.SessionsService.RefreshHeadersAsync(); };
        DataContextChanged += (_, _) => RefreshAll();
        Loaded += (_, _) =>
        {
            RefreshAll(); ScrollToBottomDeferred(); RefreshImageStrip(); RefreshSessionTitle();
            if (ChatScroll != null)
                ChatScroll.PropertyChanged += OnScrollViewerPropertyChanged;
        };
        Unloaded += async (_, _) => await OnUnloadedAsync();

        // Drag-and-drop image support
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Ctrl+V paste support (handled at the root so it works anywhere except inside the text input)
        AddHandler(KeyDownEvent, OnRootKeyDown, RoutingStrategies.Tunnel);
    }

    private void RefreshAll()
    {
        if (VM is not { } vm) return;
        vm.PropertyChanged -= OnVmPropertyChanged;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.Messages.CollectionChanged -= OnMessagesChanged;
        vm.Messages.CollectionChanged += OnMessagesChanged;
        SyncToggleButton(vm);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AiViewModel.IsEnabled) or nameof(AiViewModel.IsThinking) or nameof(AiViewModel.StatusText))
            Dispatcher.UIThread.Post(() => { if (VM is { } vm) SyncToggleButton(vm); });
        if (e.PropertyName is nameof(AiViewModel.CurrentImageBytes) or nameof(AiViewModel.HasImage))
            Dispatcher.UIThread.Post(RefreshImageStrip);
        if (e.PropertyName == nameof(AiViewModel.CurrentSession))
            Dispatcher.UIThread.Post(RefreshSessionTitle);
    }

    private void RefreshSessionTitle()
    {
        if (SessionTitleLabel == null) return;
        SessionTitleLabel.Text = VM?.CurrentSession?.Title ?? Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Hoshi_2"];
    }

    private void RefreshImageStrip()
    {
        if (ImageThumb == null || ImageLabel == null || ImageHint == null) return;
        var bytes = VM?.CurrentImageBytes;
        if (bytes is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                ImageThumb.Source = Bitmap.DecodeToWidth(ms, 400);
            }
            catch { ImageThumb.Source = null; }
            ImageLabel.Text = $"Image attached ({bytes.Length / 1024} KB)";
            ImageHint.Text  = VM?.CurrentSession?.ImageSource ?? Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_FromCurrentArtwork"];
        }
        else
        {
            ImageThumb.Source = null;
            ImageLabel.Text = Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_NoImageAttached"];
            ImageHint.Text  = Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_DropAnImageHerePasteWith"];
        }

        var artworkId = GetCurrentArtworkId();
        if (ViewArtworkBtn != null)
            ViewArtworkBtn.IsVisible = !string.IsNullOrEmpty(artworkId);

        // The Open button lets the user attach a local image file. Once a session already has an
        // image sourced from the inline viewer (a Pixiv artwork), overwriting it via Open would
        // detach the chat from that artwork's context, so only allow Open on a "blank" session —
        // one with no image attached yet, or an image that wasn't loaded from the inline viewer.
        var hasImageFromInlineViewer = bytes is { Length: > 0 } && !string.IsNullOrEmpty(artworkId);
        if (OpenImageBtn != null)
            OpenImageBtn.IsVisible = !hasImageFromInlineViewer;
    }

    /// <summary>The Pixiv artwork ID associated with the current chat context, if any.</summary>
    /// <remarks>
    /// Prefer the session's own artwork ID so switching between saved sessions doesn't keep
    /// using a stale card from the previous session/viewer.
    /// </remarks>
    private string? GetCurrentArtworkId() => VM?.CurrentSession?.PixivArtworkId ?? VM?.CurrentCard?.Id;

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Hook into per-message content updates so streaming text keeps the view scrolled
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is AiChatMessage msg)
                    msg.PropertyChanged += (_, _) => ScrollToBottomDeferred();
            }
        }
        ScrollToBottomDeferred();
    }

    private bool _autoScroll = true;

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ExtentProperty && _autoScroll)
            ChatScroll?.ScrollToEnd();
        if (e.Property == ScrollViewer.OffsetProperty && ChatScroll != null)
        {
            var offset = ChatScroll.Offset.Y;
            var atBottom = ChatScroll.Extent.Height - offset - ChatScroll.Viewport.Height < 40;
            _autoScroll = atBottom;
        }
    }

    private void ScrollToBottomDeferred()
    {
        _autoScroll = true;
        Dispatcher.UIThread.Post(() => ChatScroll?.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void SyncToggleButton(AiViewModel vm)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (StatusLabel != null) StatusLabel.Text = vm.StatusText;
            if (ToggleBtn == null) return;
            ToggleBtn.Header = vm.IsThinking ? Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Thinking"] :
                                vm.IsEnabled  ? "Disable"  : "Enable";
            ToggleBtn.IsEnabled = !vm.IsThinking;
        });
    }

    private async void OnToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        if (!vm.IsEnabled)
        {
            if (ToggleBtn != null) { ToggleBtn.Header = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Starting"]; ToggleBtn.IsEnabled = false; }
            var progress = new Progress<string>(msg =>
                Dispatcher.UIThread.Post(() => { if (StatusLabel != null) StatusLabel.Text = msg; }));
            await vm.ToggleEnabledAsync(progress);
            if (ToggleBtn != null) ToggleBtn.IsEnabled = true;
            SyncToggleButton(vm);
        }
        else
        {
            vm.Disable();
            SyncToggleButton(vm);
        }
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e) => VM?.ClearChatCommand.Execute(null);

    private async void OnSendClicked(object? sender, RoutedEventArgs e) => await SendAsync();
    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SendAsync();
    }

    private async System.Threading.Tasks.Task SendAsync()
    {
        if (VM is not { } vm || string.IsNullOrWhiteSpace(InputBox?.Text)) return;
        vm.InputText = InputBox.Text;
        InputBox.Text = string.Empty;
        await vm.SendCommand.ExecuteAsync(null);
    }

    private async void OnDescribeClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        vm.RequestImageSend();
        vm.InputText = "Describe this image in detail. Include the subject, the art style and technique, and the character(s) — appearance, outfit, expression, and mood.";
        await vm.SendCommand.ExecuteAsync(null);
    }

    private async void OnTagsClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        await vm.SuggestTagsCommand.ExecuteAsync(null);
    }

    private async void OnAllPagesClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        await vm.DescribeAllPagesCommand.ExecuteAsync(null);
    }

    private async void OnSimilarArtworksClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        await vm.FindSimilarArtworksCommand.ExecuteAsync(null);
    }

    private async void OnSimilarArtistsClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        await vm.FindSimilarArtistsCommand.ExecuteAsync(null);
    }

    private async void OnFavClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        var card = await vm.ResolveContextArtworkAsync();
        if (card == null)
        {
            vm.Messages.Add(new AiChatMessage { Role = "system", Content = "⚠ No artwork selected — open an artwork in the viewer first." });
            return;
        }
        var favs = AppServices.Get<Pikura.Core.Services.LocalFavoritesService>();
        favs.Toggle(card.Artwork);
        var isFav = favs.IsFavorite(card.Id);
        var msg = isFav
            ? $"Added {card.Title} to local favorites ★"
            : $"Removed {card.Title} from favorites.";
        vm.Messages.Add(new AiChatMessage { Role = "assistant", Content = msg });
    }

    private async void OnDlClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        var card = await vm.ResolveContextArtworkAsync();
        Console.Error.WriteLine($"[Hoshi] OnDlClicked: VM={VM != null} resolvedCard={card?.Title ?? "null"}");
        if (card == null) { Console.Error.WriteLine("[Hoshi] OnDlClicked: bailed — no artwork context"); return; }
        await vm.DownloadArtworkWithJobAsync(card);
    }

    // ── Sessions ──────────────────────────────────────────────────────────────

    private void OnNewSessionClicked(object? sender, RoutedEventArgs e)
    {
        VM?.StartNewSession();
        RefreshImageStrip();
        RefreshSessionTitle();
        InputBox?.Focus();
    }

    /// <summary>Selecting a row (click anywhere on it, keyboard) opens that chat — the row's own
    /// PointerPressed only fires on its painted parts, so selection alone left the old chat open.</summary>
    private void OnSessionsListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is HoshiSession s && VM is { } vm && !ReferenceEquals(vm.CurrentSession, s))
        {
            vm.LoadSession(s);
            RefreshImageStrip();
            RefreshSessionTitle();
            ScrollToBottomDeferred();
        }
    }

    private void OnSessionClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control c && c.Tag is HoshiSession s && VM is { } vm)
        {
            vm.LoadSession(s);
            RefreshImageStrip();
            RefreshSessionTitle();
            ScrollToBottomDeferred();
        }
    }

    private async void OnPinSession(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is HoshiSession s && DataContext is AiViewModel vm)
            await vm.SessionsService.SetPinnedAsync(s, !s.Pinned);
    }

    private async void OnRenameSession(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.Tag is not HoshiSession s || VM is not { } vm) return;
        // Switch to the session first so the rename applies to "current"
        if (vm.CurrentSession?.Id != s.Id) vm.LoadSession(s);
        var dialog = AppServices.Get<DialogService>();
        var newName = await dialog.ShowInputDialogAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_RenameSession"], Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_NewTitle"], s.Title);
        if (!string.IsNullOrWhiteSpace(newName))
        {
            vm.RenameCurrentSession(newName);
            RefreshSessionTitle();
        }
    }

    private void OnDuplicateSession(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.Tag is HoshiSession s && VM is { } vm)
        {
            vm.DuplicateSession(s);
            RefreshImageStrip();
            RefreshSessionTitle();
        }
    }

    private async void OnDeleteSession(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.Tag is not HoshiSession s || VM is not { } vm) return;
        var dialog = AppServices.Get<DialogService>();
        var ok = await dialog.ShowConfirmationAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_DeleteSession"], $"Delete \"{s.Title}\"? This cannot be undone.");
        if (ok) await vm.DeleteSessionAsync(s);
        RefreshImageStrip();
        RefreshSessionTitle();
    }

    private async void OnClearAllSessionsClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        var dialog = AppServices.Get<DialogService>();
        var ok = await dialog.ShowConfirmationAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_DeleteAllSessions"], Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_DeleteALLSessionsThisCannotBe"]);
        if (ok) await vm.DeleteAllSessionsAsync();
        RefreshImageStrip();
        RefreshSessionTitle();
    }

    private async void OnDeleteSelectedSessionsClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        var selected = vm.Sessions.Where(s => s.IsSelected).ToList();
        if (selected.Count == 0) return;
        
        var dialog = AppServices.Get<DialogService>();
        var ok = await dialog.ShowConfirmationAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_DeleteSelectedSessions"], $"Delete {selected.Count} session(s)? This cannot be undone.");
        if (ok)
        {
            foreach (var session in selected)
            {
                await vm.DeleteSessionAsync(session);
            }
        }
        RefreshImageStrip();
        RefreshSessionTitle();
    }

    private void OnSessionCheckboxClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is HoshiSession s && VM is { } vm)
        {
            // Show/hide delete selected button based on selection count
            var hasSelection = vm.Sessions.Any(sess => sess.IsSelected);
            Dispatcher.UIThread.Post(() => 
            {
                if (DeleteSelectedBtn != null) DeleteSelectedBtn.IsVisible = hasSelection;
            });
            e.Handled = true;
        }
    }

    // ── Image input ───────────────────────────────────────────────────────────

    /// <summary>Pops up a much larger preview of the attached image — the 100x100 strip
    /// thumbnail is deliberately compact, but too small to make out details on its own.</summary>
    private void OnImageThumbClicked(object? sender, PointerPressedEventArgs e)
    {
        if (ImageThumb.Source == null) return;
        ImageThumbLarge.Source = ImageThumb.Source;
        global::Avalonia.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(ImageThumbBorder);
    }

    private async void OnOpenImageClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_OpenImage"],
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp" }
                }
            }
        });
        if (files.Count == 0) return;
        await LoadImageFromFileAsync(files[0]);
    }

    private async void OnPasteImageClicked(object? sender, RoutedEventArgs e)
    {
        await TryPasteImageFromClipboardAsync();
    }

    private async void OnUrlImageClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = AppServices.Get<DialogService>();
        var url = await dialog.ShowInputDialogAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_LoadImageFromURL"],
            "Paste an image URL (or right-click an image in your browser → Copy image address):",
            "");
        if (string.IsNullOrWhiteSpace(url)) return;
        await LoadImageFromUrlAsync(url.Trim());
    }

    /// <summary>
    /// Jumps to the artwork behind the current chat session in the Gallery's inline viewer.
    /// The Hoshi session/chat history is untouched — it lives independently in the sidebar,
    /// so navigating away and back preserves it automatically.
    /// </summary>
    private async void OnViewArtworkClicked(object? sender, RoutedEventArgs e)
    {
        var artworkId = GetCurrentArtworkId();
        if (string.IsNullOrEmpty(artworkId))
        {
            VM?.Messages.Add(new AiChatMessage { Role = "system", Content = "⚠ No Pixiv artwork is associated with this session." });
            return;
        }

        var mainWindow = TopLevel.GetTopLevel(this) as Pikura.Avalonia.Views.MainWindow;
        var galleryVm = AppServices.Get<GalleryViewModel>();
        mainWindow?.LoadGalleryView();
        try { await galleryVm.OpenArtworkByIdInNewTabAsync(artworkId); }
        catch (Exception ex) { galleryVm.StatusMessage = ex.Message; } // network failure: show it, don't crash
    }

    /// <summary>
    /// Opens a recommended/similar artwork result in a new Gallery viewer tab.
    /// The artwork ID is read from the message's DataContext so it can't be lost by a stale Tag binding.
    /// </summary>
    private async void OnOpenArtworkFromChat(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AiChatMessage msg } || string.IsNullOrEmpty(msg.ArtworkId)) return;
        try
        {
            var mainWindow = TopLevel.GetTopLevel(this) as Pikura.Avalonia.Views.MainWindow;
            var galleryVm  = AppServices.Get<GalleryViewModel>();
            mainWindow?.LoadGalleryView();
            await galleryVm.OpenArtworkByIdInNewTabAsync(msg.ArtworkId);
        }
        catch (Exception ex)
        {
            VM?.Messages.Add(new AiChatMessage { Role = "system", Content = $"✗ Could not open artwork: {ex.Message}" });
        }
    }

    /// <summary>
    /// Opens a Pixiv URL (artwork or artist) from a chat result in the default browser.
    /// The URL is read from the message's DataContext so it can't be lost by a stale Tag binding.
    /// </summary>
    private async void OnOpenUrlFromChat(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AiChatMessage msg } || string.IsNullOrEmpty(msg.PixivUrl)) return;
        try
        {
            var url = new Uri(msg.PixivUrl);
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher != null)
            {
                await launcher.LaunchUriAsync(url);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(msg.PixivUrl) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            VM?.Messages.Add(new AiChatMessage { Role = "system", Content = $"✗ Could not open URL: {ex.Message}" });
        }
    }

    /// <summary>
    /// Navigates to an artist's gallery from a recommended-artists result.
    /// The artist ID is read from the message's DataContext so it can't be lost by a stale Tag binding.
    /// </summary>
    private async void OnOpenArtistFromChat(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AiChatMessage msg } || string.IsNullOrEmpty(msg.ArtistId)) return;
        try
        {
            var mainWindow = TopLevel.GetTopLevel(this) as Pikura.Avalonia.Views.MainWindow;
            var galleryVm  = AppServices.Get<GalleryViewModel>();
            mainWindow?.LoadGalleryView();
            await galleryVm.LoadArtistByIdCommand.ExecuteAsync(msg.ArtistId);
        }
        catch (Exception ex)
        {
            VM?.Messages.Add(new AiChatMessage { Role = "system", Content = $"✗ Could not open artist gallery: {ex.Message}" });
        }
    }

    private async void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+V anywhere except inside an editable text input pastes an image from clipboard
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Don't hijack paste when the user is typing in the input box
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is TextBox) return;
            if (await TryPasteImageFromClipboardAsync()) e.Handled = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Avalonia 12: just accept any drop and let OnDrop figure out if it has a file
        e.DragEffects = DragDropEffects.Copy;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        try
        {
            // Avalonia 12 sync IDataTransfer extension: returns IStorageItem
            if (e.DataTransfer.TryGetFile() is IStorageFile sf)
                await LoadImageFromFileAsync(sf);
        }
        catch (Exception ex)
        {
            VM?.Messages.Add(new AiChatMessage { Role = "system", Content = $"✗ Drop failed: {ex.Message}" });
        }
    }

    // ── Image loading helpers ────────────────────────────────────────────────

    private async Task LoadImageFromFileAsync(IStorageFile file)
    {
        if (VM is not { } vm) return;
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();
            vm.SetSessionImage(bytes, file.Name);
            if (vm.CurrentSession is { } s && ViewModels.AiViewModel.IsDefaultChatTitle(s.Title))
            {
                vm.RenameCurrentSession(Path.GetFileNameWithoutExtension(file.Name));
                RefreshSessionTitle();
            }
            RefreshImageStrip();
        }
        catch (Exception ex)
        {
            vm.Messages.Add(new AiChatMessage { Role = "system", Content = $"✗ Could not load image: {ex.Message}" });
        }
    }

    private Task<bool> TryPasteImageFromClipboardAsync()
    {
        // Avalonia 12's clipboard read APIs are limited for arbitrary binary images.
        // Until the platform exposes a stable image-paste pattern, point users to the alternatives.
        VM?.Messages.Add(new AiChatMessage
        {
            Role = "system",
            Content = "Clipboard paste isn’t available yet — use 📂 Open, drag-and-drop, or 🌐 URL to attach an image."
        });
        return Task.FromResult(false);
    }

    private async Task LoadImageFromUrlAsync(string url)
    {
        if (VM is not { } vm) return;
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Pikura/1.0");
            var bytes = await http.GetByteArrayAsync(url);
            vm.SetSessionImage(bytes, url);
            if (vm.CurrentSession is { } s && ViewModels.AiViewModel.IsDefaultChatTitle(s.Title))
            {
                vm.RenameCurrentSession(new Uri(url).Host);
                RefreshSessionTitle();
            }
            RefreshImageStrip();
        }
        catch (Exception ex)
        {
            vm.Messages.Add(new AiChatMessage { Role = "system", Content = $"✗ Could not fetch image from URL: {ex.Message}" });
        }
    }
    
    private void OnGroupHeaderClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is SessionGroupViewModel group)
        {
            group.IsExpanded = !group.IsExpanded;
            
            // Update the expand icon by finding it in the border's child grid
            if (border.Child is Grid grid && grid.ColumnDefinitions.Count == 2)
            {
                if (grid.Children[1] is TextBlock icon)
                {
                    icon.Text = group.IsExpanded ? "▼" : "▶";
                }
            }
        }
    }

    private async Task OnUnloadedAsync()
    {
        // Cancel any in-flight AI generation before saving so the process can shut down cleanly
        if (VM is { } vm)
        {
            // NOTE: no CancelPending here — an in-flight answer belongs to its origin
            // chat and must finish streaming (it persists into that session either way).

            // Save the current session when navigating away from Hoshi
            try
            {
                await vm.OnViewUnloadingAsync();
            }
            catch (Exception ex)
            {
                // Log error but don't crash the navigation
                System.Diagnostics.Debug.WriteLine($"Error saving session on unload: {ex.Message}");
            }
        }
    }

    // ⋯ sidebar menu: toggles multi-select checkboxes.
    private void OnToggleSelectionMode(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AiViewModel vm) vm.IsSelectionMode = !vm.IsSelectionMode;
    }

    // Empty-chat suggestion chips: put the text in the box and send.
    private async void OnSuggestionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text } && InputBox != null)
        {
            InputBox.Text = text;
            await SendAsync();
        }
    }

    // ── Folders + archive ─────────────────────────────────────────────────────
    private async void OnNewFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        var dialog = AppServices.Get<DialogService>();
        var name = await dialog.ShowInputDialogAsync(
            Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_NewFolder"],
            Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_FolderName"]);
        if (!string.IsNullOrWhiteSpace(name)) await vm.SessionsService.CreateFolderAsync(name.Trim());
    }

    private async void OnFolderHeaderClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c || c.Tag is not AiViewModel.FolderRow row || VM is not { } vm) return;
        await vm.SessionsService.SetFolderCollapsedAsync(row.Folder.Id, !row.Folder.Collapsed);
    }

    private async void OnRenameFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.Tag is not AiViewModel.FolderRow row || VM is not { } vm) return;
        var dialog = AppServices.Get<DialogService>();
        var name = await dialog.ShowInputDialogAsync(
            Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_RenameFolder"],
            Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_FolderName"], row.Folder.Name);
        if (!string.IsNullOrWhiteSpace(name)) await vm.SessionsService.RenameFolderAsync(row.Folder.Id, name.Trim());
    }

    private async void OnDeleteFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.Tag is not AiViewModel.FolderRow row || VM is not { } vm) return;
        var dialog = AppServices.Get<DialogService>();
        var ok = await dialog.ShowConfirmationAsync(
            Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_DeleteFolder"],
            Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_DeleteFolderConfirm"]);
        if (ok) await vm.SessionsService.DeleteFolderAsync(row.Folder.Id);
    }

    private async void OnArchiveSessionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.Tag is not HoshiSession sess || VM is not { } vm) return;
        await vm.SessionsService.SetSessionArchivedAsync(sess, !sess.Archived);
    }

    private async void OnArchiveSelectedClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        foreach (var sess in vm.Sessions.Where(x => x.IsSelected))
            await vm.SessionsService.SetSessionArchivedAsync(sess, true);
    }

    private void OnSessionContextMenuOpening(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm || cm.Tag is not HoshiSession sess || VM is not { } vm) return;
        foreach (var item in cm.Items.OfType<MenuItem>())
        {
            if (item.Items.Count == 1 && item.Items[0] is MenuItem { Name: "MoveToFolderList" } placeholder)
            {
                item.Items.Clear();
                foreach (var f in vm.SessionsService.Folders)
                {
                    var mi = new MenuItem { Header = f.Name, Tag = (sess.Id, f.Id) };
                    mi.Click += OnMoveSessionToFolderClicked;
                    item.Items.Add(mi);
                }
                var nf = new MenuItem { Header = Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_NewFolder"] };
                nf.Click += async (_, _) =>
                {
                    var dialog = AppServices.Get<DialogService>();
                    var name = await dialog.ShowInputDialogAsync(
                        Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_NewFolder"],
                        Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_FolderName"]);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var id = await vm.SessionsService.CreateFolderAsync(name.Trim());
                        await vm.SessionsService.SetSessionFolderAsync(sess, id);
                    }
                };
                item.Items.Add(nf);
                var rm = new MenuItem { Header = Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_RemoveFromFolder"] };
                rm.Click += async (_, _) => await vm.SessionsService.SetSessionFolderAsync(sess, null);
                item.Items.Add(rm);
            }
        }
    }

    private async void OnMoveSessionToFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not (string sessionId, string folderId) || VM is not { } vm) return;
        var sess = vm.Sessions.FirstOrDefault(x => x.Id == sessionId);
        if (sess != null) await vm.SessionsService.SetSessionFolderAsync(sess, folderId);
    }

    private async void OnMoveSelectedClicked(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        var dialog = AppServices.Get<DialogService>();
        var name = await dialog.ShowInputDialogAsync(
            Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_MoveSelectedToFolder"],
            Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_FolderName"]);
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = await vm.SessionsService.CreateFolderAsync(name.Trim());
        foreach (var sess in vm.Sessions.Where(x => x.IsSelected))
            await vm.SessionsService.SetSessionFolderAsync(sess, id);
    }
}
