# Worklog — 2026-07-21 00:19:32 ICT

## Scope

Published the initial Fast Reader/Viewer feature set and responsive navigation
foundation.

## Work recorded

- `422fab2` published the folder/image/archive/PDF reader, thumbnail browser,
  Direct2D viewer, settings, cache, hotkeys, file associations, and rendering
  scheduler.
- Added single-page, dual-page, and offset-spread reading behavior, reading
  direction controls, position/navigation state, and archive/PDF discovery.
- `4a9b393` added smooth precision scrolling and preserved high-frequency input
  responsiveness.
- Established the principle that expensive decode, resize, filesystem, and
  archive work must not block WinForms paint/input.

## Validation

The changes were source/build work from the initial feature branch. Real-world
large-folder and GPU stress behavior was left for subsequent user testing.
