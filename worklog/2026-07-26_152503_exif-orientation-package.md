# Worklog — 2026-07-26 15:25:03 ICT

## Scope

Camera-JPEG EXIF orientation and package/security hardening, reconstructed from
the diagnostic worklog associated with the GPU/package fixes.

## Work recorded

- Updated Magick.NET-Q8-x64 from 14.14.0 to 14.15.0; the transitive vulnerable
  package scan was clean afterward.
- Propagated EXIF orientation tag `0x0112` through `Book` page metadata,
  thumbnail/full-view/zoom/animation/print/probe/contact-sheet render paths,
  and rotation-aware cache keys.
- Kept archive entries from paying the EXIF-read cost when they are not JPEGs.
- Preserved GPU-first rendering and safe CPU fallback only when a native GPU
  path is unavailable or retired.

## Validation

Vulnerability scan, build, and publish were verified. A known camera-JPEG UI
sample was still required for definitive orientation validation.
