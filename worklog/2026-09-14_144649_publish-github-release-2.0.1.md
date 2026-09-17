# Publish GitHub Release 2.0.1

## Request

- Build the latest Release configuration.
- Push the current source to GitHub.
- Publish GitHub Release 2.0.1.
- Build a new Microsoft Store MSIX for 2.0.1.
- Prepare end-user release notes describing changes since 2.0.

## Source and version

- Updated `Version` to `2.0.1` and `AssemblyVersion` / `FileVersion` to `2.0.1.0`.
- Source release commit: `27304960c926848c8102e6faeced78623e3d85ca` (`Release Fast Reader Viewer 2.0.1`).
- Pushed branch: `origin/agent/fix-gpu-thumbnail-stability`.
- Git tag: `v2.0.1`, targeting the source release commit above.

## Changes since 2.0

- Fixed jagged white or bright fringes around transparent PNG artwork at fractional zoom and on dark backgrounds.
- Standardized premultiplied-alpha handling across CPU decoding, Direct2D presentation, GPU uploads, thumbnails, Full View, animated images, contact sheets, and color-management effects.
- Preserved transparent edges through the Lanczos resize path while keeping its alpha-aware straight-alpha input contract.
- Changed persistent page previews for transparency-capable formats to lossless PNG; opaque images and PDFs retain their existing JPEG cache identities.

## Release artifacts

| Artifact | Size (bytes) | SHA-256 |
| --- | ---: | --- |
| `Fast-Reader-Viewer-2.0.1-win-x64.zip` | 25,465,478 | `68B10114E95649AA8BE08CE75D32D87F8C48B2149B86C9AAD7125F4ABEDC6E13` |
| `Fast.Reader.Viewer.exe` | 66,887,137 | `FB94750C93A473F7B88C94E5C7621EB10A5FE119AAA64013F0890A4C0D19BBAA` |
| `FastReaderViewer_2.0.1.0_x64.msix` | 95,256,923 | `AAFD9B5004FCBABB6DB2E2DF7D17346D279FE91536C1F2C82466A87B45A2F584` |
| `SHA256SUMS-2.0.1.txt` | 292 | `5F6E4B775F406430C10589F68F923992447A32CE6597AEA70EE2E08156629535` |

## GitHub publication

- Release: https://github.com/gimkim/G-Reader/releases/tag/v2.0.1
- Status: public, published, and not marked as a prerelease.
- Verified all four assets through the GitHub API; reported sizes and SHA-256 digests match the local files.
- Verified the remote `v2.0.1` tag resolves to `27304960c926848c8102e6faeced78623e3d85ca`.

## Validation

- `dotnet build -c Release --no-restore`: passed with 0 warnings and 0 errors after the version bump.
- Final `dotnet build -c Release --no-restore -t:Rebuild` from the committed source: passed with 0 warnings and 0 errors.
- `dotnet publish -c Release -o release --no-restore`: passed.
- Published EXE reports file version `2.0.1.0` and product version `2.0.1+27304960c926848c8102e6faeced78623e3d85ca`.
- ZIP inspected: 33 entries, executable and license notices present, no PDB files. The first ZIP contained a PDB and was rebuilt after removing it from the isolated 2.0.1 staging directory.
- MSIX manifest inspected: identity `gimkim.FastReaderViewer`, version `2.0.1.0`, architecture `x64`, and packaged executable present.
- `git diff --check` and `git diff --cached --check` passed before the source release commit, apart from expected line-ending notices.
- The implementation worklog records a targeted run of the actual Lanczos thumbnail path against the reported 4000 x 6000 transparent PNG; the resulting preview retained premultiplied alpha and showed no reported white fringe when composited over the reader background.

## Remaining manual validation

- The MSIX is the unsigned Store upload artifact and was not interactively installed; Microsoft Store signing occurs after certification.
- No automated UI interaction was performed. Manual checking of the reported PNG in Full View, multiple zoom levels, Thumbnail view, and after persistent-cache reload remains outstanding.
