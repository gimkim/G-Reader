# Worklog — 2026-08-03 17:43:40 ICT

## Scope

Removed the obsolete local preservation rules for historical version
snapshots, because GitHub is now the source of release history.

## Changes

- Removed the `versions\1.0`, `versions\2.0`, and `versions\3.0` do-not-edit
  rules from `AGENTS.md`.
- Renamed the remaining section to clarify that only UI responsiveness and
  Direct2D/page-navigation invariants are preserved.
- Removed stale preserved-snapshot wording and the `versions` repository-boundary
  entry from `MIGRATION.md`.
- Updated the README to describe only local build output as excluded from Git.

No source/runtime code or historical files were deleted. `git diff --check`
passed; this documentation-only change does not require a rebuild.
