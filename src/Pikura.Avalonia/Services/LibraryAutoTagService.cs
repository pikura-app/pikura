using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pikura.Core.Services.Library;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.Services;

/// <summary>Background auto-tagger for the local library. Triggers: startup (+60 s),
/// library-scan completion, setting enabled, the manual "Tag untagged" button. One pass at a
/// time; requests during a pass queue exactly one more. Waits (between images) while the user
/// has paused it or Hoshi is generating, so the two never compete for the CPU/GPU.</summary>
public partial class LibraryAutoTagService : ObservableObject, IDisposable
{
    private readonly LibraryIndexService _index;
    private readonly SettingsService _settings;
    private readonly Func<bool> _isHoshiGenerating;
    private readonly ILogger<LibraryAutoTagService> _logger;
    private readonly object _lock = new();
    private bool _runRequested;
    private bool _requestedManually;
    private Task? _worker;
    private CancellationTokenSource _cts = new();
    private bool _disposed;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _done;
    [ObservableProperty] private int _total;
    [ObservableProperty] private bool _isPaused;

    /// <summary>Raised (on the UI thread) after each completed pass.</summary>
    public event EventHandler? RunCompleted;

    public LibraryAutoTagService(LibraryIndexService index, SettingsService settings,
        Func<bool> isHoshiGenerating, ILogger<LibraryAutoTagService> logger)
    {
        _index = index; _settings = settings; _isHoshiGenerating = isHoshiGenerating; _logger = logger;
        _index.ScanCompleted += (_, _) => RequestRun();
        // React only when the auto-tag toggle itself flips — Changed fires for every setting.
        var wasOn = _settings.Current.LibraryAutoTag;
        _settings.Changed += (_, _) =>
        {
            var on = _settings.Current.LibraryAutoTag;
            if (on == wasOn) return;
            wasOn = on;
            if (on) RequestRun();
            else if (!_requestedManually) Cancel();
        };
    }

    /// <summary>Starts the delayed startup pass.</summary>
    public void Start() => _ = Task.Delay(TimeSpan.FromSeconds(60), _cts.Token)
        .ContinueWith(t => { if (!t.IsCanceled) RequestRun(); }, TaskScheduler.Default);

    /// <summary>Queue a pass. <paramref name="manual"/> runs even when auto-tagging is off.</summary>
    public void RequestRun(bool manual = false)
    {
        if (_disposed || !CanRun(manual)) return;
        lock (_lock)
        {
            _runRequested = true;
            _requestedManually |= manual;
            if (_cts.IsCancellationRequested) { _cts.Dispose(); _cts = new CancellationTokenSource(); }
            _worker ??= Task.Run(WorkerLoopAsync);
        }
    }

    private bool CanRun(bool manual)
    {
        var s = _settings.Current;
        return (manual || (s.LibraryAutoTag && s.HoshiUseAnimeTagger)) && _index.IsTaggerModelInstalled(out _);
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            CancellationToken ct;
            lock (_lock)
            {
                if (!_runRequested || _disposed) { _worker = null; _requestedManually = false; return; }
                _runRequested = false;
                ct = _cts.Token;
            }
            await Dispatcher.UIThread.InvokeAsync(() => { IsRunning = true; Done = 0; Total = 0; });
            var completed = false;
            try
            {
                _logger.LogInformation("Library auto-tag pass starting");
                await _index.TagUntaggedAsync(
                    new SyncProgress(p => Dispatcher.UIThread.Post(() => { Done = p.Done; Total = p.Total; })),
                    ct, WaitWhilePausedAsync, writeSidecars: false);
                completed = true;
                _logger.LogInformation("Library auto-tag pass finished");
            }
            catch (OperationCanceledException) { _logger.LogInformation("Library auto-tag pass cancelled"); }
            catch (Exception ex) { _logger.LogWarning(ex, "Library auto-tag pass failed"); }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsRunning = false;
                if (completed) RunCompleted?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    private async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        while (IsPaused || _isHoshiGenerating())
            await Task.Delay(1000, ct);
    }

    public void TogglePause() => IsPaused = !IsPaused;

    /// <summary>Cancels any in-flight pass and drops queued requests.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _runRequested = false;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }

    /// <summary>IProgress that invokes inline (Progress&lt;T&gt; would post to the thread pool
    /// and could deliver updates out of order).</summary>
    private sealed class SyncProgress(Action<LibraryScanProgress> report) : IProgress<LibraryScanProgress>
    {
        public void Report(LibraryScanProgress value) => report(value);
    }
}
