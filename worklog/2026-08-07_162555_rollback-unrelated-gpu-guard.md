# Worklog - 2026-08-07 16:25:55 ICT

## Scope

Rolled back the extra GPU-startup guard added during log triage because the
folder-size-dependent blocker was already identified as stale synchronous
Explorer order enumeration. The user requested keeping this fix narrowly
scoped.

## Changes

- Removed the temporary `GpuInteropDevice` asynchronous initialization API and
  restored its original lazy GPU initialization behavior.
- Restored the original Direct2D/thumbnail paint initialization path and
  removed the temporary shared-GPU warm-up callback.
- Restored the original nvJPEG initialization sequence.
- Kept the actual large-folder fix: Explorer order capture occurs after the
  window is shown on a cancellable STA worker; folder metadata/EXIF work and
  visible listing progress remain asynchronous.

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `dotnet publish -c Release -o release --no-restore` passed.
- Updated `release\Fast Reader Viewer.exe` timestamp: 2026-08-07 16:25:44.
- `git diff --check` passed (existing LF/CRLF notices only).
