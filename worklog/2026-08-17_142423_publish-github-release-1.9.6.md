# Publish GitHub release 1.9.6

## Request

Build the latest Release configuration, commit and push the current changes,
update the product to version 1.9.6, build a new 1.9.6 MSIX, publish GitHub
release `v1.9.6`, and write release notes covering changes since 1.9.5.

## Source and version

- Updated project `Version` to `1.9.6` and updated `AssemblyVersion` and
  `FileVersion` together to `1.9.6.0`.
- Committed the Settings numeric mouse-wheel guard and its worklog as release
  source commit `bd94c21f173a6f1840e613dbdf67761533754f0e`.
- Pushed the commit to `origin/agent/fix-gpu-thumbnail-stability`.

## Release artifacts

- `Fast-Reader-Viewer-1.9.6-win-x64.zip`: 25,443,458 bytes, SHA-256
  `BD2ADAD4BB83E3C9B83C1764E6E049559484264E392676188399EC845BD0507B`.
- `Fast.Reader.Viewer.exe`: 66,801,121 bytes, file version `1.9.6.0`, SHA-256
  `9302E55F16CA2EB24E7744ED2C6089DE4703C7D6F322ACA39DD054C841229374`.
- `FastReaderViewer_1.9.6.0_x64.msix`: 95,159,465 bytes, SHA-256
  `364C91245738F74053DEF5ABCE226DF85D8CFA35448B035292F7F0D564336BDC`.
- Published `SHA256SUMS-1.9.6.txt` covering all three binary artifacts.

## GitHub publication

- Created public, non-prerelease release **Fast Reader/Viewer 1.9.6**:
  `https://github.com/gimkim/G-Reader/releases/tag/v1.9.6`.
- The lightweight `v1.9.6` tag resolves to source commit `bd94c21`.
- GitHub reported all four assets as `uploaded`, and its SHA-256 digests match
  the locally generated files.
- Release notes use the requested **What's New Since Version 1.9.5** scope and
  describe the safer numeric Settings mouse-wheel behavior.

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors
  before the source commit.
- `dotnet build -c Release --no-restore -t:Rebuild` passed with 0 warnings and
  0 errors after the release source commit was created.
- Rebuilt runtime ZIP, single-file EXE, and self-contained Store MSIX from
  commit `bd94c21`; the EXE product version includes that commit SHA.
- Inspected the runtime ZIP: 33 entries, executable and license notices
  present, and no PDB files.
- Inspected the MSIX manifest: identity `gimkim.FastReaderViewer`, version
  `1.9.6.0`, architecture `x64`, and packaged executable present.
- `git diff --cached --check` passed before the source release commit.

## Remaining external validation

- The MSIX is intentionally unsigned locally; Microsoft Store signing and
  certification remain external steps.
- No interactive installation or manual Settings wheel test was run during
  this publication session.
