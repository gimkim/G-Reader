# G Reader

Toolbar page-layout control cycles through Single page, Two pages, and Two pages offset. Offset keeps the first page single so subsequent spreads stay paired correctly.

The toolbar view-mode control switches between the full-page reader and a virtualized thumbnail grid. The grid includes an images-per-row slider and remembers both the last view mode and grid density.

In the thumbnail grid, single-click selects a page, double-click opens it, Ctrl+C copies the selected page as a file, and Ctrl+mouse-wheel changes the images-per-row density. Ctrl+C and Ctrl+V are reserved from toolbar hotkey assignment.

Command line opening supports `G Reader.exe "C:\path\book.cbz"`, `G Reader.exe "C:\path\folder"`, and the `--open`, `--file`, or `--folder` options.

Open random uses the library root configured in Settings, scans it recursively in the background, and randomly opens an archive, PDF, or folder containing images.
Open in Explorer selects the current image file for folders, or the source archive/PDF file.

Hardware-accelerated Windows comic and image reader written in C#.

## Run

Launch `release/G Reader.exe`, use the Open File/Open Folder toolbar icons, or drag a supported book onto the window.

The top menu bar has been replaced by one icon-only toolbar. Every icon has a tooltip and navigation follows the selected reading direction. Pages always default to fit the current viewport.

The application requires the .NET 8 Windows Desktop Runtime. This runtime is already installed on the development machine.

## Supported input

- Image folders
- JPEG, PNG, BMP, GIF, TIFF and WebP
- ZIP/CBZ, RAR/CBR and 7Z/CB7
- PDF through the Windows PDF renderer

## Implemented reader behavior

- Single and double-page display
- Optional double-page mode that automatically shows landscape/wide pages as a single page
- Optional cover page and forward-one-page navigation
- Japanese/right-to-left page placement
- Natural numeric page sorting
- Always fit-to-screen, rendered with Lanczos filtering
- Page rotation
- First, previous, next and last page actions
- Fullscreen, slideshow, thumbnails, drag-and-drop, printing, save and clipboard
- Bottom page slider, shown automatically whenever a book opens
- Persistent display preferences only
- Folder and archive scanning runs outside the UI thread
- Pages and thumbnails appear progressively as decoding completes
- A 4,096 MB memory budget shared by decoded pages and ready-to-display Lanczos pages warms both directions around the current position
- The budget favors navigation speed: 3,072 MB for ready-to-display surfaces and 1,024 MB for decoded source pages
- One persistent per-book background worker keeps decoding and Lanczos pre-resizing across normal page changes
- Background pre-rendering uses a CPU-sized worker pool while reserving one logical processor for foreground UI work
- The Lanczos pipeline uses an uncompressed handoff to reduce page-switch latency
- Background workers maintain a configurable moving soft-target RAM cache (defaults: 3072 MB ahead and 1024 MB behind)
- Cache cleanup is coalesced and delayed, with 512 MB of temporary headroom, so navigation does not contend with strict per-page eviction
- Direct2D keeps a 512 MB GPU-side LRU of recently displayed resized pages, avoiding repeated RAM-to-GPU uploads when navigating back and forth
- GPU cleanup also waits until navigation pauses and allows 128 MB of temporary headroom
- Rendered bitmaps enter the cache by ownership transfer, so no pixel copy or sorting occurs while foreground lookup locks are held
- Adaptive pre-cache uses up to 30 workers on 32-thread systems when the current page is within 12 pages of the uncached boundary
- Lanczos pre-rendering passes BGRA pixel buffers directly between System.Drawing and Magick.NET, avoiding intermediate BMP encoding and decoding
- Initial cache fill stays at full burst speed until the 4096 MB budget is within 128 MB of capacity; adaptive throttling starts only afterward
- Full-size bitmap cloning and bulk decoded/render-cache disposal always run off the WinForms UI thread
- Cache-hit navigation skips redundant Loading/Ready status updates on the WinForms UI thread
- Cache accounting and slider-range scans use lock-free snapshots, separate from the foreground render lookup lock
- Page layout batches proxy-control bounds into one suspended WinForms layout pass
- Full-size clone finalization and page rotation stay on background workers
- Rapid navigation uses asynchronous cancellation callbacks, so canceling stale display/render work does not hold the UI thread
- Cache-status UI posts are coalesced to the latest value and unchanged slider cache ranges do not repaint
- Settings provides four Lanczos-family quality levels, cache-ahead/cache-behind MB limits, and reader background color
- Optional boundary navigation opens the previous/next archive or PDF by natural file name or modified date; image folders can be included with a separate checkbox, or the feature can be disabled
- Full-book pre-caching starts once per opened book and never restarts during page navigation
- Opening another folder or archive clears decoded, resized and Direct2D page caches first
- The bottom position slider remains visible without an animated progress strip
- Numeric filenames sort numerically first; remaining names use string order
- Window resizing is debounced and rerendered asynchronously with Lanczos
- Displayed pages stay horizontally centered in the reading area
- Arrow navigation follows reading direction: Right advances in LTR, while Left advances in RTL
- Mouse-wheel down/up over the reading area moves to the next/previous page
- Mouse-wheel page navigation follows the pointer over G Reader even while another application has focus
- Unfocused wheel capture uses asynchronous Windows Raw Input and never installs a low-level system mouse hook
- Background Lanczos pre-render workers run below UI priority and Magick uses one internal thread per page to avoid nested CPU oversubscription
- Dropping a file or image folder onto G Reader activates and focuses the reader
- Pages switch instantly with no transition effect on a double-buffered Direct2D HWND render target
- Held navigation input is applied immediately instead of waiting for an animation queue
- One-click LTR/RTL reading-direction switch
- The bottom position slider mirrors its fill, thumb and drag direction in RTL mode
- Window maximized state is restored; normal windows also restore their last position and size
- Bottom position slider shows current/total pages plus loading and cache progress

## Intentionally omitted

- Windows Explorer thumbnail and shell extension
- History, bookmarks, recent/resume and last-page state
- Gamma, white balance, vibrance and automatic color correction

## Known parity gaps

- Animated GIF/WebP playback is currently displayed as a still frame.
- Magnifier and physical page splitting are represented by the basic viewer behavior but are not yet equivalent to CDisplayEx 1.10.33.
- Leap Motion support is not included.
- The original toolbar bitmap resources and translations were not copied.

## Build

```powershell
dotnet build .\CDisplayEx.CSharp.csproj -c Release
dotnet publish .\CDisplayEx.CSharp.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\release
```
