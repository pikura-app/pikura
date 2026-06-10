using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Pikura.Avalonia.Services;
using Pikura.Core.Models;
using Pikura.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orientation = Avalonia.Layout.Orientation;
using VerticalAlignment = Avalonia.Layout.VerticalAlignment;

namespace Pikura.Avalonia.Views.Download;

public partial class DownloadQueueView : UserControl
{
    private readonly DownloadCoordinator _coordinator;
    private Window? _ownerWindow;
    private readonly Dictionary<Guid, (ProgressBar Bar, TextBlock Label, TextBlock SubLabel)> _liveCards = new();
    private DispatcherTimer? _refreshTimer;

    // Drag-and-drop reorder state
    private Border? _dragCard;
    private Guid _dragJobId;
    private int _dragOriginalIndex;
    private Border? _dropTarget;
    private static readonly SolidColorBrush DropHighlight = new(Color.FromRgb(96, 165, 250));

    public DownloadQueueView()
    {
        InitializeComponent();
        _coordinator = AppServices.Get<DownloadCoordinator>();
        _coordinator.JobCompleted += (_, _) => _ = LoadJobsAsync();
        _ = LoadJobsAsync();
    }

    public void SetOwnerWindow(Window window)
    {
        _ownerWindow = window;
        // Start a refresh timer while the window is open
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += (_, _) => _ = LoadJobsAsync();
        _refreshTimer.Start();
        window.Closed += (_, _) => _refreshTimer?.Stop();
    }

    private async Task LoadJobsAsync()
    {
        var jobs = await _coordinator.GetJobsAsync(limit: 50);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _liveCards.Clear();
            JobsPanel.Children.Clear();
            if (jobs.Count == 0)
            {
                JobCountLabel.Text = "(empty)";
                JobsPanel.Children.Add(new TextBlock
                {
                    Text = "No download jobs yet.",
                    Foreground = Brush.Parse("#6B7280"),
                    Margin = new Thickness(8, 16),
                    FontSize = 13
                });
                return;
            }

            JobCountLabel.Text = $"({jobs.Count} jobs)";
            foreach (var job in jobs)
                JobsPanel.Children.Add(BuildJobCard(job));
        });
    }

    private void SubscribeToLiveProgress(DownloadJob job,
        ProgressBar bar, TextBlock label, TextBlock subLabel)
    {
        var progress = new Progress<JobProgress>(p =>
        {
            bar.Value = p.PercentComplete;
            label.Text = $"{p.PercentComplete:F0}%  {p.Message}";
            subLabel.Text = $"{p.CompletedTargets}/{p.TotalTargets} artworks";
        });
        _liveCards[job.Id] = (bar, label, subLabel);
        _coordinator.SubscribeToProgress(job.Id, progress);
    }

    private Border BuildJobCard(DownloadJob job)
    {
        var statusColor = job.Status switch
        {
            JobStatus.Completed => "#4ADE80",
            JobStatus.Failed => "#F87171",
            JobStatus.Running => "#60A5FA",
            JobStatus.Cancelled => "#9CA3AF",
            _ => "#9CA3AF"
        };

        var card = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var info = new StackPanel { Spacing = 4 };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = job.Name,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13
        });
        titleRow.Children.Add(new Border
        {
            Background = Brush.Parse(statusColor + "33"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            Child = new TextBlock
            {
                Text = job.Status.ToString(),
                FontSize = 10,
                Foreground = Brush.Parse(statusColor)
            }
        });
        info.Children.Add(titleRow);

        info.Children.Add(new TextBlock
        {
            Text = $"{job.CompletedItems}/{job.TotalItems} targets  ·  Created {job.CreatedAt:yyyy-MM-dd HH:mm}",
            FontSize = 11,
            Foreground = Brush.Parse("#9CA3AF")
        });

        if (job.Status == JobStatus.Running)
        {
            var bar = new ProgressBar
            {
                Value = 0,
                Maximum = 100,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                ShowProgressText = false,
                IsIndeterminate = false,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var progressLabel = new TextBlock
            {
                Text = "0%",
                FontSize = 11,
                Foreground = Brush.Parse("#9CA3AF")
            };
            var artworkLabel = new TextBlock
            {
                Text = "Starting...",
                FontSize = 11,
                Foreground = Brush.Parse("#9CA3AF")
            };
            info.Children.Add(bar);
            info.Children.Add(progressLabel);
            info.Children.Add(artworkLabel);
            SubscribeToLiveProgress(job, bar, progressLabel, artworkLabel);
        }

        if (!string.IsNullOrEmpty(job.ErrorMessage))
        {
            info.Children.Add(new TextBlock
            {
                Text = "⚠ " + job.ErrorMessage,
                FontSize = 11,
                Foreground = Brush.Parse("#F87171"),
                TextWrapping = TextWrapping.Wrap
            });
        }

        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        // Action buttons
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (job.Status == JobStatus.Pending || job.Status == JobStatus.Paused)
        {
            var startBtn = new Button
            {
                Content = job.Status == JobStatus.Paused ? "▶ Resume" : "▶ Start",
                FontSize = 11,
                Padding = new Thickness(8, 4),
                CornerRadius = new CornerRadius(5)
            };
            startBtn.Click += async (_, _) =>
            {
                await _coordinator.StartJobAsync(job.Id);
                await LoadJobsAsync();
            };
            btnPanel.Children.Add(startBtn);
        }

        if (job.Status == JobStatus.Running)
        {
            var pauseBtn = new Button
            {
                Content = "⏸ Pause",
                FontSize = 11,
                Padding = new Thickness(8, 4),
                CornerRadius = new CornerRadius(5)
            };
            pauseBtn.Click += async (_, _) =>
            {
                await _coordinator.PauseJobAsync(job.Id);
                await LoadJobsAsync();
            };
            btnPanel.Children.Add(pauseBtn);
        }

        if (job.Status == JobStatus.Running || job.Status == JobStatus.Pending || job.Status == JobStatus.Paused)
        {
            var cancelBtn = new Button
            {
                Content = "✕ Cancel",
                FontSize = 11,
                Padding = new Thickness(8, 4),
                CornerRadius = new CornerRadius(5)
            };
            cancelBtn.Click += async (_, _) =>
            {
                await _coordinator.CancelJobAsync(job.Id);
                await LoadJobsAsync();
            };
            btnPanel.Children.Add(cancelBtn);
        }

        if (btnPanel.Children.Count > 0)
        {
            Grid.SetColumn(btnPanel, 1);
            grid.Children.Add(btnPanel);
        }

        // Right-click reorder menu for active/pending/paused jobs
        var isReorderable = job.Status is JobStatus.Pending or JobStatus.Paused or JobStatus.Running;
        if (isReorderable)
        {
            var menu = new ContextMenu();

            var toTop = new MenuItem { Header = "⏫ Move to Top" };
            toTop.Click += async (_, _) =>
            {
                await _coordinator.ReorderJobAsync(job.Id, DownloadCoordinator.ReorderAction.MoveToTop);
                await LoadJobsAsync();
            };

            var up = new MenuItem { Header = "▲ Move Up" };
            up.Click += async (_, _) =>
            {
                await _coordinator.ReorderJobAsync(job.Id, DownloadCoordinator.ReorderAction.MoveUp);
                await LoadJobsAsync();
            };

            var down = new MenuItem { Header = "▼ Move Down" };
            down.Click += async (_, _) =>
            {
                await _coordinator.ReorderJobAsync(job.Id, DownloadCoordinator.ReorderAction.MoveDown);
                await LoadJobsAsync();
            };

            var toBottom = new MenuItem { Header = "⏬ Move to Bottom" };
            toBottom.Click += async (_, _) =>
            {
                await _coordinator.ReorderJobAsync(job.Id, DownloadCoordinator.ReorderAction.MoveToBottom);
                await LoadJobsAsync();
            };

            menu.Items.Add(toTop);
            menu.Items.Add(up);
            menu.Items.Add(down);
            menu.Items.Add(toBottom);
            card.ContextMenu = menu;

            // Drag-and-drop reordering
            card.Cursor = new Cursor(StandardCursorType.SizeAll);
            card.PointerPressed += OnCardPointerPressed;
            card.PointerMoved += OnCardPointerMoved;
            card.PointerReleased += OnCardPointerReleased;
            card.Tag = job.Id;
        }

        card.Child = grid;
        return card;
    }

    // ── Drag-and-drop reorder ────────────────────────────────────────────────

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border card) return;
        if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
        _dragCard = card;
        _dragJobId = (Guid)(card.Tag ?? Guid.Empty);
        _dragOriginalIndex = JobsPanel.Children.IndexOf(card);
        e.Pointer.Capture(card);
    }

    private void OnCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCard == null) return;
        var pos = e.GetPosition(JobsPanel);

        // Find which card we're hovering over
        Border? hovered = null;
        foreach (var child in JobsPanel.Children.OfType<Border>())
        {
            var bounds = child.Bounds;
            if (pos.Y >= bounds.Top && pos.Y <= bounds.Bottom)
            {
                hovered = child;
                break;
            }
        }

        if (hovered != null && hovered != _dragCard)
        {
            if (_dropTarget != null && _dropTarget != hovered)
                _dropTarget.BorderBrush = Brushes.Transparent;

            _dropTarget = hovered;
            hovered.BorderBrush = DropHighlight;

            // Reorder visually in the panel
            var dragIdx = JobsPanel.Children.IndexOf(_dragCard);
            var dropIdx = JobsPanel.Children.IndexOf(hovered);
            if (dragIdx >= 0 && dropIdx >= 0 && dragIdx != dropIdx)
            {
                JobsPanel.Children.RemoveAt(dragIdx);
                JobsPanel.Children.Insert(dropIdx, _dragCard);
            }
        }
    }

    private async void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragCard == null) return;

        if (_dropTarget != null)
            _dropTarget.BorderBrush = Brushes.Transparent;

        e.Pointer.Capture(null);

        // Persist the new order by assigning sort_order = index for each card
        var cards = JobsPanel.Children.OfType<Border>()
            .Where(b => b.Tag is Guid)
            .ToList();

        for (var i = 0; i < cards.Count; i++)
        {
            if (cards[i].Tag is Guid id)
                await _coordinator.SetJobSortOrderAsync(id, i);
        }

        _dragCard = null;
        _dropTarget = null;

        await LoadJobsAsync();
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => _ = LoadJobsAsync();

    private async void OnClearCompleted(object? sender, RoutedEventArgs e)
    {
        var jobs = await _coordinator.GetJobsAsync();
        foreach (var job in jobs)
        {
            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                await _coordinator.DeleteJobAsync(job.Id);
        }
        await LoadJobsAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_ownerWindow is Window w)
            w.Close();
    }
}
