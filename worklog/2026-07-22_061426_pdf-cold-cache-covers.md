# Worklog — 2026-07-22 06:14:26 ICT

## Scope

Cold-cache PDF thumbnail stability, memory growth, folder covers, and viewport
resize behavior.

## Work recorded

- `6e2bd4c` reduced PDF thumbnail memory growth and improved worker cleanup.
- `2c7e970` improved folder thumbnail cover selection and labels; immediate
  folder images are preferred, with limited child traversal and no recursive
  parent cover.
- `4e517e0` fitted cached full-view frames to the new viewport during resize,
  avoiding a stale old-window-size presentation.
- `72bd1c8` hardened the cold-cache PDF thumbnail path against crashes while
  scrolling rapidly before thumbnails were generated.
- The user-facing delete-page/cache-remap, multi-select, font sizing, and
  archive/PDF cover requirements were tracked as part of this thumbnail and
  cache-hardening phase.

## Validation

Build/static validation was completed. The user performed the high-speed PDF
scroll tests manually; no assistant UI test is claimed here.
