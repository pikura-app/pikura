using System;

namespace Pikura.Avalonia.Views.Library;

/// <summary>Scroll position of the Library browse grid, in pixels.</summary>
internal readonly record struct LibraryScrollMetrics(double OffsetY, double ViewportHeight, double ExtentHeight)
{
    public double Remaining => ExtentHeight - OffsetY - ViewportHeight;
    public bool IsMeasured => ViewportHeight > 0 && ExtentHeight > 0;
    public bool IsScrollable => ExtentHeight > ViewportHeight;
}

/// <summary>What the view model reports about paging when the grid scrolls or changes size.</summary>
internal readonly record struct LibraryPagingState(bool CanLoadMore, bool IsLoadingPage, int ItemCount);

/// <summary>
/// Decides when the Library browse grid requests its next page. It has no Avalonia types, so the
/// scroll handler's guards can be tested with the event sequences a real ScrollViewer produces.
/// </summary>
internal sealed class LibraryScrollPager
{
    /// <summary>Distance from the bottom, in pixels, at which the next page is requested.</summary>
    internal const double LoadThreshold = 300;

    // Extent and item count when a page was last requested. Until layout reflects the appended
    // items, the old extent still reads "at the bottom" and would request yet another page.
    private double _extentAtRequest = -1;
    private int _itemCountAtRequest = -1;

    /// <summary>A page was requested and layout hasn't caught up with it yet.</summary>
    internal bool IsAwaitingLayout => _itemCountAtRequest >= 0;

    /// <summary>ScrollChanged: true when the next page should be requested now.</summary>
    internal bool OnScrollChanged(LibraryScrollMetrics metrics, LibraryPagingState paging,
        double offsetDeltaY, double viewportDeltaY, double extentDeltaY)
    {
        ReleaseWhenSettled(metrics, paging);
        if (!paging.CanLoadMore || IsAwaitingLayout) return false;
        if (!ShouldLoadMoreOnScroll(metrics.OffsetY, metrics.ViewportHeight, metrics.ExtentHeight,
                offsetDeltaY, viewportDeltaY, extentDeltaY))
            return false;
        Request(metrics, paging);
        return true;
    }

    /// <summary>Paging was held (busy work, a delete) and may continue. No scroll event comes for a
    /// grid that already sits at the bottom, or for one too short to scroll at all.</summary>
    internal bool OnPagingUnblocked(LibraryScrollMetrics metrics, LibraryPagingState paging)
    {
        ReleaseWhenSettled(metrics, paging);
        if (!paging.CanLoadMore || IsAwaitingLayout) return false;
        if (!IsParkedAtEnd(metrics)) return false;
        Request(metrics, paging);
        return true;
    }

    /// <summary>
    /// Scroll or layout change: request the next page by remaining distance to the bottom. Content
    /// growth (<c>ExtentDelta.Y &gt; 0</c>) isn't required, since an ordinary scroll keeps the extent.
    /// An unmeasured viewport, scrolling upward, and scrollable content still at the top don't load.
    /// </summary>
    internal static bool ShouldLoadMoreOnScroll(
        double offsetY,
        double viewportHeight,
        double extentHeight,
        double offsetDeltaY,
        double viewportDeltaY,
        double extentDeltaY)
    {
        if (viewportHeight <= 0 || extentHeight <= 0) return false;
        if (extentHeight - offsetY - viewportHeight > LoadThreshold) return false;

        if (extentHeight > viewportHeight)
            return offsetDeltaY > 0 || (offsetY > 0 && (viewportDeltaY > 0 || extentDeltaY < 0));

        // Shorter than the viewport: nothing can scroll, so only another page helps. The offset
        // delta is negative here when the content just shrank and the offset was clamped to 0.
        return true;
    }

    /// <summary>Without a scroll event: the grid doesn't fill the viewport, or the user already
    /// scrolled to the bottom. Scrollable content still at the top doesn't count.</summary>
    internal static bool IsParkedAtEnd(LibraryScrollMetrics metrics) =>
        metrics.IsMeasured
        && metrics.Remaining <= LoadThreshold
        && (!metrics.IsScrollable || metrics.OffsetY > 0);

    private void Request(LibraryScrollMetrics metrics, LibraryPagingState paging)
    {
        _extentAtRequest = metrics.ExtentHeight;
        _itemCountAtRequest = paging.ItemCount;
    }

    private void ReleaseWhenSettled(LibraryScrollMetrics metrics, LibraryPagingState paging)
    {
        if (!IsAwaitingLayout) return;
        // Layout moved the extent (the page landed, or the results were replaced), or the request
        // ended without adding anything (it failed, was superseded, or nothing was left).
        if (Math.Abs(metrics.ExtentHeight - _extentAtRequest) >= 1.0
            || (!paging.IsLoadingPage && paging.ItemCount <= _itemCountAtRequest))
        {
            _extentAtRequest = -1;
            _itemCountAtRequest = -1;
        }
    }
}
