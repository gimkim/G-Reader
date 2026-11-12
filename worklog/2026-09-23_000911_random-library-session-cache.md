# Cache Random library candidates for the application session

## Symptom and changes
- Open Random recursively enumerated the configured library on every click.
- Added RandomLibrarySessionCache owned by SharedAppServices, shared across all reader windows in the application instance.
- One completed candidate snapshot per normalized, case-insensitive root; retained only in RAM until the instance exits. Switching roots uses separate snapshots, including when returning to an earlier root.
- Per-root cancellation-aware semaphore prevents simultaneous windows from duplicating a scan. Failed/cancelled scans do not publish a snapshot; successful empty results are cached.
- AsyncMainForm performs cache access and first-scan path validation on the worker thread. Cached calls no longer enumerate or check the root on the UI thread. Existing selection/exclusion of the current book is preserved.
- Added or removed books are reflected after restarting the instance. A cached book removed externally may fail to open through the normal open error handling; no automatic recursive rescan is performed.

## Validation and delivery
- Release build: 0 warnings/errors; git diff --check passed.
- Temporary standalone harness links the actual cache source: normalized roots, distinct roots, 16 concurrent requests/one scan, cancelled result discarded, exception retry, empty snapshot reuse, and a fresh instance rescanning all passed.
- Harness: %TEMP%/fastreader-random-cache-probe/Probe.csproj. No interactive Random/NAS timing test performed.
- Initially published to release/staging-random-session-cache while PID 44756 was running from release.
- User then closed the app; verified no reader process remained and published successfully to release.
- Published release/Fast Reader Viewer.dll SHA-256 matches the current Release build.
- Version remains 2.0.3; no commit/push/MSIX release requested.
