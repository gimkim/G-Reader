# Single-process multi-window profile host

## Request

- Allow Fast Reader/Viewer to behave like a browser: multiple independent
  windows use one settings/cache profile without racing settings, source edits,
  cache maintenance, GPU work, PDFium workers, or the updater.
- Preserve UI responsiveness and the existing GPU-first rendering paths.

## Previous risk

- Every executable launch previously created a separate top-level process and
  independently loaded and saved `settings.json`.
- Process-local cache writer gates could not coordinate clear, cleanup, or PDF
  cache migration with another process using the same cache root.
- Destructive PDF-page and image-file edits had no application-wide per-source
  serialization.
- Static GPU/PDF/decode limits bounded one process, so launching several copies
  multiplied the effective resource budget.

## Implementation

- Added a per-user/profile mutex and current-user named-pipe command channel.
  A later Explorer, command-line, or executable launch forwards its normalized
  open request to the profile owner and exits.
- Added an `ApplicationContext` that keeps one message pump alive for multiple
  independent `AsyncMainForm` windows. `Ctrl+N` opens another window, `Ctrl+W`
  closes one, and `Ctrl+Q` exits all windows.
- Centralized the in-memory settings object, settings dialog ownership, change
  notification, source-change notification, ImageMagick thread policy, and
  per-window memory allocation in `SharedAppServices`.
- Kept book, page, selection, cancellation, Direct2D presentation, and view mode
  local to each window. Only the active/final window supplies next-session
  window/view defaults.
- Treated configured RAM cache sizes as process totals. One window receives the
  full budget; with several windows the active one receives 60% and inactive
  windows share 40%. Inactive/minimized full-view warming runs at idle priority.
- Made settings writes atomic with a unique temporary file, durable flush,
  replacement backup, process mutex, and fallback loading from the previous
  valid copy. The mutex wait is bounded so settings I/O cannot look like a hang.
- Added canonical-path mutation leases for image deletion and PDF page editing.
  Multiple file acquisition is sorted to prevent lock-order deadlocks, and the
  on-disk lease coordinates compatible processes sharing the profile.
- Added a cross-process cache reader/writer protocol. Ordinary writes share a
  lock-file lease; clear, cleanup, capture-remap, and apply-remap hold writer
  intent and wait for exclusive access before touching the cache tree.
- Adapted raw mouse-wheel registration and extended-diagnostics heartbeats for
  multiple top-level windows.
- Kept update checks process-global. An accepted update calls
  `Application.Exit`, waits for the one hosting process, replaces the executable,
  and relaunches after all reader windows close.

## Files

- `Program.cs`
- `SingleInstanceCoordinator.cs`
- `FastReaderApplicationContext.cs`
- `SharedAppServices.cs`
- `CacheProcessCoordinator.cs`
- `AsyncMainForm.cs`
- `RawMouseWheelInput.cs`
- `ExtendedDiagnostics.cs`
- `PersistentPreviewCache.cs`
- `UserSettings.cs`
- `README.md`
- `AGENTS.md`

## Validation

- `dotnet build .\CDisplayEx.CSharp.csproj -c Release --no-restore` succeeded
  with 0 warnings and 0 errors.
- A targeted non-UI mutation test created two independent
  `FileMutationCoordinator` objects for the same canonical PDF path. The second
  lease remained blocked while the first was held and completed after release.
- A targeted non-UI cache test held a normal writer lease, verified that a
  maintenance lease remained blocked, then verified maintenance completed after
  the writer released it.
- `git diff --check` succeeded; Git emitted only existing line-ending notices.

## Remaining manual verification

- Open several windows through `Ctrl+N`, Explorer associations, drag/drop, and
  command-line launches; confirm requests arrive as new windows in one process.
- Exercise simultaneous thumbnail/full-view activity and confirm the active
  window remains responsive while inactive work uses the reduced budget.
- Open the same PDF/folder in two windows, edit it in one, and confirm the other
  reloads after the source-change notification.
- Accept an update with several windows open and confirm every window closes
  before replacement and the application relaunches once.
- No automated UI interaction was performed in this session.
