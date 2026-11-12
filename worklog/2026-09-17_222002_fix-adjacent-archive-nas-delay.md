# Faster adjacent archive lookup on NAS

## Symptom and evidence
- User reports slow archive-to-archive navigation and requests diagnosis from the latest log and a fix.
- Inspected Diagnostics/session-20260917-211955-pid14712.log. At 22:14:31 the parent folder reports 162 folders and 10,443 containers. Subsequent archive Open requested -> Open completed intervals are approximately 13-28 ms; those breadcrumbs exclude adjacent-path discovery.
- Current settings: FolderPageSort=DateModified (2), descending=true, AutoMoveMode=2.
- FindAdjacentBook reopened the entire parent via Book.Open on every navigation. SortBrowsePaths then queried modification timestamps separately for every sibling. The parent page list was also built unnecessarily.
- Read-only invocation of the original FindAdjacentBook against the three archive paths in this log, mode=3 (explicit next-container action): 2430, 2334, 2442 ms. This directly reproduces the discovery delay, not the entire UI transition.

## Changes
- Book.GetSiblingEntries enumerates FileSystemInfo objects once, retaining enumeration-provided timestamps and file sizes, then uses the existing sort rules without constructing image pages for the parent.
- Folder and archive/PDF grouping, tie breakers, descending order, and folder eligibility are preserved. No retained directory snapshot: additions/deletions are seen on the next request.
- Adjacent discovery observes the current book cancellation token, rejects stale/disposed results, and logs start/completion with elapsed milliseconds before Open requested.
- GPU rendering and decoding paths remain unchanged.
- Files: Book.cs, AsyncMainForm.cs.

## Validation
- dotnet build -c Release --no-restore: 0 warnings/errors.
- Same three NAS lookups after change: 104/39/23 ms; second run: 74/29/20 ms. Results match the original next paths exactly. These are warm filesystem measurements, not cold-NAS or end-to-end UI timings.
- Temporary reflection probe compares new sibling order against Book.Open on a local fixture for all 7 sort modes in both directions: 14 passed. Includes folders, archives, PDF, ignored file, and empty folder; cancellation passed.
- Probe retained outside source control at %TEMP%/fastreader-adjacent-probe/Program.cs.
- dotnet publish -c Release -o release/staging-archive-navigation --no-restore: passed.
- git diff --check: passed (line-ending notices only).

## Delivery and remaining verification
- Running PID 14712 uses release/Fast Reader Viewer.exe. It was not terminated or overwritten.
- Updated executable: release/staging-archive-navigation/Fast Reader Viewer.exe. Close all existing reader windows before starting it, because launches normally forward to the existing profile-owning process.
- Manual next/previous archive UI testing with the new executable is still required. No live GPU/UI fix claim, no version bump, no commit/push, no Store package/release publication.
