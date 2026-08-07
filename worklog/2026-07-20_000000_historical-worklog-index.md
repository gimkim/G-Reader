# Fast Reader/Viewer historical worklog index

This index and the files next to it were backfilled from the complete chat
history available in this task and the repository's Git history. Commit times
are used where they are available. Some Partner Center questions and manual
UI reports did not have a separate commit timestamp; those entries are marked
as reconstructed from chat and do not claim an additional code change.

## Chronological coverage

- `2026-07-20_164331_repository-bootstrap.md` — repository publication,
  cloning/migration, environment preparation.
- `2026-07-21_001932_reader-foundation.md` — core reader, layouts, scrolling,
  hotkeys, and initial feature set (`422fab2`, `4a9b393`).
- `2026-07-21_025826_preview-cache.md` — accelerated previews and persistent
  thumbnail caches (`0413ce7`).
- `2026-07-21_223823_gpu-webp-library.md` — GPU rendering, large libraries,
  animated WebP, archive/folder performance (`8189546`, `659f37d`).
- `2026-07-22_011327_position-pdfium.md` — remembered positions, spreads, and
  isolated PDFium workers (`923663b`, `c5de9ae`).
- `2026-07-22_051313_adaptive-tuning-gpu.md` — adaptive workers, settings,
  migration notes, and GPU thumbnail stability (`8c8da28`, `b89d739`).
- `2026-07-22_061426_pdf-cold-cache-covers.md` — PDF memory/crash fixes,
  folder covers, viewport resizing, and cold-cache behavior (`6e2bd4c`,
  `2c7e970`, `4e517e0`, `72bd1c8`).
- `2026-07-22_080000_pdf-editing-selection-cache.md` — PDF page deletion,
  multi-selection, copy actions, and cache remapping reconstructed from chat.
- `2026-07-22_175000_release-1.1-device-recovery.md` — 1.1 handoff and
  thumbnail device-loss recovery (`e44cd54`, `11cc195`).
- `2026-07-22_200328_release-1.2-to-1.4.md` — releases 1.2–1.4, DPI-safe
  settings, and full-view GPU recovery (`ef4c0b4`, `fd78aab`, `6fc8af8`,
  `033e18c`, `221668c`).
- `2026-07-22_221500_release-1.0-msix-and-store-prep.md` — 1.0 packaging,
  executable/ZIP distinction, app rename, signing, and MSIX preparation.
- `2026-07-22_223000_store-partner-center.md` — reconstructed Store listing
  form guidance: identity, categories, declarations, requirements, ratings,
  privacy, assets, keywords, descriptions, and restricted capabilities.
- `2026-07-23_023654_release-1.6-gpu-pdf-stability.md` — PDF cleanup,
  serialized GPU retirement, recovery, diagnostics, and pipeline hardening
  (`9108fa0`, `bf656f2`, `955e6ca`, `7243571`, `5ad1fd5`).
- `2026-07-25_002535_release-1.7-cache-priority.md` — release 1.7, cache
  warm-up priorities, selection debounce, and per-file progress.
- `2026-07-26_152503_exif-orientation-package.md` — EXIF orientation and
  package/security hardening reconstructed from the diagnostic worklog.
- `2026-07-27_220720_release-1.8-pdf-cover.md` — release 1.8 and PDF/archive
  cover performance work (`e25abc9`).
- `2026-07-29_153835_project-orientation.md` — later-machine/path orientation
  and instruction verification reconstructed from the continuation session.
- `2026-07-31_081936_release-1.9-history-fullscreen.md` — history popup and
  fullscreen overlays (`f07fa43`).
- `2026-08-02_233056_archive-cancellation-audit.md` — archive/cache cancellation
  and bounded native decode audit reconstructed from the diagnostic summary.
- Existing `2026-08-03_*` files — 1.9.1 GPU/D2D hang fix, packaging, release
  assets, project notes, and removal of obsolete local-version rules.

The current runtime and Store artifacts remain outside Git except when
intentionally attached to a GitHub release.
