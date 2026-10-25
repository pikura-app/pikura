using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Styling;
using Microsoft.Extensions.Logging;
using Pikura.Avalonia.ViewModels;
using Pikura.Core.Data;
using Pikura.Core.Http;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using Pikura.Avalonia.Localization;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Dialog service for Avalonia application
/// </summary>
public class DialogService
{
    private readonly ILogger<DialogService> _logger;
    private Window? _ownerWindow;

    public DialogService(ILogger<DialogService> logger)
    {
        _logger = logger;
    }

    public void Initialize(Window ownerWindow)
    {
        _ownerWindow = ownerWindow;
    }

    public Window? OwnerWindow => _ownerWindow;

    public async Task<bool> ShowConfirmationAsync(string title, string message)
    {
        try
        {
            var isDarkMode = global::Avalonia.Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;
            var textBlock = new TextBlock 
            { 
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400,
                Foreground = isDarkMode ? Brushes.White : Brushes.Black
            };
            
            var dialog = new ContentDialog
            {
                Title = title,
                Content = textBlock,
                PrimaryButtonText = Pikura.Avalonia.Localization.LocalizationService.Instance["Common_OK"],
                SecondaryButtonText = Pikura.Avalonia.Localization.LocalizationService.Instance["Common_Cancel"],
                IsDarkMode = isDarkMode
            };

            if (_ownerWindow != null)
            {
                var result = await dialog.ShowAsync(_ownerWindow);
                return result == ContentDialogResult.Primary;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show confirmation dialog");
            return false;
        }
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        try
        {
            var isDarkMode = global::Avalonia.Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;
            var textBlock = new TextBlock 
            { 
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400,
                Foreground = isDarkMode ? Brushes.White : Brushes.Black
            };
            
            var dialog = new ContentDialog
            {
                Title = title,
                Content = textBlock,
                PrimaryButtonText = Pikura.Avalonia.Localization.LocalizationService.Instance["Common_OK"],
                IsDarkMode = isDarkMode
            };

            if (_ownerWindow != null)
            {
                await dialog.ShowAsync(_ownerWindow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show message dialog");
        }
    }

    public async Task<string?> ShowInputDialogAsync(string title, string message, string defaultValue = "")
    {
        try
        {
            if (_ownerWindow == null) return null;
            var dialog = new Views.Dialogs.TextInputDialog
            {
                DialogTitle = title,
                Description = message,
                InitialValue = defaultValue
            };
            var result = await dialog.ShowDialog<string?>(_ownerWindow);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show input dialog");
            return null;
        }
    }

    /// <summary>
    /// Shows the "add to collage" picker. Lets the user check one or more of the currently open
    /// collages — and optionally create a new one — as targets for a set of artworks.
    /// Returns null when the dialog is cancelled.
    /// </summary>
    public async Task<CollagePickerResult?> ShowCollagePickerAsync(
        IReadOnlyList<ViewerTab> collages,
        int itemCount,
        ViewerTab? preselect = null)
    {
        try
        {
            if (_ownerWindow == null) return null;
            var dialog = new Views.Dialogs.CollagePickerDialog(collages, itemCount, preselect);
            return await dialog.ShowDialog<CollagePickerResult?>(_ownerWindow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show collage picker dialog");
            return null;
        }
    }

    public async Task<ImageEditPreset?> ShowDownloadPresetDialogAsync(
        ArtworkPreview artwork,
        List<ArtworkPreview>? additionalArtworks = null)
    {
        try
        {
            if (_ownerWindow == null) return null;

            // Convert Core.Models.ArtworkPreview to Dialogs.ArtworkPreview
            var selectedArtworks = new List<Views.Dialogs.ArtworkPreview>
            {
                new Views.Dialogs.ArtworkPreview
                {
                    ArtworkId = artwork.Id ?? "",
                    Title = artwork.Title ?? "",
                    ArtistName = artwork.UserName ?? "",
                    ThumbnailUrl = artwork.ThumbnailUrl,
                    PageCount = artwork.PageCount,
                    IllustType = artwork.IllustType
                }
            };

            if (additionalArtworks != null)
            {
                selectedArtworks.AddRange(additionalArtworks.Select(a => new Views.Dialogs.ArtworkPreview
                {
                    ArtworkId = a.Id ?? "",
                    Title = a.Title ?? "",
                    ArtistName = a.UserName ?? "",
                    ThumbnailUrl = a.ThumbnailUrl,
                    PageCount = a.PageCount,
                    IllustType = a.IllustType
                }));
            }

            var imageResizeService = AppServices.Get<ImageResizeService>();
            var imageLoader = AppServices.Get<PixivImageLoader>();
            var pixivClient = AppServices.Get<PixivClient>();
            var presetsRepo = AppServices.Get<UserPresetsRepository>();
            var customPresets = presetsRepo != null ? await presetsRepo.GetAllAsync() : new List<ImageEditPreset>();

            var window = new Views.Dialogs.DownloadPresetWindow(
                imageResizeService,
                this,
                imageLoader,
                pixivClient,
                selectedArtworks,
                customPresets);

            var result = await window.ShowDialog<ImageEditPreset?>(_ownerWindow);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show download preset dialog");
            return null;
        }
    }
}

// Simple ContentDialog implementation for Avalonia
public class ContentDialog : ContentControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ContentDialog, string>(nameof(Title));

    public static readonly StyledProperty<string> PrimaryButtonTextProperty =
        AvaloniaProperty.Register<ContentDialog, string>(nameof(PrimaryButtonText));

    public static readonly StyledProperty<string> SecondaryButtonTextProperty =
        AvaloniaProperty.Register<ContentDialog, string>(nameof(SecondaryButtonText));

    public static readonly StyledProperty<bool> IsDarkModeProperty =
        AvaloniaProperty.Register<ContentDialog, bool>(nameof(IsDarkMode), false);

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string PrimaryButtonText
    {
        get => GetValue(PrimaryButtonTextProperty);
        set => SetValue(PrimaryButtonTextProperty, value);
    }

    public string SecondaryButtonText
    {
        get => GetValue(SecondaryButtonTextProperty);
        set => SetValue(SecondaryButtonTextProperty, value);
    }

    public bool IsDarkMode
    {
        get => GetValue(IsDarkModeProperty);
        set => SetValue(IsDarkModeProperty, value);
    }

    public async Task<ContentDialogResult> ShowAsync(Window parent)
    {
        var result = ContentDialogResult.None;

        var dialog = new Window
        {
            Title = Title,
            SizeToContent = SizeToContent.Height,
            Width = 450,
            MaxWidth = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = IsDarkMode ? new SolidColorBrush(Color.Parse("#1e1e1e")) : new SolidColorBrush(Color.Parse("#ffffff")),
            // Inherit the parent's theme so dark mode is applied
            RequestedThemeVariant = parent.RequestedThemeVariant
                ?? global::Avalonia.Application.Current?.RequestedThemeVariant,
        };

        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(24, 24, 24, 20) };

        if (Content is Control contentControl)
            panel.Children.Add(contentControl);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };

        if (!string.IsNullOrEmpty(SecondaryButtonText))
        {
            var secondaryButton = new Button { Content = SecondaryButtonText, Padding = new Thickness(20, 8), MinWidth = 80 };
            secondaryButton.Click += (_, _) => { result = ContentDialogResult.Secondary; dialog.Close(); };
            buttonPanel.Children.Add(secondaryButton);
        }

        if (!string.IsNullOrEmpty(PrimaryButtonText))
        {
            var primaryButton = new Button { Content = PrimaryButtonText, Padding = new Thickness(20, 8), MinWidth = 80 };
            primaryButton.Click += (_, _) => { result = ContentDialogResult.Primary; dialog.Close(); };
            buttonPanel.Children.Add(primaryButton);
        }

        panel.Children.Add(buttonPanel);
        dialog.Content = panel;

        await dialog.ShowDialog(parent);
        return result;
    }
}

public enum ContentDialogResult
{
    None,
    Primary,
    Secondary
}

public enum RedownloadChoice { Yes, YesToAll, No, NoToAll }

public static class RedownloadConfirmDialog
{
    /// <summary>
    /// Shows a modal dialog asking whether to re-download an artwork that already
    /// exists on disk.  The dialog displays the artwork thumbnail (if available),
    /// the title, and four action buttons.
    /// </summary>
    public static async Task<RedownloadChoice> ShowAsync(
        Window parent,
        string title,
        Bitmap? thumbnail)
    {
        var choice = RedownloadChoice.No;

        var dialog = new Window
        {
            Title = Pikura.Avalonia.Localization.LocalizationService.Instance["Dialog_FileAlreadyExists"],
            SizeToContent = SizeToContent.Height,
            Width = 520,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = parent.RequestedThemeVariant
                ?? global::Avalonia.Application.Current?.RequestedThemeVariant,
        };

        var outer = new StackPanel { Spacing = 16, Margin = new Thickness(24, 20, 24, 20) };

        // Header
        outer.Children.Add(new TextBlock
        {
            Text = "File already exists",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold
        });

        // Thumbnail + title row
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        if (thumbnail != null)
        {
            row.Children.Add(new Border
            {
                Width = 160, Height = 160,
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                Child = new Image
                {
                    Source = thumbnail,
                    Stretch = Stretch.UniformToFill
                }
            });
        }
        row.Children.Add(new TextBlock
        {
            Text = $"\"{title}\" is already downloaded.\nDo you want to re-download it?",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = thumbnail != null ? 310 : 460,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        });
        outer.Children.Add(row);

        // Buttons
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        Button MakeBtn(string label, RedownloadChoice c, bool isAccent = false)
        {
            var b = new Button
            {
                Content = label,
                Padding = new Thickness(16, 7),
                MinWidth = 80
            };
            if (isAccent)
            {
                b.Classes.Add("accent");
            }
            b.Click += (_, _) => { choice = c; dialog.Close(); };
            return b;
        }

        btnPanel.Children.Add(MakeBtn("No to all",  RedownloadChoice.NoToAll));
        btnPanel.Children.Add(MakeBtn("No",          RedownloadChoice.No));
        btnPanel.Children.Add(MakeBtn("Yes",         RedownloadChoice.Yes, isAccent: true));
        btnPanel.Children.Add(MakeBtn("Yes to all",  RedownloadChoice.YesToAll, isAccent: true));

        outer.Children.Add(btnPanel);
        dialog.Content = outer;

        await dialog.ShowDialog(parent);
        return choice;
    }
}
