# Reuse the latest active window placement

## Symptom

- A new reader window used the default `1100 x 760` size and an artificial
  cascade offset instead of the size and position of the latest window.
- The same behavior affected a new window requested by another executable,
  Explorer association, or command-line launch.

## Cause

- Secondary windows were constructed with saved-placement restoration disabled.
- `FastReaderApplicationContext` then changed only their location and kept the
  constructor's default size.

## Fix

- Track the most recently activated `AsyncMainForm` in the application context.
- Capture its normal bounds and maximized state when opening another window.
- For a fullscreen source window, use the saved pre-fullscreen bounds/state.
- Apply the captured bounds exactly, without the previous cascade offset.
- Use persisted placement only when there is no existing window to copy.

## Files

- `AsyncMainForm.cs`
- `FastReaderApplicationContext.cs`

## Validation

- `dotnet build .\CDisplayEx.CSharp.csproj -c Release --no-restore` succeeded
  with 0 warnings and 0 errors.
- `git diff --check` succeeded with only line-ending notices.
- `dotnet publish .\CDisplayEx.CSharp.csproj -c Release -o .\release --no-restore`
  succeeded.
- The published application DLL hash matches the current Release build DLL.

## Remaining manual verification

- Move and resize the active reader, press `Ctrl+N`, and verify the new window
  opens at the same bounds.
- Repeat while maximized and by opening another file from Explorer.
- No automated UI interaction was performed in this session.
