# Worklog — 2026-08-02 23:30:56 ICT (historical reconstruction)

## Scope

Archive thumbnail cancellation and bounded native decode audit associated with
the large-folder/WebP freeze work.

## Work recorded

- Kept `ArchiveFactory.Open`/dispose and native teardown outside shared pool
  locks so opening a new archive cannot hold the scheduler gate during cleanup.
- Used cancellation-aware archive-entry copies with bounded memory streams and
  `ArrayPool<byte>`; initial entry capacity is capped rather than trusting a
  large archive metadata size.
- Made persistent preview cache reads (`TryLoad`/`TryLoadBrowse`) bounded and
  cancellation-aware, rethrowing cancellation instead of converting it into a
  stuck placeholder.
- Added process-wide fast-codec admission so archive/PDF/native decode workers
  cannot multiply without a bound when a large folder is opened.
- Kept the real reproduction in scope: `L:\tgm\(0archive`, large archive
  thumbnail browsing, and black WebP previews.

## Validation

Build/publish and diff checks were available; the real library UI reproduction
was not claimed as completed by the assistant.
