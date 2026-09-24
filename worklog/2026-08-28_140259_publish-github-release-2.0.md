# Publish GitHub Release 2.0

## Request

- Build the latest Release configuration.
- Push the current source to GitHub.
- Publish GitHub Release 2.0.
- Build a new Microsoft Store MSIX for 2.0.
- Prepare end-user release notes describing changes since 1.9.7.

## Source and version

- Updated `Version`, `AssemblyVersion`, and `FileVersion` to 2.0 / 2.0.0.0.
- Source release commit: `ccf89ccc13a1cd564515dca0d10780c9105cd992` (`Release Fast Reader Viewer 2.0`).
- Pushed branch: `origin/agent/fix-gpu-thumbnail-stability`.
- Git tag: `v2.0`, targeting the source release commit above.

## Release artifacts

| Artifact | Size (bytes) | SHA-256 |
| --- | ---: | --- |
| `Fast-Reader-Viewer-2.0-win-x64.zip` | 25,464,577 | `3659E236EC77051BDF8A6CFF0F9A8F587F98C821205E5B95E8826FA6D2CF2704` |
| `Fast.Reader.Viewer.exe` | 66,887,133 | `E8C87345C6D03415E76530FAC6774980936E3CC0F2D979EB94477F53444629E1` |
| `FastReaderViewer_2.0.0.0_x64.msix` | 95,181,164 | `8DFA470255343D8AE09D67773A35FF99AD694D9D656E386D4A7134D0A642E338` |
| `SHA256SUMS-2.0.txt` | 290 | `439AFECDE118C56730C17EA74E43632E9BBFF937C6D3882F50C16A46DF40E851` |

## GitHub publication

- Release: https://github.com/gimkim/G-Reader/releases/tag/v2.0
- Status: public, published, and not marked as a prerelease.
- Verified all four assets through the GitHub API; their reported sizes and SHA-256 digests match the local files.
- Verified the remote `v2.0` tag resolves to `ccf89ccc13a1cd564515dca0d10780c9105cd992`.

## What's new since 1.9.7

- Multi-window reading within one shared application process, including forwarding later launches to the running session.
- New-window placement based on the latest active reader window, plus `Ctrl+N`, `Ctrl+W`, and `Ctrl+Q` shortcuts.
- Shared settings, caches, schedulers, GPU/PDF admission, history, and cross-window resource prioritization while keeping each reader window independent.
- Image deletion from Thumbnail and Full View with confirmation, multi-selection, source-change safeguards, and recovery through the Windows Recycle Bin.
- Application-wide coordination for destructive source edits and cross-window refresh notifications.
- Embedded ICC profile validation with per-image sRGB fallback for malformed profiles.
- Correct Previous and End navigation in two-page layouts when Auto Single Landscape is enabled.
- Improved shared settings persistence, cache maintenance, update handling, and diagnostics for the multi-window architecture.

## Validation

- `dotnet build -c Release --no-restore`: passed with 0 warnings and 0 errors.
- Final `dotnet build -c Release --no-restore -t:Rebuild` from the committed source: passed with 0 warnings and 0 errors.
- Published EXE reports file version `2.0.0.0` and product version `2.0+ccf89ccc13a1cd564515dca0d10780c9105cd992`.
- ZIP inspected: 33 entries, executable and notices present, no PDB files.
- MSIX manifest inspected: identity `gimkim.FastReaderViewer`, version `2.0.0.0`, architecture `x64`, and packaged executable present.
- `git diff --cached --check` passed before the source release commit.

## Remaining manual validation

- The MSIX is the unsigned Store upload artifact and was not interactively installed; Microsoft Store signing occurs after certification.
- No full manual UI regression was performed for multi-window behavior, image deletion, malformed ICC rendering, or Auto Single Landscape navigation during this publication step. Prior implementation worklogs contain their targeted validation evidence.
