Mod Settings Tool  v0.1.0
=========================

An in-game configuration manager for every installed BepInEx mod in
Supermarket Simulator. By Leptoon. Apache License 2.0.

What it does
------------
- Main menu: a list of every installed mod, color-coded by health.
  Green is healthy, amber loaded with warnings, red failed to load
  (with the reason shown).
- In the store, press Esc, open Settings, and click the "Mods" tab:
  pick any installed mod and edit its BepInEx config live. Toggles,
  sliders, dropdowns, text fields, and keybinds. Mods without config
  show "No settings to change."

Edits apply when you press Save and go through each mod's own config
system, so the owning mod and BepInEx stay in charge. It never
hand-edits another mod's .cfg file and never touches your save game.

Requirements
------------
- Supermarket Simulator
- BepInEx 6 (IL2CPP), Tobey's BepInEx Pack

Install
-------
1. Extract this archive into your Supermarket Simulator game folder,
   the folder that contains "BepInEx". The plugin will land at:
     BepInEx/plugins/ModSettingsTool/ModSettingsTool.dll
2. Launch the game.

To uninstall, delete the BepInEx/plugins/ModSettingsTool folder.

Configuration
-------------
The tool's own settings live at:
  BepInEx/config/com.leptoon.modsettingstool.cfg
Set [General] Enabled = false to disable it (the menus stay vanilla);
the tool also lists its own row in the Mods tab.

GUID: com.leptoon.modsettingstool
