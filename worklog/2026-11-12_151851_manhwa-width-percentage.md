# Configurable Manhwa reading width

## Request

- Full-width Manhwa rendering magnifies narrow source images too much. Allow
  selecting how much of the reading area's width the image occupies.

## Changes

- `UserSettings.cs`: persist `ManhwaWidthPercent`, default 50%, with 1–100%
  validation on load. Existing profiles without the property get the new default.
- `ReaderSettingsDialog.cs`: Settings > General > Reading layout > Manhwa width
  (%), with numeric entry and a wrapping explanation of scale/section behavior.
- `AsyncMainForm.cs`: a percentage dropdown immediately beside the Manhwa
  toolbar toggle. Common widths are 25/33/50/67/75/100%; Custom opens Settings
  for arbitrary 1–100% values. Show the selected width in the status/tooltip,
  apply changes immediately, save them, and synchronize reader windows.
- The existing Read menu is not added to the current window (`BuildMenu` is not
  invoked), so the usable entry point is the actual toolbar, not a latent menu.
- `SharedAppServices.cs`: dedicated width-change notification; ordinary preset
  changes do not reset the mode or restart global scheduler configuration.
- `ManhwaPageLayout.cs`: scale available width by the configured percentage,
  center the image, and recalculate gap-free screen-height sections. Clamp tiny
  widths and use long arithmetic to keep extreme dimensions safe.
- `AsyncViewerPanel.Manhwa.cs`: cancel stale width renders, retain a stable
  source-row anchor across repeated width/viewport changes, and re-render the
  section that contains that row. Navigation resets the anchor to the new part.
- `README.md`: document the visible percentage control, custom settings path,
  50% default, and preserved reading area.
- Existing GPU rendering and quality/cancellation paths remain in use. Changing
  width does not decode an entire strip through the normal full-page loader.

## Validation

- Release build: passed, 0 warnings/errors.
- Normal publish to `release`: passed; current executable remains
  `release/Fast Reader Viewer.exe`.
- Store builder: passed; local package under `release/store`.
- `git diff --check`: passed (only Git LF/CRLF conversion notices).
- Width geometry: 1,776 layouts / 862,975 assertions passed for 1/10/25/50/75/100%,
  invalid percentages, centered dimensions, complete source-row coverage,
  partial tails, monotonic section counts, tiny viewports, and large integers.
- Hidden STA viewer: 59 checks passed, including baseline navigation/cancel
  behavior, rapid width changes/stale results, 1–100 clamping, and repeated
  width/resize oscillations without source-row drift or metadata reloading.
- Settings/main-form: 25 targeted checks passed for default 50, legacy JSON,
  custom 37, JSON roundtrip, input limits, cancel-safe in-memory dialog edits,
  layout bounds at 720/1040px, real toolbar item order/text/preset checks, shared
  width changes that preserve the current source area, and status updates.
- Test helpers exited. The user's settings file remained byte-identical and no
  full-page loader calls occurred in the integration checks.
- Temporary test projects/logs are outside the repository:
  `%TEMP%/FastReaderManhwaWidthRegression-20261112`,
  `%TEMP%/FastReaderManhwaViewerRegression-20261112/results-width.txt`, and
  `%TEMP%/FastReaderManhwaWidthUi-20261112/result.log`.

## Remaining manual checks and delivery

- Interactive readability with the user's narrow images and real high-DPI /
  fullscreen windows remains a manual check. Hidden-control bounds checks do
  not claim screenshot or complete visible UI verification.
- This is the updated local development build, still 2.0.6 / 2.0.6.0. No commit,
  push, GitHub asset replacement, Store submission, or installation was performed.
