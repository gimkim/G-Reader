# Fix transparent PNG edge fringing

## Symptom

At non-integer zoom, a large transparent PNG displayed a jagged white fringe
around partially transparent cutout edges on the reader's dark background. The
same image appeared smooth in browsers and other viewers.

## Evidence and cause

- Reproduced the pixel pipeline with
  `C:\Users\tatsa\Downloads\IRyS-clear-card.png` (4000 x 6000).
- The file contains 288,764 partially transparent pixels and 5,833,403 fully
  transparent pixels.
- CPU image bitmaps were straight-alpha BGRA, while the full-view Direct2D path
  imported them as `AlphaMode.Ignore` and the thumbnail path declared those
  same bytes as premultiplied. Both interpretations are incorrect for alpha.
- Animated/GPU surfaces had the same `AlphaMode.Ignore` import mismatch.
- Persistent full-view and page-thumbnail previews were always encoded as JPEG,
  which cannot retain source transparency.

## Changes

- Added `BitmapAlphaUtility` to normalize decoded/cached images to
  `Format32bppPArgb` and to premultiply raw BGRA safely.
- Kept Magick.NET's Lanczos input in straight-alpha form for alpha-aware
  filtering, then premultiplied its output before Direct2D presentation.
- Updated fast preview, render cache, page cache, CPU upload, native GPU upload,
  animated WebP upload, contact sheets, and color-management effects to use a
  consistent premultiplied-alpha contract.
- Normalized ImageMagick and TurboJPEG CPU outputs before they reach the UI, so
  the defensive Direct2D conversion is not paid repeatedly on normal JPEG work.
- Added a defensive conversion at the full-view Direct2D upload boundary for
  any legacy/non-normalized bitmap.
- Transparent-capable source types now use lossless PNG persistent page caches;
  JPEG/PDF cache identities and files remain unchanged, avoiding a global cache
  rebuild. Browse cover cards remain flattened JPEGs by design.

## Validation

- `dotnet build -c Release --no-restore`: passed, 0 warnings, 0 errors.
- Reflected into the built application and ran its actual Lanczos thumbnail path
  against the reported 4000 x 6000 PNG. The source normalized from
  `Format32bppArgb` to `Format32bppPArgb`, and the resized output remained
  `Format32bppPArgb`.
- Composited the resulting 667 x 1000 preview over the reader's dark background;
  the reported jagged white fringe was absent.
- `dotnet publish -c Release -o release --no-restore`: passed.
- `git diff --check`: passed (only Git line-ending notices).

## Manual UI verification remaining

Open the reported PNG in full view, test fit view and several zoom levels on a
dark background, then return to thumbnail view and reopen it after the new PNG
preview cache has been written. No automated UI interaction was performed.
