# Moving G Reader to another Windows PC

This repository contains the complete source, project assets, dependency
declarations, license notices, and Codex project notes needed to continue work
on another machine. Build output, local settings, disk caches, and preserved
binary snapshots are intentionally not stored in Git.

## 1. Install prerequisites

- Windows 10 or Windows 11, x64
- Git for Windows
- .NET 8 SDK, x64
- Codex, signed in with the intended account
- Current NVIDIA display driver when NVIDIA acceleration will be used
- Optional CUDA runtime libraries for nvJPEG (`cudart`, `nvjpeg`, and preferably
  `nppc`/`nppif`) in the application directory, `PATH`, or a CUDA Toolkit `bin`
  directory

The CPU image path and Direct2D renderer work without CUDA. NuGet restores the
managed and packaged native dependencies declared in
`CDisplayEx.CSharp.csproj`, including PDFium, TurboJPEG, WebP, and Magick.NET.

## 2. Clone and open in Codex

```powershell
New-Item -ItemType Directory -Force "$env:USERPROFILE\source"
Set-Location "$env:USERPROFILE\source"
git clone https://github.com/gimkim/G-Reader.git "G Reader"
Set-Location "G Reader"
```

Open that `G Reader` directory as the workspace in Codex. `AGENTS.md` contains
the project map, validation commands, responsiveness requirements, and rules
for preserved versions.

## 3. Restore, validate, and publish

```powershell
dotnet restore .\CDisplayEx.CSharp.csproj
dotnet build .\CDisplayEx.CSharp.csproj -c Release --no-restore
dotnet publish .\CDisplayEx.CSharp.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\release
```

Run `release\G Reader.exe`. This is a framework-dependent publish, so the .NET
8 Desktop Runtime must remain installed on machines that only run the published
application. A development machine with the .NET 8 SDK already satisfies this.

## 4. Move optional local state

To retain hotkeys, performance tuning, cache limits, window state, and saved
reading positions, close G Reader and copy:

```text
%APPDATA%\G Reader\settings.json
```

The persistent preview cache defaults to:

```text
%LOCALAPPDATA%\G Reader\PreviewCache
```

Copying the cache is optional and can be very large. It is safe to omit it; G
Reader regenerates previews as needed. If a custom cache path was configured,
either copy that directory too or change the path after the first launch.

## Repository boundaries

The following local directories are excluded from Git and are not restored by
cloning:

- `bin`, `obj`, and `release` build output
- `versions` and local preserved binary snapshots
- `%APPDATA%` settings and `%LOCALAPPDATA%` preview caches
- Codex conversation history

Source files, README documentation, license notices, and `AGENTS.md` do move
with the repository.
