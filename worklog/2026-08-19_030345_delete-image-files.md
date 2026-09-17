# Delete image files from thumbnail and full view

## Request

Allow the Delete key to remove image files from both Thumbnail and Full View,
with a confirmation dialog before any filesystem change.

## Behavior

- In Thumbnail view, Delete targets every Ctrl-selected image page.
- In Full View, Delete targets the current image page only.
- Existing PDF page deletion and its preview dialog remain unchanged.
- Images inside archives are not rewritten or removed; the app reports that
  archive entries cannot be deleted directly.
- Confirmation lists the selected filenames, defaults to **No**, and moves
  confirmed files to the Windows Recycle Bin rather than permanently deleting
  them.
- File length and modified time are checked again after confirmation so a file
  changed by another program is not deleted accidentally.
- Deletion runs away from the UI thread, retries briefly for decoder handles,
  reports partial failures, and reloads the folder at the nearest remaining
  page.

## Cache and state handling

- Active decode/thumbnail work is cancelled before filesystem changes.
- Completed page-cache and thumbnail-cache entries are remapped to the new page
  indexes, including partial-success deletion batches.
- Persistent per-file preview identities remain reusable for unchanged files.
- The containing folder's browse-cover identity is invalidated so navigating to
  the parent does not briefly reuse a cover containing a deleted image.

## Files

- `AsyncMainForm.cs`
- `PersistentPreviewCache.cs`

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors
  during implementation.
- `dotnet publish -c Release -o release --no-restore` succeeded and refreshed
  the normal release output.
- `git diff --check` passed; only the repository's existing CRLF conversion
  notices were emitted.

## Remaining manual UI test

- Test one-file deletion in Full View.
- Test Ctrl multi-selection followed by Delete in Thumbnail view.
- Test deleting the final image in a folder, a read-only file, a partially
  locked batch, Cancel/No confirmation, and Delete while viewing an archive or
  PDF.
- No destructive UI test was run automatically against the user's image
  library.
