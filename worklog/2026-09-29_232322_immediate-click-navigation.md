# Remove click navigation delay

- User reports newly added click navigation feels much slower than arrows or scrolling.
- Cause: single-click dispatch waited for SystemInformation.DoubleClickTime through a WinForms timer.
- Removed the timer and dispatch PageSideClicked synchronously on valid left mouse-up, after releasing pan/capture state. Retains drag threshold, outside-release, modifier and other-button filtering.
- Behavioral tradeoff communicated: the first click of a double-click now changes page immediately; the second click still invokes zoom but does not turn another page. There is no delay to distinguish a single click from the first click of a double-click.
- Release build passed with 0 warnings/errors; diff check passed.
- Temporary reflection harness invoked actual mouse handlers and verified synchronous left/right events without pumping timers, no event on press alone, no second page turn from the second half of double-click, and suppression of drag-away/back, outside release and right-button input (seven checks passed).
- Harness: %TEMP%/fastreader-immediate-click/Probe.csproj. Actual desktop mouse/GPU end-to-end latency was not measured.
- No reader process found before publication; published successfully to normal release directory. Version unchanged (2.0.5); no GitHub/MSIX release requested.
