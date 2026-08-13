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

    private void ApplyAccessibilitySettings()
    {
        var settings = _settingsService.Current;
        
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
    }

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
            currentResources["AccessibilityBorder"] = new SolidColorBrush(isDark ? Colors.White : Colors.Black);
        }
        else
        {
            // Restore the user's chosen theme (System/Scheduled are handled by App.ApplyTheme).
            currentResources.Remove("AccessibilityBorder");
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
