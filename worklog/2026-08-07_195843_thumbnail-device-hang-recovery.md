# Thumbnail device-hang recovery

## Symptom

The thumbnail area stopped updating while the toolbar remained interactive.
The active instance was PID 57740 in thumbnail mode at `L:\tgm\(0archive`.

## Evidence

- Diagnostics recorded `D2DERR_RECREATE_TARGET` (`0x8899000C`) followed by
  `DXGI_ERROR_DEVICE_HUNG` (`0x887A0006`) at 19:51:21.
- The shared D3D device was recreated successfully, but health records then
  showed `fastPending=0` and no further visible work while the UI heartbeat
  continued. This distinguishes a stalled thumbnail recovery from a blocked
  WinForms message pump.
- The folder contains 10,317 archive/PDF containers. A cover can expand into
  four native image/PDF decodes, while the previous outer scheduler admitted
  as many as 64 covers concurrently.
- The process remained responsive but held about 9.1 GB working set and 11.5 GB
  private bytes after rapid full-view/container transitions.

## Changes

- Bound archive/PDF cover generation to 2-8 concurrent outer jobs based on
  logical cores. Codec schedulers still retain their configured concurrency
  inside each cover job.
- Changed browse-preview device-loss replacement so it waits for an overlapping
  stale GPU job to release its in-flight key instead of silently treating the
  missing replacement as complete.
- Added a coalesced visible-preview retry when an old native job cannot retire
  within the bounded wait.
- Added a Direct2D swap-chain watchdog: repeated `DXGI_ERROR_WAS_STILL_DRAWING`
  for at least 750 ms now discards and recreates the thumbnail target, checking
  whether a full shared-device reset is required.
- Direct2D hardware presentation remains enabled. Native GPU thumbnail sources
  are retired only after the driver has reported an actual device loss, as in
  this reproduction.

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `git diff --check` passed (only existing LF/CRLF notices were emitted).
- Publishing to `release` was attempted but could not replace the executable
  because five Fast Reader/Viewer processes were still running from that path,
  including PID 57740. No process was terminated automatically.

## Remaining manual test

After closing all running instances, publish to `release` and reproduce rapid
archive/full-view transitions followed by returning to `L:\tgm\(0archive`.
Confirm that visible covers resume after a simulated/real device reset and that
scrolling continues while background contact sheets are generated.
