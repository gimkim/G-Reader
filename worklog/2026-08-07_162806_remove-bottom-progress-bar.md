# Worklog - 2026-08-07 16:28:06 ICT

## Symptom

The newly visible right-side bottom progress bar was too wide, appeared and
disappeared as nested operations started/finished, and looked distracting.

## Changes

- Removed the `ProgressBar` control from the bottom panel.
- Removed the progress lifecycle code that forced the bottom panel visible or
  hid it again for each operation.
- Retained the throttled status text in the existing load-status label, so
  listing phases and current filenames remain available when the bottom status
  area is enabled by the user.

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `dotnet publish -c Release -o release --no-restore` passed.
- Updated `release\Fast Reader Viewer.exe` timestamp: 2026-08-07 16:27:46.
- No UI automation was run; visual confirmation remains a manual test.
