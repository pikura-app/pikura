using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Helper for managing Windows startup registry entries.
/// </summary>
public static class StartupHelper
{
    private const string RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Pikura";
    // Legacy names that may have been registered by older builds or the installer.
    // We always remove these alongside the canonical name so the user's setting takes effect.
    private static readonly string[] LegacyAppNames = ["PixivUtil2"];

    /// <summary>
    /// Gets whether the app is set to start with Windows.
    /// </summary>
    public static bool IsStartupEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
            if (key == null) return false;
            if (key.GetValue(AppName) != null) return true;
            foreach (var legacy in LegacyAppNames)
            {
                if (key.GetValue(legacy) != null) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets whether the app should start with Windows.
    /// </summary>
    public static void SetStartupEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
            if (key == null) return;

            // Always clear legacy names so disabling actually removes startup,
            // even if a previous installer/build registered under another key.
            foreach (var legacy in LegacyAppNames)
            {
                try { key.DeleteValue(legacy, false); } catch { }
            }

            if (enabled)
            {
                var exePath = GetExecutablePath();
                key.SetValue(AppName, exePath, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set startup: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the startup command with arguments based on startup settings.
    /// </summary>
    public static void UpdateStartupCommand(bool startMinimized, bool startInTray)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
            if (key == null) return;

            // Check if we currently have a startup entry
            var existingValue = key.GetValue(AppName);
            if (existingValue == null) return; // Not set to start with Windows

            var exePath = GetExecutablePath();
            var args = BuildStartupArguments(startMinimized, startInTray);
            var command = args.Length > 0 ? $"\"{exePath}\" {args}" : exePath;

            key.SetValue(AppName, command, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to update startup command: {ex.Message}");
        }
    }

    private static string BuildStartupArguments(bool startMinimized, bool startInTray)
    {
        var args = "";
        if (startInTray)
        {
            args += "--tray ";
        }
        else if (startMinimized)
        {
            args += "--minimized ";
        }
        return args.Trim();
    }

    private static string GetExecutablePath()
    {
        var assembly = Assembly.GetEntryAssembly();
        if (assembly != null)
        {
            return assembly.Location;
        }

        // Fallback for published apps
        var processModule = Process.GetCurrentProcess().MainModule;
        if (processModule != null)
        {
            return processModule.FileName;
        }

        return Path.Combine(AppContext.BaseDirectory, "Pikura.Avalonia.exe");
    }
}

/// <summary>
/// Startup behavior options parsed from command line.
/// </summary>
public class StartupOptions
{
    /// <summary>Start minimized to taskbar.</summary>
    public bool Minimized { get; set; }

    /// <summary>Start minimized to system tray.</summary>
    public bool Tray { get; set; }

    /// <summary>Parse command line arguments.</summary>
    public static StartupOptions Parse(string[] args)
    {
        var options = new StartupOptions();

        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--minimized":
                case "-min":
                    options.Minimized = true;
                    break;

                case "--tray":
                case "-tray":
                    options.Tray = true;
                    break;
            }
        }

        return options;
    }
}
