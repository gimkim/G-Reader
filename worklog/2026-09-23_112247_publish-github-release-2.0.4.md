# Publish Fast Reader/Viewer 2.0.4

## Request and source
- Build latest source, push GitHub, publish 2.0.4 with MSIX and What's New relative to 2.0.2.
- Version 2.0.4; assembly/file/MSIX version 2.0.4.0.
- Source commit/tag v2.0.4: b5eb71ddff855d00079e18e47e7d49e8e29929d3.
- Branch: agent/fix-gpu-thumbnail-stability.

## Changes since 2.0.2
- Includes 2.0.3 responsive Settings layout: content-sized rows, wrapping checkbox captions/descriptions, stacked labels/editors on narrow windows, adaptable buttons/progress/tabs/footer, monitor-aware sizing, reduced repeated layout work.
- Open Random now retains candidate lists per normalized root for the lifetime of the application instance and shares them across windows. Repeat requests avoid recursive rescans; restart the instance to refresh added/removed books.
- Failed/cancelled scans are not cached; concurrent requests for a root are serialized.
- Prior implementation evidence: 72 Settings geometry/scaling cases plus 18 dynamic resize/text cases, and seven cache behavior checks passed. No repeated UI/NAS timing test in the release step.

## Build and verification
- Initial Release build and committed-source rebuild passed with 0 warnings/errors.
- Normal release publish failed because the reader started again during the task and PID 45096 locked release/Fast Reader Viewer.dll. No process was terminated. User was asked to close the reader; it was still running at final check.
- Isolated runtime and single-file publishes and Store MSIX build succeeded from the source commit.
- EXE ProductVersion: 2.0.4+b5eb71ddff855d00079e18e47e7d49e8e29929d3.
- ZIP has 33 entries, executable/licenses present, no PDB.
- MSIX: gimkim.FastReaderViewer, 2.0.4.0, x64; manifest executable matches URI-decoded package entry.
- git diff --check and staged diff check passed.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| Fast.Reader.Viewer.exe | 66891233 | 6807EB721E35E23A6E705D31CFB842E6922FE43714B7FEFE5754E29D459A797C |
| Fast-Reader-Viewer-2.0.4-win-x64.zip | 25467633 | 9C76BD2AC0756772CA3997FA324EDA4BDEF58A93880E45D9501553A45EE41107 |
| FastReaderViewer_2.0.4.0_x64.msix | 95259892 | 38F164612869E0A1C4D2DC8EACDEC36F1DD73BEAB1B41E6B4582082CB58A9119 |

## Publication and remaining local delivery
- https://github.com/gimkim/G-Reader/releases/tag/v2.0.4 is published, not draft/prerelease; latest endpoint returns v2.0.4.
- Remote tag target verified; four asset sizes and SHA-256 digests match local files, including checksum file.
- New standalone EXE is release/Fast.Reader.Viewer.exe; runtime directory is release/staging-2.0.4-runtime.
- Normal release/Fast Reader Viewer.dll remains 2.0.3 until the running application is closed and normal publish is retried.
- MSIX is unsigned for Store submission; no installation or Partner Center submission performed. Physical mixed-DPI and NAS UI checks remain manual.
