# Prevent accidental Settings numeric changes from mouse wheel

## Symptom

Scrolling a Settings page while the pointer was over a `NumericUpDown` silently
changed that setting instead of only scrolling the page.

## Change

- Added `SettingsNumericUpDown`, used by every numeric input factory in
  `ReaderSettingsDialog`.
- Mouse-wheel input never changes a numeric value, including when the editor
  currently has keyboard focus.
- The wheel message is forwarded to the nearest auto-scrolling settings
  container, so the page continues scrolling naturally under the pointer.
- Direct typing, keyboard adjustment, and the control's up/down buttons retain
  their standard behavior.

## Validation

- `dotnet build -c Release --no-restore` passed with 0 warnings and 0 errors.
- `git diff --check` passed apart from the existing line-ending notice.
- Publishing to the normal `release` directory was attempted but could not
  replace `Fast Reader Viewer.exe` because the running app (PID 568) held the
  executable open. The running process was not closed automatically.

## Remaining manual UI check

After closing the current release executable and publishing again, open every
Settings page and wheel-scroll with the pointer over focused and unfocused
numeric inputs. Confirm values remain unchanged and the settings page scrolls.
