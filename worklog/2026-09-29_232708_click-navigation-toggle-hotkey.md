# Toggle Click to Navigate and configure its hotkey

- User requested a menu-bar toggle and configurable hotkey, then specified that enabled click navigation must disable double-click 100% zoom.
- Added checked mouse/arrow toolbar button and Options > Click to Navigate menu item. Both use the same toggle action; tooltip shows On/Off and the configured shortcut.
- Added Toggle Click to Navigate under Settings > Hotkeys > Reading view, default unassigned to avoid taking an existing shortcut.
- Added persisted UserSettings.ClickToNavigate, default true to retain the existing click behavior. Shared setting change updates all reader windows without reconfiguring rendering resources.
- When enabled, each left click including the second click in a double-click navigates immediately; double-click zoom handler is bypassed. When disabled, clicks do not navigate and the existing double-click zoom handler runs. Drag-to-pan and Ctrl-wheel paths remain available.
- Includes the preceding uncommitted immediate-click fix; no existing work discarded.
- Targeted actual-handler harness passed 10 checks including immediate dispatch, repeat click, drag/outside/right-button suppression, disabled click, in-memory JSON persistence, and hotkey catalog registration. No user settings were changed by tests.
- Build/publish succeeded after correcting an ambiguous DrawLines overload in the new icon; normal release directory updated. git diff --check passed.
- Manual toolbar/menu/hotkey interaction, double-click zoom with real image data, and multi-window visual state checks remain. No new version, commit/push or MSIX requested.
