# Correct auto-single landscape spread navigation

## Symptom

- In a nine-page book whose first page is landscape, double-page mode with
  Auto Single Landscape sent End to page 9 by itself instead of pages 8-9.
- Navigating backward reached pages 3-4 and then jumped directly to page 1,
  skipping page 2.

## Cause

- End always selected the final raw page index rather than the start of the
  final spread.
- Previous-page navigation inferred its step only from the page immediately
  before the current index. That reverse-local rule was not the inverse of the
  forward spread sequence after an auto-single landscape page.

## Fix

- Added `SpreadNavigation`, which walks spread starts from the beginning of the
  book using one rule for Next, Previous, and End.
- Double-page navigation now uses that shared sequence while Forward One Page
  and single-page modes retain one-page movement.
- End now selects the beginning of the final spread.
- The menu, toolbar, hotkey, LTR, and RTL paths all converge on the corrected
  navigation methods.

## Files

- `SpreadNavigation.cs`
- `AsyncMainForm.cs`

## Validation

- `dotnet build .\CDisplayEx.CSharp.csproj -c Release --no-restore` succeeded
  with 0 warnings and 0 errors.
- A targeted non-UI regression test produced
  `1, 2-3, 4-5, 6-7, 8-9`, selected page 8 as the End spread start, and moved
  backward from 4-5 to 2-3 and then page 1.
- Additional targeted cases passed for odd/even page counts, first-page
  cover/offset, and a known landscape page in the middle of a book.
- `git diff --check` succeeded with only line-ending notices.
- Publishing to `release` was attempted but correctly failed because running
  process 46136 held `release\Fast Reader Viewer.dll` open. The process was not
  terminated automatically.

## Remaining manual verification

- Close the running application, publish to `release`, then reproduce the
  original nine-page scenario in the real UI.
- No automated UI interaction was performed in this session.
