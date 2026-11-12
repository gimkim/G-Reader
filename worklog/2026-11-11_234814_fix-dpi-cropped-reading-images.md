# Fix DPI-dependent cropped images in reading mode

## Symptom and evidence

- Some images were enlarged inside an otherwise correctly sized page rectangle,
  clipping the right and bottom edges. Clearing caches did not resolve it.
- `BitmapAlphaUtility.CloneToPArgb` used `Graphics.DrawImageUnscaled`. GDI+ still
  applies the source/target physical DPI ratio with this overload: a 72-DPI
  source copied into a default 96-DPI bitmap grows by 4/3 and is clipped.
- A synthetic reproduction matched the crop pattern in the reported screenshot.
  The attached 1440x1920 clipboard PNG itself reports about 95.9866 DPI; it does
  not establish the original file's DPI before copying. Setting its DPI to 72
  in memory reproduced the crop with 6,676,047 incorrect pixel channels using
  the legacy conversion; the fixed conversion preserves every pixel.

## Changes

- `BitmapAlphaUtility.cs`: convert using explicit full source/destination pixel
  rectangles and `GraphicsUnit.Pixel`, preserving dimensions and premultiplied
  alpha independently of source DPI. The existing raw PArgb copy stays intact.
- `PersistentPreviewCache.cs`: apply the same pixel geometry to browse JPEG
  flattening. Advance image/archive page and browse cache identities so previously
  cropped previews are regenerated automatically. Keep existing direct-PDF page
  and cover identities, including cache sources used for PDF page remapping.
- GPU presentation, quality settings, scheduling, and thread ownership are
  unchanged. No user settings or production caches were edited or cleared.

## Validation

- Release build: passed, 0 warnings / 0 errors.
- `dotnet publish -c Release -o release --no-restore`: passed.
- Store builder: passed; generated
  `release/store/FastReaderViewer_2.0.5.0_x64.msix` (95,291,565 bytes).
- `git diff --check`: passed (Git reports only its usual LF/CRLF conversion
  notices for edited source files).
- 47 bitmap regression checks passed: 72/96/120/144/300 DPI, asymmetric DPI,
  24bpp RGB / 32bpp ARGB / 32bpp PArgb, owned conversion, semitransparent pixels,
  the supplied full image and copies at 72/120/300 DPI, and a legacy crop
  reproduction. Before the fix, 24 of the initial 43 checks failed.
- 28 cache regression checks passed: JPEG flattening at five DPI values and
  asymmetric DPI; lossless PNG transparency; poisoned legacy page/browse cache
  entries ignored; new asynchronous writes and reads retain full geometry; three
  direct-PDF legacy key comparisons remain equal. PDF key checks use a dummy
  Book identity and do not invoke PDFium or claim a real PDF rendering test.
- Regression harnesses live outside the repository under
  `%TEMP%/FastReaderDpiRegression-20261111` and
  `%TEMP%/FastReaderCacheDpiRegression-20261111`; cache checks use a temporary root.
- Visually inspected the legacy and fixed 72-DPI image outputs. The legacy output
  clips right/bottom; the fixed output contains the complete source image.

## Remaining manual verification

- Open the user's original affected files in the rebuilt application and verify
  full-page fit, single/dual-page layouts, navigation, and zoom on the real GPU.
  No real application UI / archive / PDF / multi-window reproduction was run.
- Version remains 2.0.5 / 2.0.5.0. No commit, push, GitHub release, Store submission,
  or MSIX installation was performed.
