# Publish GitHub release 1.9.5

## Request

Commit and push the current Fast Reader/Viewer changes, update the product to
version 1.9.5, build a new 1.9.5 MSIX, publish GitHub release `v1.9.5`, and
write release notes covering everything added since version 1.9.2.

## Source and version

- Updated project `Version` to `1.9.5` and updated `AssemblyVersion` and
  `FileVersion` together to `1.9.5.0`.
- Committed the independent PDF thumbnail lane, redundant PDF resize removal,
  and settings explanations as release source commit
  `3eab2f5597ce3df6c4bd81067faa3b13c14ad8b1`.
- Pushed the commit to `origin/agent/fix-gpu-thumbnail-stability`.

## Release artifacts

- `Fast-Reader-Viewer-1.9.5-win-x64.zip`: 25,443,194 bytes, SHA-256
  `BD47D5DB73141155A65B456BDDA757621D9B7F92CB95CD13A66FC958EFD6B7DE`.
- `Fast.Reader.Viewer.exe`: 66,801,121 bytes, file version `1.9.5.0`, SHA-256
  `5252508483DED9483BF80814C6BD967F293720E203E6C6CE98ED08EE32FC85C7`.
- `FastReaderViewer_1.9.5.0_x64.msix`: 95,159,144 bytes, SHA-256
  `F0DC6BAA900EBDFAC37484D63E44697E362C1A16B62733732402F2F51657EE86`.
- Published `SHA256SUMS-1.9.5.txt` covering all three binary artifacts.

## GitHub publication

- Created public, non-prerelease release **Fast Reader/Viewer 1.9.5**:
  `https://github.com/gimkim/G-Reader/releases/tag/v1.9.5`.
- The lightweight `v1.9.5` tag resolves to source commit `3eab2f5`.
- GitHub reported all four assets as `uploaded`, and its SHA-256 digests match
  the locally generated files.
- Release notes use the requested **What's New Since Version 1.9.2** scope and
  cover the 1.9.3 device-recovery changes, the 1.9.4 interface/zoom/progress/GPU
  changes, and the new 1.9.5 PDF thumbnail lane.

## Validation

- `dotnet build -c Release --no-restore` passed before the source commit.
- `dotnet build -c Release --no-restore -t:Rebuild` passed with 0 warnings and
  0 errors after the release source commit was created.
- Rebuilt runtime ZIP, single-file EXE, and self-contained Store MSIX from
  commit `3eab2f5`; the EXE product version includes that commit SHA.
- Inspected the runtime ZIP: 33 entries, executable and license notices
  present, and no PDB files.
- Inspected the MSIX manifest: identity `gimkim.FastReaderViewer`, version
  `1.9.5.0`, architecture `x64`, and packaged executable present.
- `git diff --cached --check` passed before the source release commit.

## Remaining external validation

- The MSIX is intentionally unsigned locally; Microsoft Store signing and
  certification remain external steps.
- No interactive installation, PDF throughput comparison, or complete manual
  UI regression covering all changes since 1.9.2 was run during publication.
