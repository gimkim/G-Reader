# Fast Reader/Viewer agent notes

## Project

- C#/.NET 8 WinForms comic reader derived from the required behavior of CDisplayEx.
- Main UI implementation: `AsyncMainForm.cs`.
- Direct2D rendering: `Direct2DViewerSurface.cs` and `AsyncViewerPanel.cs`.
- Book/folder/archive/PDF loading: `Book.cs`.
- Settings UI and persistence: `ReaderSettingsDialog.cs` and `UserSettings.cs`.
- The original installed CDisplayEx binaries remain at `C:\Program Files\CDisplayEx` for reference only.

## Build and release

```powershell
dotnet build -c Release --no-restore
dotnet publish -c Release -o release --no-restore
powershell -ExecutionPolicy Bypass -File .\packaging\Store\build-store-msix.ps1
```

The current executable is `release\Fast Reader Viewer.exe`.
The Microsoft Store package is written under `release\store`.

For setting up another development machine, follow `MIGRATION.md`. Runtime
settings and preview caches live outside the repository and should never be
committed.

## Preservation rules

- Do not modify or regenerate anything under `versions\1.0-unlimited-cache-smooth`.
- Do not modify or regenerate anything under `versions\2.0-ui-responsive`.
- Do not modify or regenerate anything under `versions\3.0` or `versions\G-Reader-3.0.zip`.
- Keep the corresponding version ZIP archives unchanged.
- Preserve UI responsiveness: image decoding, resizing, cache discovery, and filesystem enumeration must not block the UI thread.
- Preserve Direct2D rendering and the responsive page-navigation path.

## Product concepts and user-facing behavior

Fast Reader/Viewer is a high-performance image, comic, archive, and PDF reader.
GPU acceleration is intentional: keep the normal rendering path on the GPU and
use a CPU path only when the native GPU path is unavailable or has been safely
retired after device loss. A fallback must never be used as a shortcut for
avoiding a GPU bug. Quality settings (including Lanczos) must be honored by
every corresponding render and cache path.

- Supported content includes image folders, ZIP/other archives, and PDFs with
  large page counts. Reading layouts are single-page, dual-page, and offset
  dual-page; reading direction can be left-to-right or right-to-left; portrait
  pages may be paired automatically while landscape pages remain individual.
- Remember the last position for each file/folder. Hotkeys, worker counts,
  cache limits, preview quality, GPU codecs, and history are configurable.
- Folder covers use images in the immediate folder first. If none exist, look
  down at most one child-folder level; do not recursively walk arbitrarily deep
  and do not show a parent-folder thumbnail as a content cover.
- Thumbnail selection supports Ctrl multi-select. A plain click or navigation
  key returns to one selected item. Delete confirms the selected PDF pages with
  a preview (dual-page mode asks which page); copy handles all selected items,
  while open/explorer/read actions use the most recently selected item.
- Deleting PDF pages remaps/migrates existing cache indexes where possible;
  rebuilding every page is the slow fallback, not the default.
- Thumbnail work is visible-item first. Full-view warm-up may use idle capacity
  at lower priority, but selecting or entering full view promotes that request
  and cancels stale selection work after a short debounce. Cache progress is
  tracked per page/file, not as a false contiguous prefix.
- Full-screen top and bottom bars are overlay controls. Show them when the
  pointer is near the corresponding edge by a viewport percentage, so they do
  not resize the image area and work across DPI/scaling settings. History is a
  recent folder/archive popup with configurable enable/retention count.
- PDF cover generation should probe only the first few pages (the configured
  four-page cover window) and stop as soon as a usable cover is available; for
  a folder containing only archives/PDFs, use the first supported entry rather
  than leaving the folder cover empty.
- When a folder/archive has no supported items, stay in thumbnail mode with a
  parent card and an in-page message; do not block the user with an OK dialog.

## Engineering invariants

- Never block the WinForms UI thread on image/PDF decoding, resizing, cache
  discovery, filesystem/archive enumeration, worker startup, or GPU retirement.
  All expensive work is cancellable, bounded, and scheduled away from paint and
  input handlers.
- Keep global decode/codec admission bounded; cancel stale archive/PDF jobs and
  dispose archive/native resources outside shared scheduler locks. Do not create
  unbounded PDFium/native worker processes. Use the PDFium watchdog and extended
  diagnostics to detect crashes, hangs, and stalled heartbeats.
- The automatic global fast-preview worker default is approximately half of the
  logical CPU cores, then bounded by the codec/GPU gates and memory budget. A
  setting explanation must show effective limits and products of interacting
  values instead of silently accepting an impossible number.
- JPEG should prefer nvJPEG/NPP on a supported NVIDIA setup (staged GPU paths
  for background work); ImageMagick/CPU is the explicit fallback when the GPU
  codec is unavailable or has been retired for the session.
- Direct2D resources must have clear ownership and a safe end-draw/device-loss
  barrier. Do not run competing per-thumbnail background Direct2D render-target
  operations against the UI target. Prefer staged GPU/NPP/nvJPEG work and paced
  UI uploads; invalidate stale callbacks after a device reset.
- Persistent preview caches live outside the repository. Cache reads/writes are
  cancellation-aware and bounded; cache migrations must preserve valid entries
  after page deletion or reorder.
- Extended logging is optional but, when enabled, records detailed errors,
  renderer/GPU recovery, worker queues, cache activity, crash data, and hang
  dumps under `%LOCALAPPDATA%\Fast Reader Viewer\Diagnostics`.

## Build, release, and change tracking

- Use `release` as the normal output directory. The Store package is written to
  `release\store` by `packaging\Store\build-store-msix.ps1`.
- A release version bump updates `<Version>`, `<AssemblyVersion>`, and
  `<FileVersion>` together; the MSIX package version is derived from the
  four-part assembly version. Build with the commands in the Build and release
  section and run `git diff --check` before committing.
- Record each change session in a separate timestamped Markdown file under
  `worklog\YYYY-MM-DD_HHmmss_*.md`, including the symptom, evidence, files,
  validation, and any remaining manual UI test needed. Static/build checks do
  not claim that a real GPU/PDF/archive UI reproduction was completed.
- Keep runtime settings, caches, dumps, and generated release artifacts out of
  source control unless a release asset is intentionally attached to GitHub.
  Preserve all historical `versions\` content listed above.
- Settings pages must remain readable at different resolutions and Windows DPI
  scaling levels: use responsive layout/anchoring, avoid clipped explanatory
  text, and keep performance/codec relationships visible. The update checker
  compares GitHub release versions and always asks before downloading and
  relaunching a newer build.

## Microsoft Store/package identity

The Store package uses the reserved identity `gimkim.FastReaderViewer` and the
assigned publisher identity from Partner Center. Keep the executable/package
display name as Fast Reader/Viewer and include only capabilities actually used
by the app. A self-built unsigned binary may trigger SmartScreen; Store MSIX
signing/distribution is the supported trust path.

## Historical utility

`legacy-tools\enable-slider-default.ps1` is the earlier binary-patching utility from before the C# rewrite. It is retained only as project history.
