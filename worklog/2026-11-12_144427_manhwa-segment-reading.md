# Manhwa reading mode for long vertical images

## Request and behavior

- Add a mode that reads tall images in consecutive sections no taller than the
  viewport, advancing to the next image only after the current strip is finished.
- Read > Manhwa mode (long images), a toolbar toggle, and a configurable hotkey
  enable the mode. The default hotkey is unassigned. It applies to image folders
  and archives; PDFs retain their normal layout.
- Fit image width to the reading viewport. Crop contiguous source rows without
  gaps or overlaps. The final partial section stays at the same scale, top
  aligned, with space below when necessary.
- Next/previous, click-to-navigate, wheel, and vertical navigation move between
  sections before images. Moving backward across an image/book opens the last
  section. Home/direct selection starts at the top; End opens the final section.
- Show section position in the status bar. Remember the image/source-row/rotation
  under the existing Remember reading position setting. Switching temporarily
  to thumbnails can resume the same section; explicit selection starts at top.
- Share the persistent mode across windows while keeping each window's section,
  cancellation, and presentation independent. Preserve saved single/dual layout
  preferences. Ordinary zoom is disabled in this fit-width mode.

## Implementation

- `ManhwaPageLayout.cs`: integer, overflow-safe crop geometry without allocating
  a list proportional to image height; handle tiny and extreme viewports.
- `AsyncViewerPanel.Manhwa.cs`, `AsyncViewerPanel.cs`: separate section state,
  asynchronous metadata/fast/final rendering, cancellation/version barriers,
  queued navigation during metadata, resize/source-row restoration, safe surface
  ownership, device/quality re-rendering, and stale normal-preview guards.
- `AsyncMainForm.cs`: mode entry, navigation/book boundaries, crop callbacks,
  status, resume, thumbnail transitions, and no full-strip precaching in this
  mode. Normal thumbnails remain independent.
- `UserSettings.cs`, `SharedAppServices.cs`, `ToolbarHotkeys.cs`,
  `ToolbarIconFactory.cs`: persistence/shared notification and accessible controls.
- `ManhwaSegmentRenderer.cs`: urgent bounded worker admission, rotated first-frame
  extraction, cancellation-aware decode, GPU JPEG viewport rendering and staged
  NPP resizing, alpha handling, and bounded Direct2D uploads. Exact Lanczos
  variants unavailable through NPP use their requested ImageMagick filters,
  retaining GPU presentation. Full strips are not uploaded as Direct2D textures.
- `NvJpegNativeDecoder.cs`: opt-in cancellable GPU-slot waiting and upscaling;
  existing callers keep nonblocking/downscale-only defaults.
- Native validation exposed pre-existing NPP binding failures: all five geometry
  exports were sought in the filtering DLL and were null. Verified installed
  CUDA 12.1 exports and bind resize/mirror to nppig and transpose/channel exchange
  to nppidei. Real GPU readback then exposed reversed 90/270 mirror axes; corrected
  them using the installed NppiAxis definitions and verified all four rotations.
- `README.md`: mode usage and retained normal PDF/single/dual behavior.

## Validation

- Final `dotnet build -c Release --no-restore`: passed, 0 warnings/errors.
- Final `dotnet publish -c Release -o release --no-restore`: passed. The updated
  local executable is `release/Fast Reader Viewer.exe`.
- Store builder passed and generated the local development MSIX under
  `release/store/FastReaderViewer_2.0.6.0_x64.msix`.
- `git diff --check`: passed (only Git LF/CRLF conversion notices).
- Pure layout: 2,095 dimension/viewport cases, 3,840,354 assertions covering exact
  and partial tails, complete pixel coverage, reverse symmetry, source-row
  restore, DPI-scaled viewports, invalid inputs, and near-int.MaxValue dimensions.
- Viewer hidden-STA controls: 26 checks covering fast/final completion, metadata
  navigation queueing, rapid input/cancellation/stale results, same-key refresh,
  resize, backward entry, boundaries, restore, mode exit, and stale normal paths.
- Main-form isolated STA harness: 23 checks covering same-image/image/book
  navigation, Home/End, metadata input, reopening/resume, thumbnail transitions,
  shared toggles, JSON persistence, and retained dual preference. No full-page
  loader calls occurred. The real user settings file was unchanged.
- New renderer with real ImageMagick and explicit GPU stubs: 603 checks across
  PNG/BMP/TIFF/GIF/WebP/JPEG, four rotations, four quality filters, native/downscale/
  upscale outputs, alpha, first animation frame, and cancellation/invalid crops.
- Real native GPU child process: NPP default/no-upscale and opt-in upscaling,
  premultiplied alpha resizing, PNG segment GPU surfaces for all four quality
  settings, and JPEG ROI GPU readback under 0/90/180/270 degrees all passed.
- Existing viewport crop callback also passed 320 format/rotation/quality/section
  cases. Temporary harnesses are outside the repository under
  `%TEMP%/FastReaderManhwa*` and
  `%TEMP%/fastreader-manhwa-renderer-a23967348cf041d290a3fd3729586e3c`.

## Remaining checks and delivery scope

- Full interactive reading with the user's actual long-image folder/archive,
  fullscreen/DPI changes, and multiple visible windows remains a manual check.
  Hidden controls and native readback do not claim that full UI reproduction.
- No user settings, source images, or production caches were changed by tests.
- Version remains 2.0.6 / 2.0.6.0 for this local development build. This session
  does not replace the published GitHub 2.0.6 assets. No commit, push, new GitHub
  release, Store submission, or MSIX installation was performed.
