namespace CDisplayEx.CSharp;

/// <summary>
/// Calculates stable double-page spread starts from the beginning of a book.
/// Walking the same sequence in both directions avoids losing the page that
/// follows an auto-single landscape page.
/// </summary>
internal static class SpreadNavigation
{
    public static int FindNextStart(int pageCount, int currentIndex,
        bool firstPageSingle, bool autoSingleLandscape,
        IReadOnlyDictionary<int, bool> landscapePages)
    {
        if (pageCount <= 0) return 0;
        currentIndex = Math.Clamp(currentIndex, 0, pageCount - 1);
        var start = 0;
        while (start < pageCount)
        {
            var next = NextStart(pageCount, start, firstPageSingle,
                autoSingleLandscape, landscapePages);
            if (next > currentIndex) return Math.Min(next, pageCount - 1);
            if (next <= start) break;
            start = next;
        }
        return pageCount - 1;
    }

    public static int FindPreviousStart(int pageCount, int currentIndex,
        bool firstPageSingle, bool autoSingleLandscape,
        IReadOnlyDictionary<int, bool> landscapePages)
    {
        if (pageCount <= 0 || currentIndex <= 0) return 0;
        currentIndex = Math.Min(currentIndex, pageCount - 1);
        var previous = 0;
        var start = 0;
        while (start < currentIndex)
        {
            var next = NextStart(pageCount, start, firstPageSingle,
                autoSingleLandscape, landscapePages);
            if (next >= currentIndex) return start;
            previous = start;
            start = next;
        }
        return previous;
    }

    public static int FindLastStart(int pageCount, bool firstPageSingle,
        bool autoSingleLandscape,
        IReadOnlyDictionary<int, bool> landscapePages)
    {
        if (pageCount <= 0) return 0;
        var start = 0;
        while (true)
        {
            var next = NextStart(pageCount, start, firstPageSingle,
                autoSingleLandscape, landscapePages);
            if (next >= pageCount) return start;
            start = next;
        }
    }

    private static int NextStart(int pageCount, int start,
        bool firstPageSingle, bool autoSingleLandscape,
        IReadOnlyDictionary<int, bool> landscapePages)
    {
        var single = firstPageSingle && start == 0;
        if (!single && autoSingleLandscape &&
            landscapePages.TryGetValue(start, out var landscape))
            single = landscape;
        return Math.Min(pageCount, start + (single ? 1 : 2));
    }
}
