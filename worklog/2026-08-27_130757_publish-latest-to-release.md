# Publish latest build to the normal release folder

## Request

- Publish the current single-process multi-window implementation to the normal
  `release` output so the executable used for testing contains the latest code.

## Action

- Ran `dotnet publish .\CDisplayEx.CSharp.csproj -c Release -o .\release --no-restore`.
- Replaced the previously published 19 August application DLL with the current
  27 August build.

## Validation

- Publish completed successfully.
- `bin\Release\net8.0-windows10.0.19041.0\win-x64\Fast Reader Viewer.dll`
  and `release\Fast Reader Viewer.dll` are both 1,015,296 bytes.
- Both DLLs have SHA-256
  `C38D38546724DF4A086991D1827B741FED024B554B37F5F27294818877214CCD`.
- No Fast Reader/Viewer process was running after publication, so the next
  launch will load the newly published DLL.

## Manual verification

- Launch `release\Fast Reader Viewer.exe` several times and verify Task Manager
  shows one application host plus only the expected PDFium worker processes
  when a PDF is active, while every launch opens a separate reader window.
