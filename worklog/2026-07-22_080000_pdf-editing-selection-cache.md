# Worklog — 2026-07-22 08:00:00 ICT (historical reconstruction)

## Scope

PDF page editing and thumbnail selection behavior requested during the cache
and cold-PDF investigations. The chat did not expose a separate commit time for
this requirement, so the timestamp is an ordering marker.

## Requirements recorded

- Pressing Delete in thumbnail mode removes all Ctrl-selected PDF pages; in
  single-page full view it removes the current page; in dual-page view a dialog
  asks which side to remove.
- Every delete shows a confirmation dialog with a second preview of the pages
  to be removed. The cache system remaps/migrates surviving page indexes where
  possible instead of rebuilding a large PDF from zero.
- Ctrl-click selects multiple thumbnail cards. A plain click or arrow key
  collapses to one selection. Copy includes all selected pages, while Enter,
  Explorer, and read actions use the most recently selected page.
- Selection changes invalidate stale full-view warm-up work; a short debounce
  prevents rapid key presses from starting/cancelling work for every transient
  selection.

## Validation

These are product behavior requirements carried into the implementation rules;
confirmation/preview UI behavior remained a manual test.
