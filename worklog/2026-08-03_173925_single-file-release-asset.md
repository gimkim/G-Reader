# Worklog — 2026-08-03 17:39:25 ICT

## Scope

Corrected the GitHub direct-download asset so it matches the project's normal
single-file release convention.

## Change

- Published the framework-dependent single-file Windows x64 executable with
  `dotnet publish -c Release -r win-x64 --self-contained false
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`.
- Copied the verified `1.9.1.0` result to `release\Fast.Reader.Viewer.exe`.
  This is the standalone direct-download asset; the runtime ZIP remains the
  dependency-inclusive package and the MSIX remains the Store upload package.
- Recomputed `release\SHA256SUMS-1.9.1.txt` and replaced the direct EXE and
  checksum assets on GitHub release `v1.9.1`.

## Validation

The direct executable is 66,760,161 bytes and reports file version `1.9.1.0`.
The release still contains the same 1.9.1 ZIP and MSIX. No UI test was run by
the assistant; the large-archive/GPU regression should be exercised manually.
