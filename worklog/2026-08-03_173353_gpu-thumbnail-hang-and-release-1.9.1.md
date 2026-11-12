# Worklog — 2026-08-03 17:33:53 ICT

## Scope

Investigated the latest thumbnail-view hang after opening a large archive,
completed the GPU/D2D stability fix, and prepared the 1.9.1 release artifacts.

## Reported symptom and reproduction context

- The thumbnail area stopped updating while the toolbar remained responsive
  after switching a large ZIP between thumbnail and full-view workflows.
- The affected archive contained 359 pages. This is the same class of
  reproduction as ZIP -> Full View -> Thumbnail with rapid preview churn.

## Evidence

- Extended diagnostic session:
  `%LOCALAPPDATA%\Fast Reader Viewer\Diagnostics\session-20260803-172216-pid58264.log`
- Hang dump:
  `hang-20260803-172247-pid58264.dmp`
- The UI thread was inside `ID2D1RenderTarget.EndDraw()` from
  `ThumbnailGridView.DrawDirect2DThumbnailFrame()` while worker threads were
  scaling thumbnails through `GpuContactSheetRenderer` and creating D2D/DXGI
  resources. The session also showed a stalled GPU-retirement event, 155
  threads, and roughly 1.15 GB working set / 1.83 GB private bytes.
- This points to competing background D2D work and the UI render target/device
  queue, rather than a simple input deadlock.

## Changes made

- Changed per-thumbnail GPU scaling to staged NPP GPU resize through pinned host
  memory, then upload the finished bitmap; the thumbnail worker no longer opens
  a competing background Direct2D scale operation.
- Updated the generic fast-preview path to use the same staged GPU resize and
  owned upload lifecycle.
- Preserved the Direct2D compositor for the bounded contact-sheet composition,
  kept interactive GPU rendering, and retained safe device-loss/cancellation
  handling from the earlier stability work.
- Updated project version metadata to 1.9.1 / 1.9.1.0.
- Expanded `AGENTS.md` with the product behavior, GPU/cache/PDF invariants,
  Store/build rules, and worklog convention requested for this project.

## Validation

- `dotnet build -c Release --no-restore` — passed with 0 warnings and 0 errors.
- `dotnet publish -c Release -o release --no-restore` — passed; normal output
  is under `release`.
- Manual UI/GPU reproduction was not run by the assistant; the user should
  retest the large archive/PDF scenarios on the target GPU. Static and build
  checks do not substitute for that runtime test.

## Release handoff

The remaining release steps for this worklog are to build the Store MSIX,
commit the intended source/docs/worklog changes, push the branch, and publish
GitHub release `v1.9.1` with the fresh normal executable and MSIX assets.
