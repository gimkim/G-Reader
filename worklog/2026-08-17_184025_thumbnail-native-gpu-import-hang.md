# Thumbnail native GPU import hang

## Symptom

- The application froze in thumbnail mode after rapidly moving between folders.
- The thumbnail area and the WinForms message pump stopped responding.
- The affected process was intentionally left running while diagnostics were captured.

## Evidence

- Process `21120` ran `release\Fast Reader Viewer.exe` version `1.9.5.0`.
- Extended diagnostics reported the UI heartbeat stalled after opening several
  small image folders in quick succession and queueing thumbnail GPU retirement.
- The captured dump
  `%LOCALAPPDATA%\Fast Reader Viewer\Diagnostics\hang-20260817-183538-pid21120.dmp`
  showed UI OS thread `0xebb4` blocked in this native call chain:
  `ID2D1DeviceContext.CreateBitmapFromDxgiSurface` ->
  `ThumbnailGridView.GetOrCreateNativeGpuTexture` ->
  `ThumbnailGridView.DrawDirect2DItem` -> `WM_PAINT`.
- The managed thread was not waiting on `_contentGate`; the unbounded wait was
  inside the Direct2D/DXGI texture import on the UI thread.

## Fix

- Removed direct native D3D-to-Direct2D imports from newly generated thumbnail
  and folder/archive-cover results.
- JPEG cover decode and resize continue to use nvJPEG/NPP, but the small result
  is staged through host memory before the existing paced Direct2D UI upload.
- Generic GPU fast-preview resize now uses the staged NPP path as well.
- Persistent preview caching and Direct2D hardware presentation remain enabled.

## Files

- `AsyncMainForm.cs`
- `BrowsePreviewRenderer.cs`

## Validation

- `dotnet build -c Release --no-restore` succeeded with 0 warnings and 0 errors.
- After the captured old process was closed, `dotnet publish -c Release -o release
  --no-restore` succeeded and refreshed the normal release output.
- `git diff --check` succeeded; only line-ending notices were emitted.
- Static call-site inspection found no remaining producer of a GPU-backed
  `GeneratedThumbnail`; full-view GPU rendering remains separate and unchanged.

## Remaining manual test

- Launch the new release build, rapidly alternate between the same folders in
  thumbnail mode, and verify covers continue to appear without a UI heartbeat
  stall.
- This session did not claim a live GPU/UI reproduction after the fix because
  the user asked to perform UI testing personally and the captured process is
  still the old running binary.
