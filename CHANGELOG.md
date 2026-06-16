# Changelog

All notable changes to Mod Settings Tool. The newest release is at the top. Dates are year-month-day.

## 0.1.0 (2026-06-16)

The first public release. Mod Settings Tool runs on the current Supermarket Simulator (Unity 6, IL2CPP)
under Tobey's BepInEx Pack.

### Added

- Main-menu mod list: a list of installed mods to the right of the menu buttons, color-coded by health.
  Green is healthy, amber loaded with warnings, and red failed to load with the reason shown beside it.
- In-game Mods tab: a tab in the Escape-menu Settings window with a scrollable, alphabetical list of every
  installed mod. Pick one to edit its settings on the right.
- Controls generated from each mod's BepInEx config: toggles, sliders, enum and choice dropdowns, text and
  number inputs, and keybinds you can rebind by pressing a key or clear with one click.
- Staged editing: changes apply only when you press Save, and they are written through each mod's own live
  config entry, so the owning mod and BepInEx stay authoritative. A Discard path reverts unsaved edits.
- A row for mods that expose no settings, which read "No settings to change."
- An `[General] Enabled` option in the tool's own config. With it off, no patches run and the menus are
  vanilla.

### Notes

- The tool is read-only toward every other mod and the game. It never hand-edits another mod's `.cfg` file
  and never writes your save.
- It works with any BepInEx mod that uses the standard configuration system.
