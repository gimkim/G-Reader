# Explorer order and random library scan progress

## Request

Show completed/total counts while reading the current Windows Explorer folder
order and while recursively scanning the configured Random Library.

## Changes

- Added progress reporting to the STA Explorer automation worker using the
  exact item count exposed by the matching Explorer view.
- Added an O(1) Explorer fast path that reads `SortColumns` once and maps the
  supported Shell rule to the reader's native sorter. This avoids enumerating
  every Explorer COM item before the normal folder listing.
- Added Date created sorting (appended to the persisted enum to preserve all
  existing numeric setting values) and mapped the live Shell
  `System.DateCreated` rule, in addition to name, modified date, taken date,
  size, and type/extension rules.
- Unsupported Explorer rules still fall back to exact displayed-item capture,
  retaining compatibility and the processed/total status.
- The status bar now reports `processed/total items` while the Explorer order
  is captured.
- Added single-pass Random Library progress reporting for processed directories,
  all directories discovered so far, and supported book candidates found.
- Random scanning keeps a discovered-directory set, skips reparse points, and
  continues past inaccessible file or subdirectory listings.
- Both paths throttle reports to approximately 12 updates per second so large
  folders do not flood the WinForms message queue or reduce responsiveness.
- Random Library uses a growing discovered-work total rather than performing a
  second full filesystem walk only to calculate the denominator.

## Files

- `AsyncMainForm.cs`
- `Book.cs`
- `ExplorerViewOrder.cs`
- `PageSortMode.cs`

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `git diff --check` passed; Git only reported the repository's existing
  LF-to-CRLF conversion notices.

## Remaining manual UI validation

- Launch an image from an Explorer view containing many items and confirm the
  bottom status advances to the Explorer item total.
- Scan a large Random Library and confirm the discovered total can grow during
  traversal, finishes at matching processed/total counts, and UI input remains
  responsive.
