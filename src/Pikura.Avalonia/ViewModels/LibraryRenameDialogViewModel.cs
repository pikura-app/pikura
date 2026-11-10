using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Localization;
using Pikura.Core.Services.Library;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

/// <summary>One row of the rename preview.</summary>
public sealed class LibraryRenameRow
{
    public required string OldName { get; init; }
    public required string NewName { get; init; }
    public required string Note { get; init; }
    public required RenameStatus Status { get; init; }
    public bool IsRename => Status == RenameStatus.Rename;
    public bool IsSkipped => Status == RenameStatus.Skipped;
    public bool IsUnchanged => Status == RenameStatus.Unchanged;
}

/// <summary>
/// Rename dialog: folder + filename templates (prefilled from Settings, editable for this run),
/// a live preview computed off the UI thread, and the plan handed back to the caller on Rename.
/// </summary>
public partial class LibraryRenameDialogViewModel : ObservableObject
{
    private const int MaxPreviewRows = 400;
    private readonly IReadOnlyList<RenameFileInfo> _files;
    private readonly SettingsService _settings;
    private CancellationTokenSource? _previewCts;

    [ObservableProperty] private string _folderTemplate;
    [ObservableProperty] private string _filenameTemplate;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _moreRowsNote;
    [ObservableProperty] private bool _isComputing;
    [ObservableProperty] private IReadOnlyList<RenamePlanItem> _plan = [];

    public ObservableCollection<LibraryRenameRow> Rows { get; } = [];
    public bool CanRename => !IsComputing && Plan.Any(p => p.Status == RenameStatus.Rename);
    public string Header => string.Format(LocalizationService.Instance["Library_RenameHeader"], _files.Count);

    public IReadOnlyList<string> FolderTokens { get; } =
        SettingsViewModel.FolderTokens.Keys.Concat(["%character%", "%series%"]).Distinct().ToList();
    public IReadOnlyList<string> FilenameTokens { get; } =
        SettingsViewModel.FilenameTokens.Keys.Concat(["%character%", "%series%"]).Distinct().ToList();

    public LibraryRenameDialogViewModel(IReadOnlyList<RenameFileInfo> files, SettingsService settings)
    {
        _files = files;
        _settings = settings;
        _folderTemplate = settings.Current.FolderTemplate;
        _filenameTemplate = settings.Current.FilenameTemplate;
        _ = RebuildAsync(0);
    }

    partial void OnFolderTemplateChanged(string value) => _ = RebuildAsync(250);
    partial void OnFilenameTemplateChanged(string value) => _ = RebuildAsync(250);
    partial void OnIsComputingChanged(bool value) => OnPropertyChanged(nameof(CanRename));
    partial void OnPlanChanged(IReadOnlyList<RenamePlanItem> value) => OnPropertyChanged(nameof(CanRename));

    [RelayCommand]
    private void ResetToSettings()
    {
        FolderTemplate = _settings.Current.FolderTemplate;
        FilenameTemplate = _settings.Current.FilenameTemplate;
    }

    private async Task RebuildAsync(int delayMs)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        IsComputing = true;
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cts.Token);
            var s = _settings.Current;
            var options = new RenameOptions(FolderTemplate ?? "", FilenameTemplate ?? "", s.DateFormat, s.SeparateR18Folder);
            var plan = await Task.Run(() => LibraryRenameService.Plan(_files, options), cts.Token);
            if (cts.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() => Show(plan));
        }
        catch (OperationCanceledException) { }
        finally { if (_previewCts == cts) IsComputing = false; }
    }

    private void Show(IReadOnlyList<RenamePlanItem> plan)
    {
        var loc = LocalizationService.Instance;
        Plan = plan;
        Rows.Clear();
        var roots = _files.ToDictionary(f => f.File.Id, f => f.File.Root);
        string Rel(long id, string path) => roots.TryGetValue(id, out var r) ? System.IO.Path.GetRelativePath(r, path) : path;
        // Renames first, then skipped, then unchanged — the interesting rows on top.
        foreach (var p in plan.OrderBy(p => p.Status switch { RenameStatus.Rename => 0, RenameStatus.Skipped => 1, _ => 2 })
                     .Take(MaxPreviewRows))
        {
            Rows.Add(new LibraryRenameRow
            {
                OldName = Rel(p.FileId, p.OldPath),
                NewName = p.Status == RenameStatus.Rename ? Rel(p.FileId, p.NewPath) : "",
                Status = p.Status,
                Note = p.Status switch
                {
                    RenameStatus.Skipped when p.OutsideLibrary => loc["Library_RenameSkippedOutside"],
                    RenameStatus.Skipped => string.Format(loc["Library_RenameSkippedMissing"], p.MissingTokens),
                    RenameStatus.Unchanged => loc["Library_RenameUnchanged"],
                    _ => "",
                },
            });
        }
        MoreRowsNote = plan.Count > MaxPreviewRows ? string.Format(loc["Library_RenameMoreRows"], plan.Count - MaxPreviewRows) : null;
        Summary = string.Format(loc["Library_RenameSummary"],
            plan.Count(p => p.Status == RenameStatus.Rename),
            plan.Count(p => p.Status == RenameStatus.Skipped),
            plan.Count(p => p.Status == RenameStatus.Unchanged));
    }
}
