using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Pikura.Avalonia.Localization;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Service for native file picking operations.
/// </summary>
public class FilePickerService
{
    private readonly DialogService _dialogService;
    private Window? _ownerWindow;

    public FilePickerService(DialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public void Initialize(Window ownerWindow)
    {
        _ownerWindow = ownerWindow;
    }

    /// <summary>
    /// Opens a file picker dialog to select a text file.
    /// </summary>
    public async Task<string?> PickTextFileAsync(string title = "Select a file")
    {
        if (_ownerWindow == null)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_FilePickerNotInitialized"]);
            return null;
        }

        try
        {
            var storage = _ownerWindow.StorageProvider;
            var file = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Text files") { Patterns = new[] { "*.txt" } },
                    new("All files") { Patterns = new[] { "*" } }
                }
            });

            return file?.FirstOrDefault()?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Error"], $"Failed to open file picker: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Opens a folder picker dialog.
    /// </summary>
    public async Task<string?> PickFolderAsync(string title = "Select a folder")
    {
        if (_ownerWindow == null)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_FilePickerNotInitialized"]);
            return null;
        }

        try
        {
            var storage = _ownerWindow.StorageProvider;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });

            return folders?.FirstOrDefault()?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Error"], $"Failed to open folder picker: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads all text from a file picked by the user.
    /// </summary>
    public async Task<string?> PickAndReadTextFileAsync(string title = "Select a file")
    {
        var path = await PickTextFileAsync(title);
        if (path == null) return null;

        try
        {
            return await System.IO.File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Error"], $"Failed to read file: {ex.Message}");
            return null;
        }
    }
}
