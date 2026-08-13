using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views.Artwork;

public partial class CollageFullscreenWindow : Window, INotifyPropertyChanged
{
    private double _zoom = 1.0;
    private const double ZoomStep = 1.15;
    private const double MinZoom = 0.25;
    private const double MaxZoom = 5.0;

    public double Zoom
    {
        get => _zoom;
        private set
        {
            if (Math.Abs(_zoom - value) < 0.001) return;
            _zoom = Math.Clamp(value, MinZoom, MaxZoom);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ZoomedColumnWidth));
            OnPropertyChanged(nameof(ZoomedItemSpacing));
        }
    }

    public double ZoomedColumnWidth => 360 * _zoom;
    public double ZoomedItemSpacing => 16 * _zoom;

    private PropertyChangedEventHandler? _propertyChanged;
    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add => _propertyChanged += value;
        remove => _propertyChanged -= value;
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => _propertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public CollageFullscreenWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public CollageFullscreenWindow(IEnumerable<ArtworkCardViewModel> items)
    {
        InitializeComponent();
        DataContext = this;
        if (CollageItemsControl != null)
            CollageItemsControl.ItemsSource = items.ToList();

        KeyDown += OnKeyDown;
        PointerWheelChanged += OnPointerWheelChanged;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;

        Zoom = e.Delta.Y > 0
            ? Zoom * ZoomStep
            : Zoom / ZoomStep;
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Add:
            case Key.OemPlus:
                Zoom *= ZoomStep;
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                Zoom /= ZoomStep;
                e.Handled = true;
                break;
            case Key.R:
                Zoom = 1.0;
                e.Handled = true;
                break;
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
