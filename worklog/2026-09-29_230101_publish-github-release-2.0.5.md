# Publish Fast Reader/Viewer 2.0.5

## Request and source
- Build/push latest source, release 2.0.5 on GitHub, create MSIX, and write What's New relative to 2.0.4.
- Version 2.0.5; assembly/file/MSIX 2.0.5.0.
- Source commit and remote v2.0.5 tag: c644537b75740e81186b47cdfc14bb349f7ca499.
- Branch: agent/fix-gpu-thumbnail-stability.

## Changes since 2.0.4
- Click the left/right half of the full-view reading area to navigate in that physical direction through existing LTR/RTL and spread navigation.
- Single clicks defer to the Windows double-click interval to preserve zoom; dragging, release outside, and other buttons do not turn pages.
- Seven targeted handler checks passed during implementation, documented in 2026-09-29_224131_click-half-page-navigation.md. Real mouse/fullscreen/RTL interaction remains a manual check.

## Build and publication evidence
- Initial Release build and committed-source rebuild passed, 0 warnings/errors.
- Normal release publish, isolated runtime/single-file publishes, and MSIX builder passed.
- Normal release DLL and standalone EXE ProductVersion: 2.0.5+c644537b75740e81186b47cdfc14bb349f7ca499.
- ZIP inspected: 33 entries, executable and license notices present, no PDB.
- MSIX identity gimkim.FastReaderViewer, version 2.0.5.0, x64, executable verified against URI-decoded entry.
- git diff --check and staged diff check passed.
- Published https://github.com/gimkim/G-Reader/releases/tag/v2.0.5 (not draft/prerelease); latest API returns v2.0.5.
- Remote tag target verified; all four asset sizes and GitHub SHA-256 digests match local files, including checksum file.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| Fast.Reader.Viewer.exe | 66895329 | F2E0F6F7808F2A56018B52618A4E4D29A57A6F6ABBFB4729C8A45397A4D84DD9 |
| Fast-Reader-Viewer-2.0.5-win-x64.zip | 25468220 | CC74562402395A66F77DE1775C4F82D58EB99812E14AD45A74543F67C69B5C29 |
| FastReaderViewer_2.0.5.0_x64.msix | 95260769 | F602F9FA7A08652A388487523F4DB214BCE68F4548E8FCF0B07102A694593316 |

## Remaining checks
- MSIX is unsigned for Store upload; no interactive installation or Partner Center submission performed.
- No end-to-end mouse/UI regression performed during packaging.
