# Changelog

All notable changes to Mod Settings Tool. The newest release is at the top. Dates are year-month-day.

## 0.2.0 (2026-06-17)

This release brings the Mods tab to the main menu, reworks the right pane, simplifies mod health to
loaded/failed, and respects the configuration metadata that mods provide.

### Added

- The Mods tab now also appears on the main-menu Settings page, not just the in-store Escape menu, so you
  can review and edit any mod's settings before loading a save.
- A reworked right pane: a per-mod header with the mod's name, version, GUID, and load status (with the
  failure reason when it failed), settings grouped into collapsible sections, and a cleaner look that fits
  the game's menus.
- Per-setting descriptions under each control, with a toggle to hide them for a denser list.
- Per-mod search to filter a long settings list as you type.
- A colour editor for colour-valued settings: a live swatch with RGBA sliders and a hex field, collapsed
  by default behind the colour row.
- Reset for a single setting and "Reset all to defaults", with a marker showing which settings differ from
  their default. Resets are staged like any edit and applied on Save.
- Support for the metadata mods attach to their settings (ConfigurationManagerAttributes): custom display
  names, categories, ordering, an "Advanced Settings" group for advanced or non-browsable settings
  (surfaced at the bottom, never hidden), read-only settings, and hidden per-setting reset buttons.
- The main-menu list can be turned on or off from the tool's own settings and updates live, no restart
  needed.

### Changed

- Mod health is now binary: green when a mod loaded, red when it failed to load, with the reason shown.
  The previous "warnings" (amber) tier and the log scan were removed. Mod Settings Tool is a configuration
  manager, not a diagnostic tool.
- Settings appear in the mod author's declared order by default, with an option to sort alphabetically.
- A "Debug" section is moved to the bottom of a mod's page and collapsed by default.

### Fixed

- A mod that fails to load because it depends on a BepInEx-pack utility now shows red with its reason
  instead of being hidden from the list.
- "Reset all to defaults" no longer changes read-only or unsupported settings, and settings whose default
  is empty now reset correctly.
- Flags-style enum settings show and restore their combined default value correctly.
- Colour values with a malformed alpha are rejected instead of being saved as transparent.
- The mouse wheel over an open dropdown scrolls the dropdown list, not the page behind it.
- Large whole-number settings compare exactly, so the modified marker and reset stay accurate.

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
