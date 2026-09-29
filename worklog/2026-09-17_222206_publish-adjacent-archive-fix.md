# Publish adjacent archive fix to normal release directory

- User explicitly requested publication to the actual release path.
- No Fast Reader Viewer process was running at the pre-publish check.
- Ran dotnet publish -c Release -o release --no-restore successfully.
- Verified SHA-256 of release/Fast Reader Viewer.exe and release/Fast Reader Viewer.dll against the current Release build; both match.
- Source fix and NAS timing evidence are recorded in 2026-09-17_222002_fix-adjacent-archive-nas-delay.md.
- No version bump, GitHub publication, or MSIX packaging. Manual UI archive navigation remains to be verified; the app was not launched by this session.
