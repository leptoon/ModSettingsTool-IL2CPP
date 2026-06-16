# Mod Settings Tool

Mod Settings Tool is a BepInEx mod for Supermarket Simulator that puts every other installed mod's
settings into the game's own menus, and shows you at a glance whether your mods loaded correctly. It runs
on the current Unity 6 / IL2CPP version of the game.

It has two parts:

- **Main-menu mod list.** On the main menu, a list of your installed mods sits to the right of the menu
  buttons. Each name is green when the mod is healthy, amber when it loaded with warnings, and red when it
  failed, with the reason shown next to it. You can check your load order before you press Play.
- **In-game Mods tab.** Press Escape in the store, open Settings, and click the Mods tab. Pick any
  installed mod from the list and edit its settings on the right: toggles, sliders, dropdowns, text fields,
  and keybinds you can rebind by pressing a key. A mod with no settings says "No settings to change."

## How it works

Mod Settings Tool reads each mod through the standard BepInEx config system and writes changes back
through that mod's own live config entry. Your edits apply when you press Save, and they go through the mod
that owns the setting, so that mod and BepInEx stay in charge of its values. It never hand-edits another
mod's `.cfg` file and never touches your save game.

It works with any BepInEx mod that uses the standard configuration system, so the larger your load order,
the more it does for you.

## Requirements

- Supermarket Simulator, the current Unity 6 / IL2CPP version.
- Tobey's BepInEx Pack for Supermarket Simulator, the IL2CPP variant.

## Install

1. Install Tobey's BepInEx Pack for Supermarket Simulator (the IL2CPP variant).
2. Download the latest release from the
   [Releases](https://github.com/leptoon/ModSettingsTool-IL2CPP/releases) page.
3. Extract it into your Supermarket Simulator game folder, the one that contains `BepInEx`. The plugin
   lands at `BepInEx/plugins/ModSettingsTool/ModSettingsTool.dll`.
4. Launch the game.

To uninstall, delete the `BepInEx/plugins/ModSettingsTool` folder.

## Configuration

The tool's own settings live at `BepInEx/config/com.leptoon.modsettingstool.cfg`, and it lists itself in
the Mods tab like any other mod. Set `[General] Enabled = false` to turn it off: the menus go back to
vanilla and no patches are applied.

## Build from source

The build targets .NET 6 and references the BepInEx and game interop DLLs from your own install, so nothing
game-specific is committed.

1. Install the .NET 6 SDK.
2. Copy `local.props.example` to `local.props` and set `ModSettingsToolRefDir` to your game's `BepInEx`
   directory (the folder that holds `core/` and `interop/`). The file has notes on where to find it.
3. Build:

   ```bash
   dotnet build -c Release src/ModSettingsTool/ModSettingsTool.csproj
   ```

   The DLL is written to `build/ModSettingsTool.dll`.

The `scripts/deploy-to-game.sh` helper builds Release and copies the DLL into your game's plugins folder,
then verifies the deployed bytes with md5.

For the BepInEx config API the mod is built on and the in-game UI surface it hooks, see [`docs/`](docs/).

## Compatibility

Mod Settings Tool loads through BepInEx, so a large game update can break it until it is rebuilt. If your
mods stop loading after an update, check back here for a new build. The tool is built for singleplayer.

## License

Apache License 2.0. See [LICENSE](LICENSE).

## Credits

Built on [BepInEx](https://github.com/BepInEx/BepInEx) and loads through
[Tobey's BepInEx Pack for Supermarket Simulator](https://www.nexusmods.com/supermarketsimulator/mods/9).
Thanks to those projects and to the Supermarket Simulator modding community.

The in-game settings tab started in RDC Stock Manager and moved here so one mod handles settings for the
whole load order.

---

*Mod Settings Tool is not affiliated with or endorsed by the creators of Supermarket Simulator.*
