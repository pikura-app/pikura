using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Pikura.Avalonia.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Views.Dialogs;

public partial class CrashReportDialog : Window
{
    private readonly CrashInfo? _crashInfo;
    private readonly CrashReportService _crashService;

    public CrashReportDialog()
    {
        InitializeComponent();
        _crashService = new CrashReportService();
    }

    public CrashReportDialog(CrashInfo crashInfo) : this()
    {
        _crashInfo = crashInfo;
        LoadCrashInfo();
    }

    private void LoadCrashInfo()
    {
        if (_crashInfo == null) return;

        CrashTimeText.Text = _crashInfo.FormattedTimestamp;
        CrashTypeText.Text = _crashInfo.ExceptionType;
        CrashMessageText.Text = _crashInfo.Message;
        LogPathTextBox.Text = _crashInfo.LogFilePath;
    }

    private void OnViewLogClick(object? sender, RoutedEventArgs e)
    {
        if (_crashInfo == null || !File.Exists(_crashInfo.LogFilePath))
        {
            // Show error - file not found
            return;
        }

        try
        {
            // Open with default text editor
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{_crashInfo.LogFilePath}\"",
                    UseShellExecute = false
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-e \"{_crashInfo.LogFilePath}\"",
                    UseShellExecute = false
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = $"\"{_crashInfo.LogFilePath}\"",
                    UseShellExecute = false
                });
            }
        }
        catch (Exception)
        {
            // Fallback: open folder and select file
            OpenFolderAndSelectFile(_crashInfo.LogFilePath);
        }
    }

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        _crashService.OpenCrashLogsFolder();
    }

    private void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        var path = _crashInfo?.LogFilePath ?? _crashService.CrashLogsFolder;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        var dt = new global::Avalonia.Input.DataTransfer();
        dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(path));
        _ = clipboard.SetDataAsync(dt);

        // Visual feedback
        var originalText = CopyPathButton.Content?.ToString();
        CopyPathButton.Content = "Copied!";
        _ = global::System.Threading.Tasks.Task.Run(async () =>
        {
            await global::System.Threading.Tasks.Task.Delay(1500);
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                CopyPathButton.Content = originalText;
            });
        });
    }

    private void OnCopyContentsClick(object? sender, RoutedEventArgs e)
    {
        if (_crashInfo == null || !File.Exists(_crashInfo.LogFilePath)) return;

        try
        {
            var contents = File.ReadAllText(_crashInfo.LogFilePath);

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;

            var dt = new global::Avalonia.Input.DataTransfer();
            dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(contents));
            _ = clipboard.SetDataAsync(dt);

            // Visual feedback
            var originalText = CopyContentsButton.Content?.ToString();
            CopyContentsButton.Content = "Copied!";
            _ = global::System.Threading.Tasks.Task.Run(async () =>
            {
                await global::System.Threading.Tasks.Task.Delay(1500);
                await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CopyContentsButton.Content = originalText;
                });
            });
        }
        catch { /* ignore read errors */ }
    }

    private void OnDismissClick(object? sender, RoutedEventArgs e)
    {
        _crashService.ClearCrashFlag();
        // Shutdown the application after closing dialog
        _ = Task.Run(async () =>
        {
            await Task.Delay(100); // Small delay to let dialog close
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            });
        });
        Close();
    }

    private static void OpenFolderAndSelectFile(string filePath)
    {
        var folderPath = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(folderPath)) return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = false
                });
            }
            else
            {
                // Just open the folder on other platforms
                Process.Start(new ProcessStartInfo
                {
                    FileName = folderPath,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }
}
