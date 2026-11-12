# Publish Fast Reader/Viewer 2.0.3

## Request and source
- Build/push current source, publish GitHub 2.0.3 and new MSIX, and write What's New relative to 2.0.2.
- Version: 2.0.3; assembly/file/MSIX version: 2.0.3.0.
- Source commit and remote v2.0.3 tag: e4b7a147687edd970471b07f4ad659178b47fb52.
- Branch: agent/fix-gpu-thumbnail-stability.

## Changes since 2.0.2
- Settings sections and explanations grow with wrapped text instead of fixed row heights.
- Narrow windows stack labels above editors; checkbox captions wrap correctly.
- Buttons, progress bars, tab headers, Hotkeys and footer adapt to available space.
- Window fitting uses the current monitor; bounded text measurement caching and deferred construction layout reduce repeated work.
- Scope and implementation validation recorded in 2026-09-22_233915_settings-responsive-layout.md: 72 scaling/geometry cases and 18 dynamic text/resize cases passed. These were simulated control/font scales, not physical mixed-DPI monitor transitions.

## Build and artifact verification
- Initial Release build and final committed-source rebuild passed with 0 warnings/errors.
- Normal release publish, isolated framework-dependent runtime/single-file publishes, and Store builder passed.
- EXE ProductVersion: 2.0.3+e4b7a147687edd970471b07f4ad659178b47fb52.
- ZIP contains 33 entries, executable and license notices, no PDB.
- MSIX identity gimkim.FastReaderViewer, x64, version 2.0.3.0; manifest executable verified against URI-decoded package entries.
- git diff --check and staged diff check passed (line-ending notices only).

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| Fast.Reader.Viewer.exe | 66891233 | C5634A8D1C18F047F9DCA0D66114C684A427686AD26E3BCA667CAA6BA20FC0F7 |
| Fast-Reader-Viewer-2.0.3-win-x64.zip | 25467260 | 39B844FAE67B8A9834AE8389E63CD46D0F2E81375C14FE1F39D49C0753D98C35 |
| FastReaderViewer_2.0.3.0_x64.msix | 95259323 | 9541197BEC250E78F22168CA7E1BED2FF8C0E448B61BDB13C41AF1CBFA6C94F1 |

## Publication and remaining checks
- Published https://github.com/gimkim/G-Reader/releases/tag/v2.0.3, not draft/prerelease.
- Latest release API returns v2.0.3; remote tag points at source commit above.
- All four uploaded asset sizes and SHA-256 digests match local files, including SHA256SUMS-2.0.3.txt.
- MSIX is unsigned for Store submission; no interactive installation or Partner Center submission performed.
- Physical mixed-DPI monitor transition and the user's affected machine remain manual checks.
