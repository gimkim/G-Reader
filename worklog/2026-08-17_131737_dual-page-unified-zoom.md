# Unified dual-page zoom

## Request

When Full view displays a two-page spread, zoom and pan both pages together as
one spread instead of entering zoom mode for only the page under the pointer.
Each page should retain its independently fitted slot size; the larger original
source is the basis for the native 100% zoom scale.

## Implementation

- `AsyncViewerPanel` now detects a visible two-page spread and probes both
  rotated source sizes concurrently before entering zoom mode.
- The existing independently fitted left/right rectangles are converted into
  one logical spread coordinate system. Wheel zoom, toolbar zoom, animated zoom
  transitions, anchor calculations, clamping, and drag pan operate on the
  spread bounds once and derive both page rectangles from that transform.
- The larger source by pixel area defines the spread's native scale. The other
  page preserves its fitted relative size and follows the exact same zoom
  factor.
- Zoom-detail requests are generated independently for each visible page in its
  own source coordinates. Fast and final Lanczos/GPU viewport patches are then
  placed back into the shared spread transform, preserving per-page quality and
  color-profile handling.
- `Direct2DViewerSurface` now retains and draws both zoom base bitmaps, supports
  atomic spread layout/pan updates, and associates each detail layer with the
  correct page color profile.
- Single-page zoom retains its previous path and behavior.

## Safety

- The normal Direct2D/GPU presentation path remains active; no combined CPU
  bitmap is created.
- Existing zoom cancellation/version guards continue to reject stale detail
  callbacks.
- Both zoom base bitmaps are protected from GPU cache trimming while active and
  are cleared during zoom exit, frame clear, and device resource release.

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `dotnet publish -c Release -o release --no-restore` passed.
- `git diff --check` passed apart from existing line-ending notices.
- Published normal executable remains version `1.9.3.0` under `release`.

## Remaining manual UI check

In two-page Full view, test wheel zoom, toolbar zoom, double-click 100%, return
to Fit, and drag pan with pages of matching and mismatched dimensions in both
LTR and RTL modes. Confirm both pages remain locked together and that sharp
detail arrives for both pages. No automated UI interaction was performed.
