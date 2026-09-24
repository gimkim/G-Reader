# Publish GitHub release 1.9.4

## Request

Commit and push the current Fast Reader/Viewer changes, update the product to
version 1.9.4, build a new 1.9.4 MSIX, and publish GitHub release `v1.9.4`.

## Source and version

- Updated project `Version` to `1.9.4` and updated `AssemblyVersion` and
  `FileVersion` together to `1.9.4.0`.
- Committed the thumbnail filter, Explorer/Random scan progress, thumbnail GPU
  saturation recovery, continuous thumbnail progress, and unified dual-page
  zoom changes as release source commit
  `50cdc1d2c7231025bbacd2ff41afc732fa7bb73e`.
- Pushed the commit to `origin/agent/fix-gpu-thumbnail-stability`.

## Release artifacts

- `Fast-Reader-Viewer-1.9.4-win-x64.zip`: 25,442,737 bytes, SHA-256
  `A2B48A037E28C562ABA493D269AB0DA51671A6A5FE27816BB1913FBF12471BCA`.
- `Fast.Reader.Viewer.exe`: 66,801,121 bytes, file version `1.9.4.0`, SHA-256
  `02DA0189624CB15627F0BAFA3FAB8556D1610A7D319156CD0FADAB27C4476249`.
- `FastReaderViewer_1.9.4.0_x64.msix`: 95,158,377 bytes, SHA-256
  `1C2392341EEB5D4BACA6AD7FEA56D10EC3953DD994D31909446FD19C153058C9`.
- Published `SHA256SUMS-1.9.4.txt` covering all three binary artifacts.

## GitHub publication

- Created public, non-prerelease release **Fast Reader/Viewer 1.9.4**:
  `https://github.com/gimkim/G-Reader/releases/tag/v1.9.4`.
- The lightweight `v1.9.4` tag resolves to source commit `50cdc1d`.
- GitHub reported all four assets as `uploaded`, and its SHA-256 digests match
  the locally generated files.

## Validation

- `dotnet build -c Release --no-restore` passed before the source commit.
- `dotnet build -c Release --no-restore -t:Rebuild` passed with 0 warnings and
  0 errors after the release source commit was created.
- Rebuilt runtime ZIP, single-file EXE, and self-contained Store MSIX from
  commit `50cdc1d`; the EXE product version includes that commit SHA.
- Inspected the runtime ZIP: 33 entries, executable and license notices
  present, and no PDB files.
- Inspected the MSIX manifest: identity `gimkim.FastReaderViewer`, version
  `1.9.4.0`, architecture `x64`, and packaged executable present.
- `git diff --cached --check` passed before the source release commit.

## Remaining external validation

- The MSIX is intentionally unsigned locally; Microsoft Store signing and
  certification remain external steps.
- No interactive installation or complete manual UI regression covering the
  five 1.9.4 feature/fix worklogs was run during this publication session.
