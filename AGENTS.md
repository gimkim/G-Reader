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

## Historical utility

`legacy-tools\enable-slider-default.ps1` is the earlier binary-patching utility from before the C# rewrite. It is retained only as project history.
