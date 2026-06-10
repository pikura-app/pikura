using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views.History;

public partial class HistoryView : UserControl
{
    private bool _dragging;
    private int  _dragFromIndex = -1;
    private Point _dragStart;
    private const double DragThreshold = 6;
    private Control? _draggedCard;
    private Transitions? _savedTransitions;
    private double _rowHeight = 86;
    private readonly List<Transitions?> _allSavedTransitions = new();
    private readonly Dictionary<Control, double> _preMoveY = new();

    private void ApplyDragTransform(double deltaY)
    {
        if (_draggedCard == null) return;
        var y = deltaY.ToString("F2", CultureInfo.InvariantCulture);
        _draggedCard.RenderTransform = TransformOperations.Parse($"translateY({y}px) scale(1.03)");
    }

    public HistoryView()
    {
        InitializeComponent();
        ActiveJobsList.Loaded += OnActiveJobsListLoaded;
    }

    private void OnActiveJobsListLoaded(object? sender, RoutedEventArgs e)
        => RefreshDragHandlers();

    // Re-attach whenever the collection changes, but skip while actively dragging
    // so we don't detach the PointerReleased handler before the user lets go.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is HistoryViewModel hvm)
            hvm.ActiveJobs.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (!_dragging) RefreshDragHandlers();
            });
    }

    private void RefreshDragHandlers()
    {
        var panel = ActiveJobsList.ItemsPanelRoot as Panel;
        if (panel == null) return;
        foreach (var child in panel.Children)
        {
            child.RemoveHandler(PointerPressedEvent,  OnCardPressed);
            child.RemoveHandler(PointerMovedEvent,    OnCardMoved);
            child.RemoveHandler(PointerReleasedEvent, OnCardReleased);
            child.RemoveHandler(PointerCaptureLostEvent, OnCaptureLost);
            child.AddHandler(PointerPressedEvent,  OnCardPressed,  RoutingStrategies.Tunnel);
            child.AddHandler(PointerMovedEvent,    OnCardMoved,    RoutingStrategies.Tunnel);
            // Use Direct routing for release and capture-lost so they always fire on the
            // captured element even if a child (e.g. Button) tries to tunnel/bubble them.
            child.AddHandler(PointerReleasedEvent, OnCardReleased, RoutingStrategies.Direct);
            child.AddHandler(PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Direct);
        }
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        var panel = ActiveJobsList.ItemsPanelRoot as Panel;
        if (panel == null) return;
        _dragFromIndex = panel.Children.IndexOf((Visual?)sender as Control ?? (Control)sender!);
        _dragStart = e.GetPosition(panel);
        _dragging  = false;
    }

    private void OnCardMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFromIndex < 0) return;
        var panel = ActiveJobsList.ItemsPanelRoot as Panel;
        if (panel == null) return;
        var pos = e.GetPosition(panel);
        if (!_dragging && Math.Abs(pos.Y - _dragStart.Y) < DragThreshold) return;

        // Safety: if dragging but card was lost (e.g. recycled by collection change), abort.
        if (_dragging && _draggedCard == null) { EndDrag(); return; }

        if (!_dragging)
        {
            // Begin drag: lift the card and disable ALL card transitions so reordering
            // is instant (no snap) — we re-enable on drop for a smooth settle.
            _draggedCard = sender as Control;
            if (_draggedCard != null)
            {
                _savedTransitions = _draggedCard.Transitions;
                _draggedCard.Transitions = null;
                _draggedCard.Classes.Add("dragging");
                _draggedCard.ZIndex = 100;
            }
            // Disable transitions on all sibling cards so they don't animate mid-drag.
            _allSavedTransitions.Clear();
            foreach (var child in panel.Children)
            {
                if (child is Control c && c != _draggedCard)
                {
                    _allSavedTransitions.Add(c.Transitions);
                    c.Transitions = null;
                }
                else _allSavedTransitions.Add(null);
            }
            // Measure row pitch (card height + spacing) from adjacent containers.
            if (panel.Children.Count > 1)
                _rowHeight = Math.Abs(panel.Children[1].Bounds.Y - panel.Children[0].Bounds.Y);
            else if (_draggedCard != null)
                _rowHeight = _draggedCard.Bounds.Height + 10;
            // Capture the pointer so all subsequent move/release events route here even
            // when the cursor leaves the card.
            e.Pointer.Capture((IInputElement)sender!);
            _dragging = true;
            ((InputElement)sender!).Cursor = new Cursor(StandardCursorType.DragMove);
        }

        // Card follows the cursor (relative to its current slot baseline).
        ApplyDragTransform(pos.Y - _dragStart.Y);

        // Determine destination slot by hit-testing child midpoints against the pointer.
        int toIndex = -1;
        for (int i = 0; i < panel.Children.Count; i++)
        {
            var child = panel.Children[i];
            var bounds = child.Bounds;
            if (pos.Y < bounds.Y + bounds.Height / 2 && toIndex < 0)
                toIndex = i;
        }
        if (toIndex < 0) toIndex = panel.Children.Count - 1;
        if (toIndex == _dragFromIndex) return;

        if (DataContext is not HistoryViewModel hvm) return;
        var jobs = hvm.ActiveJobs;

        if (_dragFromIndex >= jobs.Count || toIndex >= jobs.Count) return;

        // FLIP: record current visual Y of every non-dragged card BEFORE the Move.
        _preMoveY.Clear();
        foreach (var child in panel.Children)
        {
            if (child is Control c && c != _draggedCard)
                _preMoveY[c] = child.Bounds.Y;
        }

        // Reorder in the jobs collection
        var shift = (toIndex - _dragFromIndex) * _rowHeight;
        jobs.Move(_dragFromIndex, toIndex);
        _dragFromIndex = panel.Children.IndexOf((Visual?)sender as Control ?? (Control)sender!);
        _dragStart = new Point(_dragStart.X, _dragStart.Y + shift);
        ApplyDragTransform(pos.Y - _dragStart.Y);

        // After Move, apply inverse transforms so non-dragged cards appear at their
        // old visual positions, then let transitions animate them to the new slot.
        foreach (var child in panel.Children)
        {
            if (child is Control c && c != _draggedCard && _preMoveY.TryGetValue(c, out var oldY))
            {
                var newY = child.Bounds.Y;
                var delta = oldY - newY;
                if (Math.Abs(delta) > 0.5)
                {
                    c.RenderTransform = TransformOperations.Parse($"translateY({delta:F2}px)");
                    // Clear transform on next frame so the transition plays
                    Dispatcher.UIThread.Post(() => c.RenderTransform = null);
                }
            }
        }
    }

    private void EndDrag()
    {
        if (_draggedCard != null)
        {
            _draggedCard.RenderTransform = null;
            _draggedCard.Classes.Remove("dragging");
            _draggedCard.ZIndex = 0;
            _draggedCard.Transitions = _savedTransitions;
            _draggedCard.Cursor = Cursor.Default;
        }
        _draggedCard      = null;
        _savedTransitions = null;

        // Re-enable transitions on all sibling cards so the next layout change animates.
        // Always fully reset transient visual state (transform/zindex/opacity/dragging
        // class) on EVERY child so a card can never get stuck offset or invisible if the
        // drag was interrupted (capture lost, collection changed mid-drag, etc.).
        var panel = ActiveJobsList.ItemsPanelRoot as Panel;
        if (panel != null)
        {
            int t = 0;
            foreach (var child in panel.Children)
            {
                if (child is Control c)
                {
                    c.RenderTransform = null;
                    c.ZIndex = 0;
                    c.Opacity = 1;
                    c.Classes.Remove("dragging");
                    c.Cursor = Cursor.Default;
                    if (t < _allSavedTransitions.Count)
                        c.Transitions = _allSavedTransitions[t];
                }
                t++;
            }
        }
        _allSavedTransitions.Clear();
        _preMoveY.Clear();

        _dragging         = false;
        _dragFromIndex    = -1;
    }

    private void OnCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        ((InputElement)sender!).Cursor = Cursor.Default;
        if (_dragging && DataContext is HistoryViewModel hvm && _dragFromIndex >= 0
            && _dragFromIndex < hvm.ActiveJobs.Count)
        {
            var ids = hvm.ActiveJobs.Select(j => j.Job.Id).ToList();
            _ = hvm.PersistActiveJobOrderAsync(ids);
        }
        EndDrag();
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ((InputElement)sender!).Cursor = Cursor.Default;
        EndDrag();
    }
}
