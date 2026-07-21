# G Reader

G Reader is a fast, Windows-native comic and image reader written in C#/.NET 8. It combines Direct2D full-page and thumbnail renderers, responsive background processing, configurable memory caches, and a virtualized library browser for folders, archives, and PDFs.

The project focuses on immediate interaction: opening, scrolling, page navigation, zooming, and resizing remain responsive while previews and final Lanczos renders are produced in the background.

## Highlights

- Direct2D presentation for both full-page and thumbnail views without page-transition effects
- Single page, two-page, and two-page offset layouts
- Left-to-right and right-to-left reading with matching navigation and slider direction
- Automatic single-page display for landscape or unusually wide images
- Always-fit default view with smooth pointer-anchored zoom and 100% double-click zoom
- Progressive preview rendering followed by final Lanczos quality
- Monitor ICC color management enabled by default, including embedded image profiles and automatic profile switching when the window moves between displays
- Virtualized thumbnail browser that remains practical with thousands of items
- Folder, archive, and PDF contact sheets generated progressively around the viewport
- Configurable and automatically optimized worker counts, per-image threads, and cache budgets
- Automatic optimization assigns Zoom Lanczos all logical processors except one reserved for UI and operating-system responsiveness
- Animated GIF and animated WebP playback without changing the static-image cache path
- Drag-and-drop, command-line opening, Explorer integration, and configurable toolbar hotkeys

## Supported input

| Type | Formats |
| --- | --- |
| Images | JPEG, PNG, BMP, TIFF, GIF, WebP |
| Comic archives | ZIP/CBZ, RAR/CBR, 7Z/CB7 |
| Documents | PDF through the Windows PDF renderer |
| Collections | Image folders and library folders containing subfolders, archives, or PDFs |

## Reader modes

### Full-page view

- Opens in fit-to-screen mode by default.
- Cycles between single page, two pages, and two pages offset. Offset leaves the first page unpaired so later spreads align correctly.
- Optionally displays landscape pages as a single page while the reader remains in two-page mode.
- Places and navigates spreads according to the selected LTR or RTL direction.
- Uses Left/Right, Home/End, mouse wheel, the bottom position slider, or configurable toolbar shortcuts for navigation.
- Configurable fullscreen mode removes the toolbar and window chrome; its compact translucent page slider floats over the image without changing layout.
- Returns to Fit immediately when changing pages from a zoomed view.
- Copies the current file with `Ctrl+C`; a two-page spread copies both source files.

### Zoom and pan

- Double-click a fitted image to enter 100% zoom; double-click again to return to Fit.
- `Ctrl+mouse wheel` performs smooth, pointer-anchored zoom in either Fit or Zoom mode.
- Hold the left mouse button to pan with immediate Direct2D movement.
- Existing sharp pixels remain visible while panning; only newly exposed edge regions are refined.
- Zoom refinement is progressive: the current texture responds immediately, an intermediate viewport preview arrives next, and a full-quality Lanczos crop replaces it in the background.
- The final renderer processes only the viewport crop where possible instead of resizing the entire original image.
- The current zoom percentage appears in both the information area and a temporary overlay.

### Thumbnail and library view

- Uses a Direct2D virtual grid: only visible cells are drawn, so the control itself does not create thousands of child controls.
- Single-click selects an item; double-click or `Enter` opens it.
- Long folder, archive, and PDF names wrap into a centered multi-line label inside the tile instead of being clipped beyond its border.
- Arrow keys always move the thumbnail selection. Home and End move to the first and last item across folders, archives, PDFs, and images.
- `Ctrl+mouse wheel` changes the number of images per row.
- Mouse-wheel and precision-touchpad input scrolls by continuous pixels with browser-style easing instead of fixed lines.
- `Ctrl+C` copies the selected image file.
- Selection changes update the bottom position slider and file information.
- An address bar opens an entered folder or file path.
- The parent tile (`… Parent folder`) moves up one level and retains selection on the folder, archive, or PDF that was exited.
- `Move up` switches Full-page view to Thumbnail view, or moves Thumbnail view to the parent folder.

Folder and container tiles receive stacked contact sheets made from up to four images. Image thumbnails and contact sheets share the fast-preview worker pool and one viewport-aware priority queue. Work starts at the selected visible item; if selection is outside the viewport, visible cells take priority.

For very large libraries, contact sheets use a bounded working set around the current viewport instead of generating thousands of previews that would immediately be evicted. Scrolling cancels stale viewport work, redraws the visible scene from cached GPU textures, and resumes preview generation after interaction settles.

## Sorting and library navigation

The toolbar keeps independent sorting controls for content inside folders and content inside archives.

Available criteria:

- Name (alphabetical)
- Name (numeric/natural)
- Date modified
- EXIF date taken
- Size
- Extension
- Ascending or descending direction

Folder view keeps the stable group order **Folder → Archive/PDF → Image**, then applies the selected folder sort inside each group. Archive pages use their complete internal paths so nested archive directories participate in name ordering.

At a book boundary, automatic library navigation can be disabled or limited to folders, archives/PDFs, or both. Navigation follows the same grouped and sorted order shown by the parent thumbnail view.

Settings can choose whether moving to the previous book opens its last page or its first page. Moving to the next book continues to open at the first page.

## Opening and Windows integration

- Open a file or folder from the toolbar.
- Drag a file or folder onto the window; G Reader activates and takes focus after the drop.
- Configure a library root and use **Open random** to choose an eligible folder, archive, or PDF recursively.
- Use **Open in Explorer** to select the current image or archive/PDF source file. In Thumbnail view, a selected folder, archive, or PDF tile is selected in Explorer instead.
- Register G Reader per-user as an available Windows image viewer from Settings.
- Images launched through the Explorer association always open in full-page, single-page mode.
- When possible, an image opened from Explorer follows the source Explorer window's current item order; natural numeric ordering is the fallback.

Command-line examples:

```powershell
& '.\G Reader.exe' 'C:\Books\Volume 01.cbz'
& '.\G Reader.exe' 'C:\Pictures\Album'
& '.\G Reader.exe' --open 'C:\Pictures\page001.jpg'
& '.\G Reader.exe' --file 'C:\Books\book.pdf'
& '.\G Reader.exe' --folder 'C:\Comics\Series'
```

## Rendering and responsiveness

G Reader separates interactive display work from decoding, preview generation, Lanczos resizing, and cache cleanup.

- Embedded ICC metadata and the active Windows monitor profile are read off the UI thread. Direct2D applies cached source-to-monitor transforms on the GPU for full-page, zoom, and image-thumbnail rendering; color management can be disabled in Settings.
- Changing folders, archives, or PDFs retains decoded and resized pages from recent books as low-priority cache entries. Returning can reuse ready pages, while retained entries are evicted before active-book data whenever the configured memory budget is needed.
- Full-page presentation and the thumbnail grid use independent Direct2D HWND render targets.
- Thumbnail images and contact sheets upload lazily into a dedicated GPU texture LRU. An adaptive uploader measures observed transfer throughput and limits each frame by both elapsed upload time and bytes: idle frames receive a larger budget to fill high-resolution grids quickly, while scrolling uses a smaller latency budget to preserve input responsiveness.
- Continuous touchpad gestures use a paced Direct2D present path (up to 30 FPS) with a 4 ms / minimum 8 MB adaptive upload budget, so Windows paint-message coalescing cannot hide newly completed or newly uploaded thumbnails until scrolling stops. Viewport priority refreshes retain the latest deferred position instead of dropping it inside the throttle interval.
- Thumbnail labels, placeholders, vector icons, badges, and the overlay scrollbar use DirectWrite/Direct2D rather than GDI+ painting.
- Static page navigation can reuse a GPU-side LRU of recently uploaded surfaces.
- Fast previews are scheduled ahead of final-quality Lanczos work.
- Folder, archive, and PDF contact sheets use the same two-stage policy as image thumbnails: a fast contact sheet appears first, then a separately cached Lanczos contact sheet replaces it. Fast and final entries have independent RAM and persistent-disk identities.
- PDF contact sheets open and parse a document once per quality pass and rasterize only near the useful output size. Normal PDF reading shares one parsed `PdfDocument` across all page entries instead of reopening the complete file for every pre-cache decode.
- Large JPEG files request a decoder-scaled DCT source near the useful output resolution, avoiding unnecessary full 45 MP managed bitmaps.
- Optional NVIDIA nvJPEG decoding keeps a shared CUDA context warm and reuses decoder states, streams, pinned host buffers, and VRAM allocations. Full view, rotated pages, zoom viewport patches, page thumbnails, and folder/archive contact sheets can remain GPU-resident through NPP and CUDA–D3D11 interop. Background batch concurrency is calculated from current free/total VRAM and each source/output image's estimated working set, with 15% (at least 1 GB) kept as headroom and one stream reserved for visible-page and zoom requests; unsupported paths immediately fall back to TurboJPEG rather than delaying the UI.
- Zoom keeps up to two full-resolution decoded JPEG sources in a bounded VRAM LRU, then generates only newly exposed viewport regions. GPU-created page thumbnails are encoded by nvJPEG before the reduced compressed bytes are sent to the persistent disk cache.
- JPEG fit/thumbnail rendering first uses the native TurboJPEG 3.1 decoder directly into BGRA memory, bypassing ImageMagick's decode wrapper. Unsupported precision/colorspace or native failures fall back to the established Magick.NET path.
- JPEG decode is codec-bound and does not scale well inside one image. G Reader therefore converts otherwise-unused per-image thread capacity into additional concurrent JPEG jobs, capped by the machine's logical CPU count. Generic large-image paths retain their configured worker gate to avoid multiplying full-resolution RAM usage.
- Non-JPEG thumbnails decode their source once and derive both the immediate fast preview and final Lanczos result before releasing the large source bitmap. Worker gates bound the number of decoded PNG, WebP, BMP, GIF, and TIFF sources resident at once.
- Non-JPEG and animated paths pass BGRA pixels directly between Magick.NET and System.Drawing, avoiding the previous in-memory BMP encode/decode round trip.
- Window resizing uses stale-while-revalidate: the previous surface remains visible until fast and final replacements arrive.
- Decoding, cache discovery, archive scanning, resizing, and bulk disposal stay off the WinForms UI thread.
- Cancellation callbacks are asynchronous so stale render work does not hold the UI thread.
- Cache cleanup is delayed and coalesced with temporary headroom rather than enforcing a strict limit during interaction.
- Thumbnail completion notifications are coalesced, and off-screen completions do not enqueue unnecessary UI repaints.
- Thumbnail scrolling redraws the virtual scene from GPU textures; no GDI window blit or per-tile child-control layout is involved.
- In-flight thumbnail and contact-sheet generation continues during wheel and scrollbar movement, so completed previews can appear while scrolling; the work queue is reprioritized after the viewport settles.
- While scrolling continuously, a coalescing viewport queue samples the visible range about every 180 ms and requests only missing fast previews in and just around the viewport. It never cancels the main thumbnail batch and retains only the newest pending viewport, avoiding event floods and long queues.
- High-frequency wheel input is combined to match display update cadence. Focused precision-touchpad input preserves sub-notch deltas, while asynchronous Raw Input keeps hover scrolling available when the app is unfocused.

Animated files are inspected and decoded only when the matching page is visible. Static images continue through the normal fast cache path without animation overhead.

## Cache and performance settings

First launch enables automatic optimization and derives a profile from available memory and logical CPU count. The detected profile can be copied into permanent manual settings and adjusted further.

Configurable values include:

- Lanczos quality
- Cache ahead and cache behind budgets
- Full-page preview cache
- Thumbnail Lanczos cache
- Thumbnail fast-preview cache
- Maximum internal thumbnail preview edge in pixels
- Pre-cache images processed concurrently
- Fast-preview worker count and CPU threads per worker
- ImageMagick threads per image
- Zoom-refinement ImageMagick threads
- Reader background color

The persistent-cache section also provides **Clear all disk cache**. Deletion is confirmed first, runs away from the UI thread, coordinates with active cache writers, and reports the removed file count and size.

The manual defaults use a soft 4,096 MB page-cache target split into 3,072 MB ahead and 1,024 MB behind. Actual automatic values depend on the machine.

## Toolbar and hotkeys

The application uses one icon toolbar instead of a traditional top menu. It contains:

1. Open file
2. Open folder
3. Open random
4. Open in Explorer
5. Move up
6. Sort inside folder
7. Sort inside archive
8. Start
9. Left
10. Right
11. End
12. Full page / Thumbnail grid
13. Page layout
14. Auto-single landscape
15. LTR / RTL
16. Settings

Every action button has a tooltip and can be assigned a custom shortcut in Settings. `Ctrl+C` and `Ctrl+V` are reserved for clipboard operations.

Default shortcuts:

| Action | Shortcut |
| --- | --- |
| Open file | `Ctrl+L` |
| Open folder | `Ctrl+O` |
| Open random | `Ctrl+R` |
| Open in Explorer | `Ctrl+Shift+E` |
| Move up | `Alt+Up` |
| Start / End | `Home` / `End` |
| Left / Right | `Left` / `Right` |
| Full page / Thumbnail grid | `Ctrl+T` |
| Page layout | `Ctrl+D` |
| Auto-single landscape | `Ctrl+Shift+A` |
| LTR / RTL | `Ctrl+J` |
| Settings | `Ctrl+,` |

In RTL mode, physical Left advances and Right goes backward. Start/End icons and positions also follow the active reading direction.

## Persistent behavior

G Reader remembers display and application preferences, including:

- Thumbnail or full-page view
- Thumbnail images per row
- Page layout and auto-single-landscape state
- LTR/RTL direction
- Sorting criteria and directions
- Cache, worker, quality, color, library, and hotkey settings
- Maximized state, or the normal window position and size

Settings are stored in:

```text
%APPDATA%\G Reader\settings.json
```

Reading history, bookmarks, resume position, and last-page state are intentionally not stored.

## Status and feedback

- The bottom slider displays the current page and total page count.
- Its colored region indicates the ready rendered-cache range and mirrors direction in RTL mode.
- The information area shows filename, source/container size, resolution, and Fit or zoom percentage.
- Loading and generation placeholders appear before previews are ready.
- Temporary overlays confirm zoom changes and clipboard copies.

## Build from source

Requirements:

- Windows 10 or Windows 11, x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Optional nvJPEG acceleration: an NVIDIA driver plus matching CUDA runtime libraries (`cudart`, `nvjpeg`, and preferably `nppc`/`nppif`) in the application directory, `PATH`, or a CUDA Toolkit `bin` directory. The normal CPU decoder remains available without CUDA.

Restore and build:

```powershell
dotnet restore .\CDisplayEx.CSharp.csproj
dotnet build .\CDisplayEx.CSharp.csproj -c Release
```

Publish a framework-dependent single-file build:

```powershell
dotnet publish .\CDisplayEx.CSharp.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\release
```

The repository contains source code and project assets. Local release output and preserved development snapshots are excluded from Git.

## Project structure

| File | Responsibility |
| --- | --- |
| `AsyncMainForm.cs` | Main window, toolbar, input, book lifecycle, background scheduling |
| `AsyncViewerPanel.cs` | Fit/zoom layout, progressive rendering, zoom crop refinement |
| `Direct2DViewerSurface.cs` | Direct2D presentation and GPU-side surface cache |
| `ThumbnailGridView.cs` | Virtual thumbnail browser, layout, selection, scrolling, and input |
| `ThumbnailGridView.Direct2D.cs` | Direct2D/DirectWrite grid renderer and GPU texture LRU |
| `Book.cs` | Folder, archive, PDF discovery and page ordering |
| `PageCache.cs` | Decoded and rendered page cache state |
| `EncodedJpegRenderer.cs` | Decoder-scaled JPEG preview and viewport rendering |
| `AnimatedImageRenderer.cs` | Animated GIF/WebP frame handling |
| `RenderWorkScheduler.cs` | Fast, final, and urgent rendering lanes |
| `ReaderSettingsDialog.cs` | Settings, automatic profile, cache/thread controls, hotkeys |

## Intentionally omitted

The following CDisplayEx-style features are outside this project's scope:

- Windows Explorer thumbnail/shell extension
- Reading history, bookmarks, recent/resume, and last-page persistence
- Gamma, white balance, vibrance, and automatic color correction
- Leap Motion support
- Original CDisplayEx toolbar resources and translations

## Notes

G Reader is a Windows-only application. The normal image pipeline uses Magick.NET/ImageMagick on the CPU; the optional nvJPEG/NPP pipeline can decode, resize, cache, and present Full-view JPEG surfaces without leaving GPU memory. Direct2D accelerates full-page and thumbnail presentation, scrolling, zooming, and interactive movement. PDF support relies on Windows' built-in PDF APIs.
