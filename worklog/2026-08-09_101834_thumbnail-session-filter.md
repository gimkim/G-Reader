# Thumbnail session filter

## Symptom / request

The thumbnail view had no way to narrow the currently displayed folders,
containers, and pages by name. A filter also needed to survive navigation into
an archive and back to the containing folder, while remaining session-only and
clearing when the application exits.

## Changes

- Added a compact search box and clear button to the thumbnail controls, with a
  short debounce and `Ctrl+F` focus shortcut.
- Store filter text in memory per normalized book/folder source path. Restoring
  a previous path restores its filter; no filter is written to user settings.
- Keep the parent-folder card visible while filtering and show an in-page
  no-match message when no supported child item matches.
- Added stable display-to-source mappings for folder entries and pages so
  filtering does not change the actual page indexes used by open, copy,
  multi-select, delete, thumbnail caches, or Direct2D rendering.
- Thumbnail generation now schedules only visible filtered items and reports
  progress against that filtered work set while retaining already-created
  source-indexed previews for later reuse.
- Synchronized background completion callbacks with filter mapping replacement
  to prevent stale source-to-display invalidation races.

## Files

- `AsyncMainForm.cs`
- `ThumbnailGridView.cs`
- `ThumbnailGridView.Direct2D.cs`

## Validation

- `dotnet build -c Release --no-restore` — passed with 0 warnings and 0 errors.
- `git diff --check` — passed (line-ending conversion notices only).

## Remaining manual UI validation

- Type a filter in a large folder, enter a matching archive, navigate back, and
  confirm the outer folder filter is restored.
- Confirm clearing the filter restores all cards and existing previews without
  index mismatches, then restart the application and confirm the filter is
  empty.
- Exercise Ctrl multi-select, copy, delete, and page activation while a PDF or
  archive filter is active.
