# Publish Fast Reader/Viewer 2.0.2

## Request and source
- Build/push current source, publish GitHub 2.0.2 and new MSIX, and write What's New relative to 2.0.1.
- Version: 2.0.2; assembly/file/MSIX version: 2.0.2.0.
- Source commit and remote v2.0.2 tag: f698c61eeddac81641b32be5b8a9739f2252b8cb.
- Branch: agent/fix-gpu-thumbnail-stability.
- Included Book.cs and AsyncMainForm.cs adjacent-book lookup optimization and its two existing worklogs.

## What's new since 2.0.1
- Faster next/previous archive, PDF, and folder discovery, particularly in large NAS/network libraries.
- Preserves sorting/grouping and refreshes directory contents for each navigation request.
- Cancels stale discovery when switching books or closing windows.
- Optional diagnostics now include adjacent-lookup timing.
- Prior implementation evidence: warm NAS discovery improved from 2334-2442 ms to 20-104 ms; 14 sort/direction comparisons and cancellation passed. These measurements are not end-to-end UI timings and were not repeated during packaging.

## Validation and publication
- Initial Release build and final committed-source rebuild passed with 0 warnings/errors.
- Normal release publish, isolated framework-dependent runtime/single-file publishes, and Store builder passed.
- EXE ProductVersion: 2.0.2+f698c61eeddac81641b32be5b8a9739f2252b8cb.
- ZIP contains 33 entries, executable and license notices, no PDB.
- MSIX identity gimkim.FastReaderViewer, x64, version 2.0.2.0. Executable is present as the URI-escaped ZIP entry Fast%20Reader%20Viewer.exe and correctly referenced by the manifest.
- git diff --check and staged diff check passed.
- Published https://github.com/gimkim/G-Reader/releases/tag/v2.0.2; not draft/prerelease, latest endpoint returns v2.0.2.
- All four uploaded asset sizes and GitHub SHA-256 digests match local files.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| Fast.Reader.Viewer.exe | 66887137 | B8DFCB62209380FD22B66F8D2C436C46CD138F06BF89A1D59F2072ED0C1972F1 |
| Fast-Reader-Viewer-2.0.2-win-x64.zip | 25466029 | BBDCD4125E017C1A5E9F3CE783AF19A46DCD89A492DBE76FDE054F594C532188 |
| FastReaderViewer_2.0.2.0_x64.msix | 95257786 | B089E3A676CE559156B3DE2B4E8B1B7E414C9B4FB9F591A75D43D7A2B9D5EBC7 |

## Remaining manual checks
- Manual next/previous archive UI navigation remains outstanding.
- MSIX is unsigned for Store submission; no interactive installation or Store submission performed.
