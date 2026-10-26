using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Pikura.Core.Settings;
using System;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Service to apply accessibility settings across the application.
/// </summary>
public class AccessibilityService
{
    private readonly SettingsService _settingsService;
    private double _baseFontSize = 13.0;
    private const double ControlFontSize = 12.0;
    private bool _isHighContrast;

    public AccessibilityService(SettingsService settingsService)
    {
        _settingsService = settingsService;

        // Apply initial settings
        ApplyAccessibilitySettings();

        // Listen for changes — Settings.Changed fires synchronously on whatever thread
        // mutated the settings, but our handlers touch Avalonia application state
        // (Resources, RequestedThemeVariant) which must run on the UI thread.
        // Without this Post() any background-thread Settings.Update would throw and
        // could corrupt unrelated flows (e.g. UpdateCheckService, cancellation).
        _settingsService.Changed += (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                ApplyAccessibilitySettings();
            else
                Dispatcher.UIThread.Post(ApplyAccessibilitySettings);
        };
    }

    private (double, bool, bool, bool)? _applied;

    private void ApplyAccessibilitySettings()
    {
        var settings = _settingsService.Current;
        // Settings.Changed fires on EVERY settings write (tab switches, slider drags…). Re-setting
        // these resources restyles the whole window, so only do it when they actually changed.
        var key = (settings.FontSizeScale, settings.UseLargeFonts, settings.UseHighContrast, settings.ReduceMotion);
        if (_applied == key) return;
        _applied = key;

        ApplyFontScaling(settings.FontSizeScale, settings.UseLargeFonts);
        ApplyHighContrast(settings.UseHighContrast);
        ApplyReducedMotion(settings.ReduceMotion);
    }

    private void ApplyFontScaling(double scale, bool useLargeFonts)
    {
        if (Application.Current == null) return;
        
        // Calculate effective font size
        double effectiveScale = scale;
        if (useLargeFonts)
            effectiveScale *= 1.2;

        // Set global font size resource
        Application.Current.Resources["AccessibilityFontSize"] = _baseFontSize * effectiveScale;
        // Controls (buttons, dropdowns, checkboxes) sit a step below body text so toolbars stay compact.
        Application.Current.Resources["AccessibilityControlFontSize"] = ControlFontSize * effectiveScale;
    }

    /// <summary>
    /// Container/control border-brush resources that views bind to directly via
    /// DynamicResource (e.g. "BorderBrush={DynamicResource Container1BorderBrush}"). Because
    /// almost every Border in the app sets BorderBrush as a local XAML value, the generic
    /// "Style Selector=Border" override in App.axaml never wins against that local value —
    /// so overriding it alone made High Contrast Mode look like it did nothing. Overriding
    /// these resource keys instead reaches every border throughout the app since they're all
    /// already wired up to re-evaluate on DynamicResource changes.
    /// </summary>
    private static readonly string[] HighContrastBorderKeys =
    {
        "Container1BorderBrush", "Container2BorderBrush", "Container3BorderBrush",
        "Container4BorderBrush", "ControlBorderBrush",
    };

    private void ApplyHighContrast(bool enable)
    {
        if (Application.Current == null) return;

        _isHighContrast = enable;
        var currentResources = Application.Current.Resources;

        if (enable)
        {
            // Use a high-visibility border colour that contrasts with the active theme.
            // Do not flip the theme variant — high contrast should keep the user's light/dark/scheduled choice.
            bool isDark = Application.Current.ActualThemeVariant == ThemeVariant.Dark;
            var borderBrush = new SolidColorBrush(isDark ? Colors.White : Colors.Black);
            currentResources["AccessibilityBorder"] = borderBrush;
            foreach (var key in HighContrastBorderKeys)
                currentResources[key] = borderBrush;
        }
        else
        {
            // Restore the user's chosen theme (System/Scheduled are handled by App.ApplyTheme).
            currentResources.Remove("AccessibilityBorder");
            foreach (var key in HighContrastBorderKeys)
                currentResources.Remove(key);
            App.ApplyTheme();
        }
    }

    private void ApplyReducedMotion(bool reduce)
    {
        if (Application.Current == null) return;

        if (reduce)
        {
            // Set animation duration to 0 for reduced motion
            var currentResources = Application.Current.Resources;
            currentResources["AccessibilityAnimationDuration"] = TimeSpan.Zero;
        }
        else
        {
            // Restore default animation durations
            var currentResources = Application.Current.Resources;
            currentResources["AccessibilityAnimationDuration"] = TimeSpan.FromMilliseconds(200);
        }
    }
}
