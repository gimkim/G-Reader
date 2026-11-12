# Thumbnail scroll progress restart

## Symptom

Scrolling a very large thumbnail view caused the preview status to restart at
`1/65783` after every scroll, making it look as though all preview work and
cache state had been discarded.

## Evidence and cause

- `ThumbnailGridView` queued `ThumbnailRefreshRequested` when smooth scrolling
  settled, when a running smooth scroll was interrupted by a click, and after
  every direct scrollbar position change.
- `AsyncMainForm` handles that event by calling
  `LoadThumbnailsProgressivelyAsync`, which cancels the existing full-book pass,
  rebuilds its visible-first order, and initializes that pass's progress counter
  from zero.
- Visible viewport refresh is already handled independently through
  `VisiblePreviewRefreshRequested`, so a full progressive restart is not needed
  for scrolling.

## Change

- Removed full thumbnail refresh scheduling from all scroll-only paths.
- Kept full refresh scheduling when the actual thumbnail render target size
  changes, because that requires previews at different dimensions.
- The active full-book pass now continues across scrolling while the visible
  refresh path fills newly exposed cards immediately.

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `git diff --check` passed (only existing line-ending notices were emitted).

## Remaining manual UI check

Open a folder/archive with tens of thousands of items, let progressive previews
start, then repeatedly wheel-scroll and drag the scrollbar. Confirm that newly
visible cards continue loading and the overall progress value does not restart
at 1 after each scroll.

## Follow-up: selection reprioritization

The first correction removed scroll-triggered full passes, but selection changes
still intentionally cancel and rebuild the visible-first ordering. That new pass
also initialized its local counter at zero, so selecting another card could still
show `1/65738` even though cached previews remained valid.

- Before starting a reordered pass, snapshot completed fast/full stages from the
  per-item thumbnail caches on a cancellable background task.
- Initialize progress from those completed stages and enqueue only stages which
  are still missing.
- A full preview counts as satisfying both the fast and full stages.
- This preserves priority promotion without discarding completed work or scanning
  the large collection on the UI thread.

Follow-up validation:

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `dotnet publish -c Release -o release --no-restore` passed.
- Published `release\Fast Reader Viewer.exe` is version `1.9.3.0`, written at
  2026-08-17 13:03 local time.
- `git diff --check` passed apart from existing line-ending notices.

## Follow-up: preserve the active pass across selection changes

Cache-aware counter restoration still created a distinct pass whenever the
selection changed. The progress value was more accurate, but the UI correctly
still exposed that a new fast-preview pass had started.

- Selection no longer raises `ThumbnailInteractionStarted`, so it does not
  cancel the active full-book thumbnail cancellation source.
- The debounced selection-priority event now requests only the bounded visible
  preview overlay instead of calling `LoadThumbnailsProgressivelyAsync`.
- The original cumulative full-book pass therefore continues uninterrupted.
  Selected-page full-view warming remains handled by its existing independent
  debounced warm-cache scheduler.
- Filter changes and real render-target size changes retain their full-refresh
  behavior because their required work set or dimensions actually change.

Second follow-up validation:

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `dotnet publish -c Release -o release --no-restore` passed.
- The normal release executable was rewritten at 2026-08-17 13:06 local time.
- Static event inspection confirms only filtering raises
  `ThumbnailInteractionStarted`, and only render-size changes call
  `QueueThumbnailRefresh`.
