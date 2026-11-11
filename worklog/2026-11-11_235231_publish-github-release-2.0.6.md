# Publish Fast Reader/Viewer 2.0.6

## Request and source

- User requested a new version released on GitHub after the DPI crop fix.
- Version 2.0.6; assembly/file/MSIX version 2.0.6.0.
- Source commit and annotated v2.0.6 tag target:
  `b6dd7cf8f72c6caad0c77afac2baf1d092f5544b`.
- Branch: `agent/fix-gpu-thumbnail-stability`; pushed source and tag atomically.
- Release notes describe the image DPI crop fix, automatic image/archive preview
  regeneration, preserved PDF caches, transparency, and validation limits.

## Build and verification

- Release build before and after the source commit: passed, 0 warnings/errors.
- Re-ran 47 bitmap and 28 cache regression checks after the version bump: all
  75 passed. Detailed scope and reproduction evidence are in
  `2026-11-11_234814_fix-dpi-cropped-reading-images.md`.
- Normal `dotnet publish -c Release -o release --no-restore` could not replace
  `release/Fast Reader Viewer.dll`: the user's running reader PID 71740 held it
  open. No process was terminated. The running/local normal-publish reader was
  not upgraded; it remains the previous 2.0.5 build.
- Isolated framework-dependent runtime and single-file publishes passed under
  `release/staging-2.0.6-b6dd7cf-runtime` and `-single`.
- The updater's required asset `release/Fast.Reader.Viewer.exe` was replaced
  with the new verified single-file output, not the small apphost.
- Single-file EXE ProductVersion:
  `2.0.6+b6dd7cf8f72c6caad0c77afac2baf1d092f5544b`.
- Portable ZIP: 33 files, executable/DLL and four license/notice files present;
  no PDBs, old release assets, settings, or caches. DLL assembly and product
  versions match the source commit.
- Store builder passed. MSIX identity `gimkim.FastReaderViewer`, assigned publisher
  matching the manifest template, version 2.0.6.0, x64, and URI-decoded executable
  entry verified. Packaged DLL version/source commit match.
- `git diff --check` and staged diff check passed.

## Publication

- Published https://github.com/gimkim/G-Reader/releases/tag/v2.0.6 as a normal
  release, not draft/prerelease. `/releases/latest` returns v2.0.6.
- Created a draft with all four assets, compared their sizes and GitHub SHA-256
  digests with local files, then published it as latest. Published API metadata
  confirms the same assets and exact source target.
- Verified remote annotated tag dereferences to the source commit above.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| Fast.Reader.Viewer.exe | 66895329 | 7828d967b611a9516ae51c1edaf256e0a8e7561b7707e7d276ca580cfd9fb46b |
| Fast-Reader-Viewer-2.0.6-win-x64.zip | 25468789 | b3a8e951e4ea685c69f095485728739d3d8c2169615dbf596d0624260c79e031 |
| FastReaderViewer_2.0.6.0_x64.msix | 95291574 | 04c1ade7c029160a7f5f3e9f2a64bd1298ba440ba7bf24604ca0aff8fb160705 |
| SHA256SUMS-2.0.6.txt | 292 | 99f8f84567fe42db173efd1c2b5e1870e11da9c878ea2d3bf65fb4f9cac9fb5f |

## Remaining manual checks

- Real reading-mode GPU/UI verification with the original affected files remains
  unperformed. No new archive/PDF/multi-window UI reproduction is claimed.
- EXE/ZIP require the .NET 8 Desktop Runtime on Windows x64. The MSIX is unsigned
  for Store upload; no installation or Partner Center submission was performed.
