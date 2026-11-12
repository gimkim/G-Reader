# Fix Manhwa deep-seek slowdown, device loss, hang and crash

## Symptom and evidence

- Reading further into Manhwa became slower, stopped showing images and crashed.
  The user clarified that a fresh launch followed by seeking directly to late
  pages also reproduces it, so accumulated navigation alone is not the cause.
- Inspected the actual session logs, crash dump PID 57244 and hang dump PID 36248.
  The crash worker is in `Worker.CopyBgrToSharedTexture` at
  `CudaGraphicsD3D11RegisterResource`, via the JPEG viewport/Manhwa renderer.
  Windows records `nvwgf2umx.dll` 32.0.16.2114 access violation 0xc0000005.
- The hang dump has a worker blocked in the same CUDA-D3D registration path and
  the UI blocked at `IDXGISwapChain.Present`, reached while clearing the previous
  Manhwa frame. Logs show D2DERR_RECREATE_TARGET and repeated device recovery.
- The affected NAS ZIP has 891 pages. Checked late-page JPEGs around indexes
  874–878: ordinary 720x7,000–8,000 images, not oversized textures. Actual CPU
  crops/probes are healthy and fast, supporting the native interop diagnosis.
- Secondary retention: every fast/final section is a new source with no later
  reuse, yet the ordinary D2D page LRU retained those retired textures up to its
  512-MB allowance. Manhwa needs only the current viewport import.

## Changes

- `NvJpegNativeDecoder.cs`: new staged viewport API shares existing bounded GPU
  admission, JPEG source cache, native decode, NPP crop/resize/rotation and BGRA
  conversion. Only the bounded viewport is copied through pinned host memory
  before an ordinary D3D upload. It never registers/maps/unregisters a Manhwa D3D
  texture with CUDA. The normal zoom API retains its existing behavior.
- `ManhwaSegmentRenderer.cs`: route supported JPEG sections through this staged
  GPU API. This retains GPU computation and quality settings rather than masking
  the registration failure by switching JPEG decode/resize to CPU.
- `Direct2DViewerSurface.cs`: uncached `PresentTransient` owns one current D2D
  bitmap/texture usage lease. Replacing, clearing, normal presentation and device
  disposal release that import after the prior EndDraw barrier. The normal page
  LRU is unchanged. Extracted bitmap-upload helper serves cached/transient paths.
- `AsyncViewerPanel.Manhwa.cs`: use transient presentation; commit/hide loading
  only when the new frame is accepted. Add optional section request/completion
  and device-recovery breadcrumbs with crop/output/elapsed information.

## Validation

- Final Release build: passed, 0 warnings/errors.
- Normal publish to `release`: passed. Current local executable is
  `release/Fast Reader Viewer.exe`.
- Store builder: passed; generated local development MSIX under `release/store`.
- `git diff --check`: passed (only Git LF/CRLF conversion notices).
- Real native GPU child test: 168 fullscreen-scale top/middle/tail crops from
  seven actual JPEGs, including the failing late pages, fast and final Lanczos.
  GPU readback, all four rotations, NPP alpha/up/downscale and PNG quality paths
  passed. A registration counting/throwing spy confirmed ZERO CUDA-D3D resource
  registrations. Both GPU admission gates cancel without leaking permits.
- Native hidden-HWND Direct2D stress: 500 unique CPU/GPU frames, 3,271 assertions.
  Manhwa creates no historical LRU entries; a seeded ordinary page entry stays
  unchanged. Texture leases remain only for the current import and are released
  on replacement/clear/dispose; native RGB readback and retirement drain pass.
- Viewer state: all 59 async/navigation/width/cancellation checks passed again
  with actual hidden HWNDs and the transient presentation path.
- Integrated real-source hidden-HWND MainForm test: 79 assertions across 18 seeks
  in a fresh native process using the same 891-page NAS ZIP and real metadata,
  ICC, renderer and GPU presentation callbacks. Direct seek to index 875, then
  874/878/890/0/875 over three cycles, including the last section, all pass.
  Every final surface is GPU-backed; device generation remains zero, recoveries
  zero, historical D2D caches empty, and full-page cache-loader calls zero.
- Integrated first seek 275 ms; later JPEG seeks 44–127 ms; WebP page zero
  405–431 ms. OS/NAS caches may have been warm from fixture reads: these are
  fresh-process seek checks, not a cold-storage throughput benchmark.
- Actual CPU crop baseline: 27 crops passed, roughly 42–94 ms. Additional PNG
  100,000-row first/middle/beyond-65,535/tail/rotation/cancellation checks passed.
- Test helpers exited, NAS inputs were read-only, cache roots were temporary,
  and the user's settings JSON remained byte-identical. Logs/harnesses are under
  `%TEMP%/FastReaderManhwaActualNasMainForm-20261112`,
  `%TEMP%/FastReaderManhwaTransientStress-20261112`,
  `%TEMP%/FastReaderManhwaActualCropStress-20261112`,
  `%TEMP%/FastReaderManhwaViewerRegression-20261112/results-transient.txt`, and
  `%TEMP%/fastreader-manhwa-renderer-a23967348cf041d290a3fd3729586e3c`.

## Remaining checks and delivery

- Visible reading with the user's usual input/fullscreen/multiple-window flow
  remains a manual check. Integrated hidden-window tests use real source/GPU
  rendering but do not claim that the user's foreground reader was operated.
- Version remains 2.0.6 / 2.0.6.0 for this local development build. No commit,
  push, GitHub release/asset replacement, Store submission or installation.
