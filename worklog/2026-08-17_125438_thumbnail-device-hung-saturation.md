# Thumbnail device-hung saturation and stalled grid recovery

## Symptom

The thumbnail area stopped updating while the menu and application heartbeat
remained responsive. The active workload was a 32,689-image folder opened from
Explorer with cold/background thumbnail generation running.

## Evidence

- Active process `50404` remained responsive and the diagnostics heartbeat was
  approximately 141-235 ms, so the WinForms message pump was not hung.
- Immediately after entering thumbnail mode, Direct2D `EndDraw` returned
  `D2DERR_RECREATE_TARGET`; the shared D3D device removal reason was
  `DXGI_ERROR_DEVICE_HUNG` (`0x887A0006`).
- The configured fast lane was 28 concurrent jobs with 16 nvJPEG workers and a
  12-image GPU batch. The process reached roughly 227-257 threads.
- A two-second live sample consumed about 24 CPU-seconds and grew private bytes
  by about 1.27 GB. Working set later exceeded 5 GB while the thumbnail grid
  was still generating work.

## Root cause

Staged thumbnail decoding used the foreground, non-blocking nvJPEG admission
path. With a large global fast-preview setting, many full-resolution nvJPEG/NPP
jobs ran concurrently; rejected GPU attempts also fell through to CPU decode.
The resulting adapter saturation and retained per-worker CUDA/pinned buffers
starved Direct2D presentation long enough for Windows TDR to remove the device.
The grid recovery recreated its target, but obsolete decode work kept loading
the adapter while the replacement frame was being established.

## Changes

- Added a dedicated staged-thumbnail GPU lane capped at four concurrent jobs.
- Staged thumbnails still use nvJPEG/NPP GPU decode and resize, but now use the
  configured background gate, batch pacing, and VRAM admission.
- Waiting staged jobs no longer immediately spill into a large parallel CPU
  fallback merely because all GPU workers are busy.
- Direct2D device-loss recovery now cancels the obsolete thumbnail generation
  before retiring/recreating the shared device and requesting a fresh pass.
- Hardware Direct2D presentation remains enabled. Session retirement of unsafe
  native CUDA-D3D source sharing does not disable staged nvJPEG/NPP compute.

## Files

- `EncodedJpegRenderer.cs`
- `NvJpegNativeDecoder.cs`
- `ThumbnailGridView.Direct2D.cs`

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `git diff --check` passed; only repository LF-to-CRLF notices were emitted.

## Remaining manual UI validation

- Restart with the new binary, clear thumbnail cache, open the same 32,689-image
  folder, enter thumbnail mode, and scroll aggressively.
- Confirm visible thumbnails continue updating, memory growth is bounded, and
  diagnostics contain no new `DXGI_ERROR_DEVICE_HUNG` recovery cycle.
