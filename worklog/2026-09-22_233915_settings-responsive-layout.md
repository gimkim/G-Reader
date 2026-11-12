# Settings layout across resolutions and scaling

## Symptom and evidence
- Settings rows and explanations were crowded/clipped on different resolutions and DPI scales.
- Section heights were computed from fixed 46/100px field allowances, with fixed 60px descriptions. Explain editors reserved 30px and assigned remaining space to text. These values did not follow wrapped text height.
- Checkbox AutoSize preferred-size checks alone missed visible truncation: a rendered 150% fixture showed its caption clipped on one line.

## Changes
- ReaderSettingsDialog: AutoSize rows/cards/explanations replace fixed height allocation. Editors dock at the top; text determines required height and the page scrolls vertically.
- SettingsFieldTable: below 620 logical pixels of field-table width, labels and editors occupy separate full-width rows; wider pages retain two columns. Reflows on resize/DPI layout.
- SettingsWrapLabel: width-constrained preferred height, with bounded measurement caching invalidated by text/font/DPI changes.
- SettingsWrapCheckBox: measures wrapped captions and sizes the control accordingly, with native AutoSize disabled so captions actually wrap.
- SettingsFlowPanel: constrains long button/status widths and benchmark progress bars to available space.
- Benchmark commands/status, Hotkeys rows/hint, tab headers and Save/Cancel footer grow with their content. Tabs wrap into multiple rows when needed.
- Working-area fitting uses the dialog's own monitor, including after moving between monitors.
- Deferred construction layout reduces repeated nested table measurement.

## Validation
- Actual WinForms dialog instantiated in a temporary STA reflection harness, without saving settings or running benchmarks.
- 72 geometry cases passed: all six tabs at client sizes 1040x820, 720x560, 620x440, with control/font scaling factors 1.0/1.25/1.5/2.0. Checked table-child overlap, label/checkbox preferred height, controls extending beyond parent width, and horizontal scrollbars.
- Initially found oversized progress bars/long buttons at 200%; fixed and reran all 72 cases with zero errors.
- Inspected DrawToBitmap output of Performance at 720x560 / 150%; confirmed wrapped checkbox text, separated rows, and visible Save/Cancel footer.
- Additional 18 tab cases passed at 150% with wide -> narrow -> wide resizing and dynamically lengthened/shortened benchmark status text.
- Harness: `%TEMP%/fastreader-settings-layout/Probe.csproj`; rendered sample: `%TEMP%/fastreader-settings-layout/performance.png`. Test data/artifacts remain outside source control.
- These are programmatic font/control scaling simulations, not OS DPI changes or a physical mixed-DPI monitor drag test.
- Release build passed with 0 warnings/errors; `dotnet publish -c Release -o release --no-restore` passed. No reader process was running at publish time.
- `git diff --check` passed (line-ending notice only).

## Delivery and remaining checks
- Updated executable: `release/Fast Reader Viewer.exe`; version remains 2.0.2.
- Physical mixed-DPI monitor transition and the user's affected machine remain manual checks.
- No GitHub release, version bump, or MSIX requested for this change.
