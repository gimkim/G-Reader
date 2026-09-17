# Worklog - 2026-08-07 14:33:57 ICT

## Symptom

Opening an image from a folder containing roughly 40,000 files could leave the
application invisible for several minutes when the folder view was ordered by
modified date descending. The delay looked like a startup hang because the
window and any status indication were not available yet.

## Evidence and cause

- The Explorer file association passed `--explorer` to `Program.Main`.
- Before `Application.Run`, startup synchronously enumerated the matching
  Explorer `Folder.Items` collection to preserve Explorer's item order.
- Folder opening then read EXIF orientation for every JPEG before sorting. This
  was unnecessary for modified-date sorting and multiplied the cold-start I/O
  cost for a large directory.
- The existing progress methods only incremented a generation number and did
  not expose status in the bottom bar.

## Changes

- `Program.cs` now creates the WinForms window before any Explorer view-order
  capture or folder enumeration.
- `ExplorerViewOrder.cs` captures the Explorer order on a dedicated STA
  background thread with cancellation, preserving Shell COM compatibility
  without blocking the message pump.
- `AsyncMainForm.cs` starts opening work from `Shown`, shows a marquee progress
  bar and current phase/file count, throttles status updates, and restores the
  normal bottom bar state when work completes or is cancelled.
- `Book.cs` reports cancellable folder/archive listing, metadata, and sorting
  phases. JPEG EXIF orientation is now loaded lazily for pages that are used;
  date-modified folder startup no longer opens all JPEG headers.

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `git diff --check` passed.
- No real 40,000-file Explorer/UI reproduction was run by the agent; manual
  validation remains: launch an image from a very large folder sorted by Date
  Modified descending, confirm the window appears immediately, and confirm the
  count/current filename status remains responsive while listing continues.
