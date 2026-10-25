using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Views.Dialogs;

/// <summary>
/// Dialog for selecting followed artists with pagination and search.
/// Tracks most-selected artists for personalized sorting.
/// </summary>
public partial class SelectFollowedArtistsDialog : Window
{
    private List<SelectableArtist> _allArtists = new();
    private List<SelectableArtist> _filteredArtists = new();
    private int _currentPage = 1;
    private const int PageSize = 20;
    private int _totalPages = 1;
    private HttpClient _httpClient = new();

    // Static dictionary to track selection counts across sessions
    private static readonly Dictionary<string, int> _selectionCounts = new();
    private static readonly Dictionary<string, DateTime> _lastSelected = new();

    public List<SelectableArtist> SelectedArtists => _allArtists.Where(a => a.IsSelected && !a.IsAlreadyAdded).ToList();

    public SelectFollowedArtistsDialog()
    {
        InitializeComponent();
        SetupHandlers();
    }

    public SelectFollowedArtistsDialog(IEnumerable<SelectableArtist> artists, string pageInfo = "")
    {
        InitializeComponent();
        SetupHandlers();

        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://www.pixiv.net/");

        // Sort artists by most-selected count (descending), then alphabetically
        _allArtists = artists.OrderByDescending(a => _selectionCounts.GetValueOrDefault(a.User.UserId, 0))
                             .ThenBy(a => a.User.UserName)
                             .ToList();
        _filteredArtists = _allArtists.ToList();
        StatusText.Text = string.IsNullOrEmpty(pageInfo) ? $"Showing {_allArtists.Count} artists" : pageInfo;
    }

    private void SetupHandlers()
    {
        CancelButton.Click += (s, e) => Close(false);
        AddButton.Click += (s, e) =>
        {
            // Track selected artists before closing
            TrackSelections();
            Close(true);
        };
        SearchButton.Click += (s, e) => PerformSearch();
        ClearSearchButton.Click += (s, e) => ClearSearch();
        PrevPageButton.Click += (s, e) => ChangePage(-1);
        NextPageButton.Click += (s, e) => ChangePage(1);
        SelectAllButton.Click += (s, e) => SelectAllOnPage();

        SearchTextBox.KeyDown += (s, e) =>
        {
            if (e.Key == global::Avalonia.Input.Key.Enter)
                PerformSearch();
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        LoadPage(1);
    }

    /// <summary>
    /// Add more artists to the dialog (used for background loading).
    /// </summary>
    public void AddArtists(IEnumerable<SelectableArtist> artists)
    {
        var search = SearchTextBox.Text?.Trim() ?? "";
        var currentCount = _allArtists.Count;

        // Add to main list maintaining sort order
        foreach (var artist in artists)
        {
            if (!_allArtists.Any(a => a.User.UserId == artist.User.UserId))
            {
                _allArtists.Add(artist);
            }
        }

        // Re-sort by selection counts
        _allArtists = _allArtists.OrderByDescending(a => _selectionCounts.GetValueOrDefault(a.User.UserId, 0))
                                 .ThenBy(a => a.User.UserName)
                                 .ToList();

        // Update filtered list if no search active
        if (string.IsNullOrEmpty(search))
        {
            _filteredArtists = _allArtists.ToList();
        }
        else
        {
            // Re-apply search filter
            _filteredArtists = _allArtists.Where(a =>
                (!string.IsNullOrEmpty(a.User.UserName) && a.User.UserName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(a.User.UserId) && a.User.UserId.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        // Refresh current page if needed
        LoadPage(_currentPage);

        // Update status
        UpdateStatusText();
    }

    private void PerformSearch()
    {
        var search = SearchTextBox.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(search))
        {
            _filteredArtists = _allArtists.ToList();
        }
        else
        {
            // Case-insensitive partial match like Gallery
            _filteredArtists = _allArtists.Where(a =>
                (!string.IsNullOrEmpty(a.User.UserName) && a.User.UserName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(a.User.UserId) && a.User.UserId.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        LoadPage(1);
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        var search = SearchTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(search))
        {
            StatusText.Text = $"Showing {_filteredArtists.Count} artists";
        }
        else
        {
            StatusText.Text = $"Found {_filteredArtists.Count} matching '{search}'";
        }
    }

    private void ClearSearch()
    {
        SearchTextBox.Text = "";
        _filteredArtists = _allArtists.ToList();
        LoadPage(1);
        UpdateStatusText();
    }

    private void ChangePage(int delta)
    {
        var newPage = _currentPage + delta;
        if (newPage >= 1 && newPage <= _totalPages)
        {
            LoadPage(newPage);
        }
    }

    private void LoadPage(int page)
    {
        _totalPages = (_filteredArtists.Count + PageSize - 1) / PageSize;
        if (_totalPages < 1) _totalPages = 1;

        _currentPage = page;
        if (_currentPage < 1) _currentPage = 1;
        if (_currentPage > _totalPages) _currentPage = _totalPages;

        var pageArtists = _filteredArtists
            .Skip((_currentPage - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        // Build UI first (show immediately without avatars)
        ArtistsPanel?.Children.Clear();
        foreach (var artist in pageArtists)
        {
            ArtistsPanel?.Children.Add(CreateRow(artist));
        }

        // Update page info
        PageInfoText.Text = $"Page {_currentPage} of {_totalPages} ({_filteredArtists.Count} total)";
        PrevPageButton.IsEnabled = _currentPage > 1;
        NextPageButton.IsEnabled = _currentPage < _totalPages;

        UpdateSummary();

        // Load avatars in background after UI is shown
        _ = LoadAvatarsInBackground(pageArtists);
    }

    private async Task LoadAvatarsInBackground(List<SelectableArtist> artists)
    {
        bool anyLoaded = false;
        foreach (var artist in artists)
        {
            if (!string.IsNullOrEmpty(artist.AvatarUrl) && artist.AvatarBitmap == null)
            {
                try
                {
                    // Upgrade URL for better quality
                    var url = artist.AvatarUrl.Replace("_square50", "_square120").Replace("_50", "_120");
                    var bytes = await _httpClient.GetByteArrayAsync(url);
                    using var stream = new System.IO.MemoryStream(bytes);
                    artist.AvatarBitmap = Bitmap.DecodeToWidth(stream, 96);
                    anyLoaded = true;
                }
                catch
                {
                    // Ignore avatar load errors
                }
            }
        }

        // Refresh UI to show loaded avatars
        if (anyLoaded)
        {
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                LoadPage(_currentPage);
            });
        }
    }

    private void SelectAllOnPage()
    {
        var pageArtists = _filteredArtists
            .Skip((_currentPage - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        foreach (var artist in pageArtists.Where(a => !a.IsAlreadyAdded))
        {
            artist.IsSelected = true;
        }

        LoadPage(_currentPage); // Refresh checkboxes
    }

    private Control CreateRow(SelectableArtist artist)
    {
        // Avatar placeholder
        var avatarBorder = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Colors.Gray),
            Margin = new Thickness(0, 0, 12, 0)
        };

        // Avatar image (if loaded)
        if (artist.AvatarBitmap != null)
        {
            avatarBorder.Child = new Image
            {
                Source = artist.AvatarBitmap,
                Width = 40,
                Height = 40,
                Stretch = Stretch.UniformToFill
            };
        }

        var checkBox = new CheckBox
        {
            IsChecked = artist.IsSelected,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            IsEnabled = !artist.IsAlreadyAdded,
            Margin = new Thickness(0, 0, 8, 0)
        };
        checkBox.IsCheckedChanged += (s, e) =>
        {
            artist.IsSelected = checkBox.IsChecked ?? false;
            UpdateSummary();
        };

        var nameText = new TextBlock
        {
            Text = artist.User.UserName,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14
        };

        var idText = new TextBlock
        {
            Text = $"ID: {artist.User.UserId}",
            FontSize = 11,
            Opacity = 0.7,
            Cursor = new Cursor(StandardCursorType.Ibeam) // Indicates it's clickable/copyable
        };

        // Click to copy ID
        idText.PointerPressed += (s, e) =>
        {
            if (e.GetCurrentPoint(idText).Properties.IsLeftButtonPressed)
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                {
                    var dt = new DataTransfer();
                    dt.Add(DataTransferItem.CreateText(artist.User.UserId));
                    _ = clipboard.SetDataAsync(dt);

                    // Also save to quick clipboard for easy pasting
                    QuickClipboardService.CopyArtist(artist.User.UserId);

                    idText.Text = $"ID: {artist.User.UserId} (copied!)";
                    idText.Opacity = 1;
                    // Reset after 2 seconds
                    Task.Delay(2000).ContinueWith(_ =>
                    {
                        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            idText.Text = $"ID: {artist.User.UserId}";
                            idText.Opacity = 0.7;
                        });
                    });
                }
            }
        };

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(nameText);
        info.Children.Add(idText);

        if (artist.IsAlreadyAdded)
        {
            info.Children.Add(new TextBlock
            {
                Text = "✓ Already added",
                FontSize = 11,
                Foreground = Brushes.Green
            });
        }

        var profileBtn = new Button
        {
            Content = "🌐",
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(profileBtn, "Open profile on Pixiv");
        profileBtn.Click += (s, e) =>
        {
            Process.Start(new ProcessStartInfo($"https://www.pixiv.net/en/users/{artist.User.UserId}") { UseShellExecute = true });
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(8)
        };
        Grid.SetColumn(checkBox, 0);
        Grid.SetColumn(avatarBorder, 1);
        Grid.SetColumn(info, 2);
        Grid.SetColumn(profileBtn, 3);
        grid.Children.Add(checkBox);
        grid.Children.Add(avatarBorder);
        grid.Children.Add(info);
        grid.Children.Add(profileBtn);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Colors.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2),
            Child = grid
        };
    }

    private void UpdateSummary()
    {
        if (SummaryText == null) return;
        var selected = _allArtists.Count(a => a.IsSelected && !a.IsAlreadyAdded);
        var total = _allArtists.Count(a => !a.IsAlreadyAdded);
        SummaryText.Text = $"{selected} of {total} selected";
    }

    /// <summary>
    /// Track selected artists for personalized sorting in future sessions.
    /// </summary>
    private void TrackSelections()
    {
        var selectedArtists = _allArtists.Where(a => a.IsSelected && !a.IsAlreadyAdded);
        foreach (var artist in selectedArtists)
        {
            var userId = artist.User.UserId;
            _selectionCounts[userId] = _selectionCounts.GetValueOrDefault(userId, 0) + 1;
            _lastSelected[userId] = DateTime.Now;
        }
    }

    /// <summary>
    /// Unfollow an artist using the Pixiv API.
    /// NOTE: This feature is disabled as Pixiv OAuth is no longer available.
    /// </summary>
    private Task<bool> UnfollowArtistAsync(string userId)
    {
        StatusText.Text = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_UnfollowNotAvailablePixivOAuthAuthentication"];
        return Task.FromResult(false);
    }
}
