using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pikura.Core.Models;
using System.Collections.Generic;

namespace Pikura.Avalonia.Views.Dialogs;

public partial class LoadPresetDialog : Window
{
    private readonly List<DownloadPreset> _presets = new();
    public DownloadPreset? SelectedPreset { get; private set; }
    public bool ShouldDelete { get; private set; }

    public LoadPresetDialog()
    {
        InitializeComponent();
    }

    public LoadPresetDialog(IEnumerable<DownloadPreset> presets)
    {
        InitializeComponent();
        _presets.AddRange(presets);
        PresetsList.ItemsSource = _presets;
    }

    private void PresetItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is DownloadPreset preset)
        {
            SelectedPreset = preset;
        }
    }

    private void LoadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is DownloadPreset preset)
        {
            SelectedPreset = preset;
            Close(true);
        }
    }

    private void DeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is DownloadPreset preset)
        {
            SelectedPreset = preset;
            ShouldDelete = true;
            Close(true);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
