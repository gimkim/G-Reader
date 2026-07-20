namespace CDisplayEx.CSharp;

internal sealed record ThumbnailFolderEntry(
    string Label,
    string Path,
    bool IsParent = false,
    bool IsContainer = false,
    bool IsPdf = false);
