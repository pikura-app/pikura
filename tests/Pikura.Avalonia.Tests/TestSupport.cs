using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Pikura.Core.Data;

namespace Pikura.Avalonia.Tests;

/// <summary>
/// Runs async test code on one thread, the way view models run on the UI thread: continuations
/// queue up and run in order instead of racing each other on the thread pool.
/// </summary>
internal sealed class SingleThreadContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _queue.Add((d, state)); }
        catch (InvalidOperationException) { /* the test has finished; drop leftover background work */ }
    }

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("Send would block the only thread.");

    public static void Run(Func<Task> test)
    {
        var previous = Current;
        var context = new SingleThreadContext();
        SetSynchronizationContext(context);
        try
        {
            var task = test();
            task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
                callback(state);
            task.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); }
    }
}

/// <summary>Library repository whose searches can be counted and held in flight.</summary>
internal sealed class GatedLibraryRepository(string dbPath)
    : LibraryRepository(dbPath, NullLogger<LibraryRepository>.Instance)
{
    private int _searches;
    private int _firstPageSearches;

    public int Searches => Volatile.Read(ref _searches);
    public int FirstPageSearches => Volatile.Read(ref _firstPageSearches);

    /// <summary>When set, each search waits for the task this returns before it queries.</summary>
    public Func<LibraryQuery, Task>? Gate { get; set; }

    public override async Task<(IReadOnlyList<LibraryFile> Items, long Total)> SearchAsync(
        LibraryQuery q, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _searches);
        if (q.Offset == 0) Interlocked.Increment(ref _firstPageSearches);
        if (Gate is { } gate) await gate(q);
        return await base.SearchAsync(q, ct);
    }
}
