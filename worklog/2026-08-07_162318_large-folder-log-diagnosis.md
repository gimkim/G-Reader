# Worklog - 2026-08-07 16:23:18 ICT

## Symptom and log evidence

The latest extended logs showed multiple processes started with `--explorer`
for images in the large library. The affected sessions stayed at
`context=UI not attached` and never wrote `Open requested`, so they had not
reached `Book.Open` or image rendering. The log's process was the stale
`release\Fast Reader Viewer.exe` from 2026-08-03; the source changes had only
been built into `bin\Release` on 2026-08-07.

The folder-size-dependent blocker in that binary was the synchronous
`ExplorerViewOrder.TryCaptureFor` call before `Application.Run`, which walks
the entire Explorer `Folder.Items` collection. This is separate from image
rendering.

## Changes and delivery

- Kept Explorer order capture after `Shown`, on a cancellable STA worker, with
  visible listing progress in `AsyncMainForm`.
- Added a shared-D3D startup guard: Direct2D controls no longer create the D3D11
  device in their constructors or wait for it from first paint. Device creation
  is warmed asynchronously and GPU controls are invalidated when ready. This
  protects the remaining renderer startup path if a display driver blocks,
  without disabling the normal GPU rendering path after initialization.
- Ensured nvJPEG/NPP waits for the shared device from its existing background
  initializer before attempting CUDA-D3D binding.
- Published the current fixed binary to `release\` so the registered Explorer
  association uses the same code as the source and current `bin\Release` build.

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `dotnet publish -c Release -o release --no-restore` passed.
- `release\Fast Reader Viewer.exe` timestamp is 2026-08-07 16:20:47.
- `git diff --check` passed (only the repository's existing LF/CRLF notices).
- Manual validation remains: close old stuck instances, launch an image from
  the large folder through Explorer, and confirm the new release window appears
  before the folder-order/listing progress completes.
