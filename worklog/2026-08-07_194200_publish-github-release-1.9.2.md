# Publish GitHub release 1.9.2

## Request

Publish the already committed Fast Reader/Viewer 1.9.2 build as a public
GitHub release.

## Release artifacts

- Published framework-dependent Windows x64 runtime package
  `Fast-Reader-Viewer-1.9.2-win-x64.zip` (24,549,106 bytes).
- Published framework-dependent single-file executable
  `Fast.Reader.Viewer.exe` (66,772,449 bytes, file version `1.9.2.0`).
- Published unsigned Microsoft Store upload package
  `FastReaderViewer_1.9.2.0_x64.msix` (95,128,811 bytes).
- Published `SHA256SUMS-1.9.2.txt` covering all three binaries.

## GitHub publication

- Created public, non-prerelease GitHub release `v1.9.2` titled
  **Fast Reader/Viewer 1.9.2**.
- The lightweight tag points to release source commit
  `bcb45fd2593d8907375418a2a27917ffbd54d0f7`.
- Release URL:
  `https://github.com/gimkim/G-Reader/releases/tag/v1.9.2`.
- Added end-user release notes covering the compact modern UI, responsive
  large-folder startup, background Explorer-order scanning, live listing
  status, and lazy JPEG orientation loading.

## Verification

- Rebuilt both runtime and single-file Windows x64 outputs from the tagged
  source before upload.
- Confirmed the direct executable reports file version `1.9.2.0` and is the
  66 MB single-file build rather than the 217 KB launcher.
- Inspected the ZIP: 33 runtime entries, executable and license notices
  present, and no PDB files.
- Queried the published release and confirmed all four assets have state
  `uploaded`; GitHub-reported SHA-256 digests match the local files.
- Queried the remote tag and confirmed it resolves to commit `bcb45fd`.

## Remaining external validation

- The MSIX is intentionally unsigned locally; Microsoft Store signing and
  certification remain external steps.
- No interactive installation, large-folder, archive, PDF, or GPU UI test was
  run during this publication session.
