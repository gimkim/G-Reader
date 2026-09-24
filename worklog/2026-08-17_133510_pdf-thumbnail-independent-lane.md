# PDF thumbnail lane no longer limited by generic four-worker gate

## Symptom

PDF thumbnail generation was much slower after preview concurrency was limited.
Increasing the configured PDFium process count did not fully improve throughput.

## Evidence and cause

- The four-job staged nvJPEG safety gate applies only to background JPEG GPU
  thumbnail decode and was not directly acquired by normal PDF page entries.
- PDF `PageEntry` instances expose `DecodeThumbnail`, but the progressive and
  visible thumbnail schedulers classified every non-encoded page as generic
  non-JPEG work.
- As a result PDFium page rendering was additionally capped by
  `FastPreviewWorkerCount` (commonly 4), even when `PdfiumProcessCount` was 8.
- Fast PDF thumbnails already returned from `RenderPageToFit` at the requested
  bounds, but were sent through the generic resize lane a second time.

## Change

- Added a dedicated PDF thumbnail admission lane sized from
  `PdfRendering.PdfiumProcessCount` for progressive fast and full page work.
- The process-wide `GlobalFastPreviewConcurrency` remains the upper scheduling
  ceiling; ordinary non-JPEG resize workers no longer reduce PDF throughput.
- Visible PDF batches now use
  `min(PdfiumProcessCount, GlobalFastPreviewConcurrency)` and may queue a bounded
  two batches without changing the PDFium process-pool safety limit.
- Fast PDF raster output is accepted directly after rotation because PDFium has
  already fit it to the requested thumbnail bounds. The redundant generic
  resize-worker pass was removed.
- Settings explanations now state the effective PDF formula and distinguish the
  PDF lane from generic non-JPEG resizing.

## Safety and performance boundaries

- PDF process creation remains bounded by the existing PDFium process pool and
  watchdog; this change does not create unbounded native workers.
- The nvJPEG staged thumbnail ceiling of four is retained for the archive/folder
  GPU path which previously caused device-hung failures under excessive CUDA/D3D
  pressure.
- UI uploads remain paced by the existing Direct2D upload budgets.

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `dotnet publish -c Release -o release --no-restore` passed.
- `git diff --check` passed apart from existing line-ending notices.
- Published `release\Fast Reader Viewer.exe` is version `1.9.4.0`, written at
  2026-08-17 13:35 local time.

## Remaining manual UI check

Clear thumbnail cache, open a long PDF, and compare throughput with PDFium set to
4 and 8 while Global fast-preview concurrency is at least 8. Confirm that 8 can
feed more PDF pages concurrently, thumbnails remain responsive while scrolling,
and no PDFium watchdog, GPU device-loss, or process-count regression occurs.
