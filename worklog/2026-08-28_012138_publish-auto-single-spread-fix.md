# Publish auto-single spread navigation fix

## Request

- Continue publishing after the running Fast Reader/Viewer process was closed.

## Action

- Confirmed no `Fast Reader Viewer.exe` process remained.
- Ran `dotnet publish .\CDisplayEx.CSharp.csproj -c Release -o .\release --no-restore`.

## Validation

- Publish completed successfully.
- The Release build and published `Fast Reader Viewer.dll` are both 1,016,832
  bytes and have SHA-256
  `517E4C9751AECE1DCD919EC76D1CA41D9B12488D6CEC740D00FDEE988BDBC9C4`.
- The next launch from `release\Fast Reader Viewer.exe` will use the corrected
  spread navigation implementation.

## Remaining manual verification

- Reproduce the nine-page, first-page-landscape scenario in the published UI.
- No automated UI interaction was performed in this session.
