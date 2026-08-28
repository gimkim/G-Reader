# Publish GitHub release 1.9.3

## Request

Commit and push the current thumbnail device-hang recovery changes, update the
product to version 1.9.3, build a new 1.9.3 MSIX, and publish GitHub release
`v1.9.3`.

## Source and version

- Updated project `Version` to `1.9.3` and updated `AssemblyVersion` and
  `FileVersion` together to `1.9.3.0`.
- Committed the thumbnail device-hang recovery changes and their diagnostic
  worklog as release source commit
  `70763c2674c61484b5e93de565f54e23f6a05190`.
- Pushed the commit to `origin/agent/fix-gpu-thumbnail-stability`.

## Release artifacts

- `Fast-Reader-Viewer-1.9.3-win-x64.zip`: 24,549,966 bytes, SHA-256
  `FD04489937353920D2EAD6BE190D5E39F1DF85A61E55F0C90225068702064714`.
- `Fast.Reader.Viewer.exe`: 66,776,545 bytes, file version `1.9.3.0`, SHA-256
  `2323850A86A8714837C87ABD0210F4253E4946EF722C940B82EE62548C18D8D5`.
- `FastReaderViewer_1.9.3.0_x64.msix`: 95,129,409 bytes, SHA-256
  `CD3D2D359755797541FC7DA89AE7D2FA5A3E2E68DC5E7CE1C45C83E647DE0FB7`.
- Published `SHA256SUMS-1.9.3.txt` covering all three binary artifacts.

## GitHub publication

- Created public, non-prerelease release **Fast Reader/Viewer 1.9.3**:
  `https://github.com/gimkim/G-Reader/releases/tag/v1.9.3`.
- The lightweight `v1.9.3` tag resolves to source commit `70763c2`.
- GitHub reported all four assets as `uploaded`, and its SHA-256 digests match
  the locally generated files.

## Validation

- `dotnet build -c Release --no-restore -t:Rebuild` passed with 0 warnings and
  0 errors after the release source commit was created.
- Rebuilt runtime ZIP, single-file EXE, and self-contained Store MSIX from
  commit `70763c2`; the EXE product version includes that commit SHA.
- Inspected the runtime ZIP: 33 entries, executable and license notices
  present, and no PDB files.
- Inspected the MSIX manifest: identity `gimkim.FastReaderViewer`, version
  `1.9.3.0`, architecture `x64`, and packaged executable present.
- `git diff --cached --check` passed before the source release commit.

## Remaining external validation

- The MSIX is intentionally unsigned locally; Microsoft Store signing and
  certification remain external steps.
- No interactive installation or follow-up reproduction of the large-library
  GPU/device-hang scenario was run during this publication session.
