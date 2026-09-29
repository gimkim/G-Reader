# Publish GitHub release 1.9.7

## Request

Build the latest Release configuration, commit and push the current changes,
update the product to version 1.9.7, build a new 1.9.7 MSIX, publish GitHub
release `v1.9.7`, and write release notes covering changes since 1.9.6.

## Source and version

- Updated project `Version` to `1.9.7` and updated `AssemblyVersion` and
  `FileVersion` together to `1.9.7.0`.
- Committed the safe staged thumbnail GPU upload change and diagnostic worklog
  as release source commit `3d1131b71c0cdb100fadba7d6ec01ac9cd5be4f8`.
- Pushed the commit to `origin/agent/fix-gpu-thumbnail-stability`.

## Release artifacts

- `Fast-Reader-Viewer-1.9.7-win-x64.zip`: 25,443,131 bytes, SHA-256
  `174B4720EE8F7730A2482BBC60DC84BF0354105C2A0315B490AA193C0F3F0044`.
- `Fast.Reader.Viewer.exe`: 66,801,121 bytes, file version `1.9.7.0`, SHA-256
  `E716E76CA78EC9C09F31DDB1F11079B9FFD5EE0961966B55C301C1887CB0998C`.
- `FastReaderViewer_1.9.7.0_x64.msix`: 95,159,016 bytes, SHA-256
  `0FE710424D976307EDC06AB03326BF40F37B2F4D085EC6CB0A69B119E83F9BFE`.
- Published `SHA256SUMS-1.9.7.txt` covering all three binary artifacts.

## GitHub publication

- Created public, non-prerelease release **Fast Reader/Viewer 1.9.7**:
  `https://github.com/gimkim/G-Reader/releases/tag/v1.9.7`.
- The lightweight `v1.9.7` tag resolves to source commit `3d1131b`.
- GitHub reported all four assets as `uploaded`, and its SHA-256 digests match
  the locally generated files.
- Release notes use the requested **What's New Since Version 1.9.6** scope and
  describe the safer staged GPU-to-Direct2D thumbnail upload path.

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors
  before the source commit.
- `dotnet build -c Release --no-restore -t:Rebuild` passed with 0 warnings and
  0 errors after the release source commit was created.
- Rebuilt runtime ZIP, single-file EXE, and self-contained Store MSIX from
  commit `3d1131b`; the EXE product version includes that commit SHA.
- Inspected the runtime ZIP: 33 entries, executable and license notices
  present, and no PDB files.
- Inspected the MSIX manifest: identity `gimkim.FastReaderViewer`, version
  `1.9.7.0`, architecture `x64`, and packaged executable present.
- `git diff --cached --check` passed before the source release commit.

## Remaining external validation

- The MSIX is intentionally unsigned locally; Microsoft Store signing and
  certification remain external steps.
- No interactive installation or live reproduction of the rapid-folder
  thumbnail hang was run against the 1.9.7 artifacts during publication.
- The older normal release process was left running and was not terminated;
  final GitHub artifacts were built from isolated staging directories.
