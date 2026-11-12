# Navigate by clicking the left/right half of the reader

## Request and implementation
- User requested page navigation by clicking either half of the window.
- Full-view Direct2D reading surface now recognizes an unmodified left-button click and emits its physical side to AsyncMainForm.
- Uses existing NavigatePhysicalLeft/Right so LTR/RTL, spreads, and book-boundary behavior match the toolbar/keyboard.
- Defers navigation by the Windows double-click interval. Double-click zoom cancels pending navigation; dragging beyond the system drag threshold (even away and back), release outside, and other mouse buttons do not navigate.
- Returning to fit through other navigation and hiding the viewer clear pending clicks. Timer checks visibility/window focus and is disposed with the viewer.
- Thumbnail selection and toolbar controls are outside the reading-surface click handler.

## Validation
- Release build passed, 0 warnings/errors; git diff --check passed.
- Temporary STA reflection harness invoked actual mouse handlers: left/right queue correct direction; double-click cancels; drag away/back, outside release, right-button input are suppressed; ReturnToFit cancels pending navigation. All seven checks passed.
- Probe: %TEMP%/fastreader-click-probe/Probe.csproj. Initial fixture was too small due to Dock sizing; setting the parent to 800x600 corrected the fixture.
- Published to normal release directory; no reader process was running at the pre-publish check.
- Real mouse interaction with fit/zoom images, native double-click event ordering, RTL spreads and fullscreen overlays remains a manual check; handler tests are not an end-to-end UI reproduction.
- No version bump or GitHub/MSIX publication requested.
