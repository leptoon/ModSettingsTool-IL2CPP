using BepInEx.Configuration;
using ModSettingsTool.Localization;
using System;
using System.Collections.Generic;
using System.IO;

namespace ModSettingsTool
{
    // How a mod's settings are ordered on its page in the in-game "Mods" tab.
    public enum SettingOrderMode
    {
        AuthorDeclaration, // the order the mod author called Bind() (the config-UI norm); default
        Alphabetical,      // sorted by section then key
    }

    // De-facto tag shape honored by ConfigBinding through ConfigDescription.Tags. This lets our own settings
    // show polished labels and metadata in the Mods tab while BepInEx still owns the entries.
    internal sealed class ConfigurationManagerAttributes
    {
        public string? DispName { get; set; }
        public string? Category { get; set; }
        public bool? ReadOnly { get; set; }
        public bool? IsAdvanced { get; set; }
        public bool? HideDefaultButton { get; set; }
    }

    internal static class ModConfigDefinitions
    {
        internal const string GeneralSection = "General";
        internal const string EnabledKey = "Enable Mod Settings Tool";

        internal const string MainMenuSection = "Main Menu";
        internal const string ShowMainMenuListKey = "Show Mod List";

        internal const string InterfaceSection = "Interface";
        internal const string SettingOrderKey = "Settings Order";
        internal const string ShowDescriptionsKey = "Show Setting Descriptions";

        internal const string LocalizationSection = "Localization";
        internal const string LastDetectedLocaleKey = "Detected Language Cache";

        internal const string LegacyGeneralSection = "General";
        internal const string LegacyEnabledKey = "Enabled";
        internal const string LegacyMainMenuSection = "MainMenu";
        internal const string LegacyShowMainMenuListKey = "ShowModList";
        internal const string LegacyUiSection = "UI";
        internal const string LegacySettingOrderKey = "SettingOrder";
        internal const string LegacyShowDescriptionsKey = "ShowDescriptions";
        internal const string LegacyLocalizationSection = "Localization";
        internal const string LegacyLastDetectedLocaleKey = "LastDetectedLocale";

        internal static bool IsOwnSectionKey(string section, string key)
        {
            return (section == GeneralSection && key == EnabledKey)
                   || (section == MainMenuSection && key == ShowMainMenuListKey)
                   || (section == InterfaceSection && key == SettingOrderKey)
                   || (section == InterfaceSection && key == ShowDescriptionsKey)
                   || (section == LocalizationSection && key == LastDetectedLocaleKey);
        }

        internal static string SectionLabel(string section)
        {
            if (section == GeneralSection) return I18n.T("config.section.general");
            if (section == MainMenuSection) return I18n.T("config.section.mainMenu");
            if (section == InterfaceSection) return I18n.T("config.section.interface");
            if (section == LocalizationSection) return I18n.T("config.section.localization");
            return string.IsNullOrEmpty(section) ? I18n.T("ui.generalSection") : section;
        }

        internal static string KeyLabel(string section, string key)
        {
            if (section == GeneralSection && key == EnabledKey) return I18n.T("config.enabled.label");
            if (section == MainMenuSection && key == ShowMainMenuListKey) return I18n.T("config.showMainMenuList.label");
            if (section == InterfaceSection && key == SettingOrderKey) return I18n.T("config.settingOrder.label");
            if (section == InterfaceSection && key == ShowDescriptionsKey) return I18n.T("config.showDescriptions.label");
            if (section == LocalizationSection && key == LastDetectedLocaleKey) return I18n.T("config.lastLocale.label");
            return key;
        }

        internal static string Description(string section, string key, string fallback)
        {
            if (section == GeneralSection && key == EnabledKey) return I18n.T("config.enabled.description");
            if (section == MainMenuSection && key == ShowMainMenuListKey) return I18n.T("config.showMainMenuList.description");
            if (section == InterfaceSection && key == SettingOrderKey) return I18n.T("config.settingOrder.description");
            if (section == InterfaceSection && key == ShowDescriptionsKey) return I18n.T("config.showDescriptions.description");
            if (section == LocalizationSection && key == LastDetectedLocaleKey) return I18n.T("config.lastLocale.description");
            return fallback;
        }

        internal static string[] ChoiceLabels(string ownerGuid, string section, string key, string[] choices)
        {
            if (!string.Equals(ownerGuid, Plugin.PluginGuid, StringComparison.OrdinalIgnoreCase)) return choices;
            if (section != InterfaceSection || key != SettingOrderKey || choices.Length != 2) return choices;

            var labels = new string[choices.Length];
            for (int i = 0; i < choices.Length; i++)
            {
                labels[i] = choices[i] switch
                {
                    nameof(SettingOrderMode.AuthorDeclaration) => I18n.T("config.settingOrder.authorDeclaration"),
                    nameof(SettingOrderMode.Alphabetical) => I18n.T("config.settingOrder.alphabetical"),
                    _ => choices[i],
                };
            }
            return labels;
        }

        internal static string? ReadConfigString(ConfigFile config, string section, string key)
        {
            if (TryReadConfigValue<string>(config, section, key, out string? value)) return value;
            return ReadConfigFileValue(config, section, key);
        }

        internal static bool HasConfigKey(ConfigFile config, string section, string key)
        {
            try
            {
                foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> kv in config)
                {
                    if (string.Equals(kv.Key.Section, section, StringComparison.Ordinal) &&
                        string.Equals(kv.Key.Key, key, StringComparison.Ordinal))
                        return true;
                }
            }
            catch { }

            return ReadConfigFileValue(config, section, key) != null;
        }

        internal static bool TryReadConfigValue<T>(ConfigFile config, string section, string key, out T value)
        {
            value = default!;
            try
            {
                var def = new ConfigDefinition(section, key);
                foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> kv in config)
                {
                    if (!string.Equals(kv.Key.Section, def.Section, StringComparison.Ordinal) ||
                        !string.Equals(kv.Key.Key, def.Key, StringComparison.Ordinal))
                        continue;
                    return TryConvert(kv.Value.BoxedValue, out value);
                }
            }
            catch { }

            string? raw = ReadConfigFileValue(config, section, key);
            return TryConvert(raw, out value);
        }

        internal static bool TryConvert<T>(object? raw, out T value)
        {
            value = default!;
            try
            {
                if (raw == null) return false;
                if (raw is T t) { value = t; return true; }
                Type target = typeof(T);
                if (target.IsEnum)
                {
                    value = (T)Enum.Parse(target, raw.ToString() ?? "", ignoreCase: true);
                    return true;
                }
                value = (T)Convert.ChangeType(raw, target);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? ReadConfigFileValue(ConfigFile config, string section, string key)
        {
            try
            {
                string? path = config.GetType().GetProperty("ConfigFilePath")?.GetValue(config)?.ToString();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

                string current = "";
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        current = line.Substring(1, line.Length - 2);
                        continue;
                    }
                    if (!string.Equals(current, section, StringComparison.Ordinal)) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string foundKey = line.Substring(0, eq).Trim();
                    if (!string.Equals(foundKey, key, StringComparison.Ordinal)) continue;
                    return line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return null;
        }
    }

    // Mod Settings Tool's OWN config. (This mod's job is to surface every OTHER mod's config; these are
    // just its handful of self-settings.) New keys use polished player-facing names; legacy values are copied
    // forward on first launch after the rename.
    internal sealed class ModConfig
    {
        // Master switch. When false the mod applies no patches and adds no UI (vanilla menus).
        public ConfigEntry<bool> Enabled { get; }

        // Show the installed-mod list on the main menu (Part 1). Off = only the in-game "Mods" tab.
        public ConfigEntry<bool> ShowMainMenuList { get; }

        // Order each mod's settings on its "Mods"-tab page: by the author's Bind() declaration order (default)
        // or alphabetically. Editable on Mod Settings Tool's own row in the tab.
        public ConfigEntry<SettingOrderMode> SettingOrder { get; }

        // Show each setting's description sub-text under its control on the "Mods" tab. Off = a more compact page
        // (control + meta line only). Editable on Mod Settings Tool's own row in the tab.
        public ConfigEntry<bool> ShowDescriptions { get; }

        // Persisted language cache: BepInEx config descriptions are created during plugin load, often before
        // Unity's selected locale is available, so the last detected runtime language seeds the next launch.
        public ConfigEntry<string> LastDetectedLocale { get; }

        public ModConfig(ConfigFile config)
        {
            I18n.Initialize(config);

            Enabled = BindMigrated(
                config,
                ModConfigDefinitions.GeneralSection,
                ModConfigDefinitions.EnabledKey,
                true,
                Description("config.enabled.description", "config.enabled.label"),
                (ModConfigDefinitions.LegacyGeneralSection, ModConfigDefinitions.LegacyEnabledKey));

            ShowMainMenuList = BindMigrated(
                config,
                ModConfigDefinitions.MainMenuSection,
                ModConfigDefinitions.ShowMainMenuListKey,
                true,
                Description("config.showMainMenuList.description", "config.showMainMenuList.label"),
                (ModConfigDefinitions.LegacyMainMenuSection, ModConfigDefinitions.LegacyShowMainMenuListKey));

            SettingOrder = BindMigrated(
                config,
                ModConfigDefinitions.InterfaceSection,
                ModConfigDefinitions.SettingOrderKey,
                SettingOrderMode.AuthorDeclaration,
                Description("config.settingOrder.description", "config.settingOrder.label"),
                (ModConfigDefinitions.LegacyUiSection, ModConfigDefinitions.LegacySettingOrderKey));

            ShowDescriptions = BindMigrated(
                config,
                ModConfigDefinitions.InterfaceSection,
                ModConfigDefinitions.ShowDescriptionsKey,
                true,
                Description("config.showDescriptions.description", "config.showDescriptions.label"),
                (ModConfigDefinitions.LegacyUiSection, ModConfigDefinitions.LegacyShowDescriptionsKey));

            LastDetectedLocale = config.Bind(
                ModConfigDefinitions.LocalizationSection,
                ModConfigDefinitions.LastDetectedLocaleKey,
                I18n.CurrentLocale,
                Description("config.lastLocale.description", "config.lastLocale.label", readOnly: true, advanced: true));

            I18n.AttachLastDetectedLocale(LastDetectedLocale);
        }

        private static ConfigDescription Description(string descriptionKey, string labelKey, bool readOnly = false, bool advanced = false)
        {
            return new ConfigDescription(
                I18n.StartupT(descriptionKey),
                null,
                new ConfigurationManagerAttributes
                {
                    DispName = I18n.StartupT(labelKey),
                    ReadOnly = readOnly ? true : null,
                    IsAdvanced = advanced ? true : null,
                    HideDefaultButton = readOnly ? true : null,
                });
        }

        private static ConfigEntry<T> BindMigrated<T>(
            ConfigFile config,
            string section,
            string key,
            T defaultValue,
            ConfigDescription description,
            params (string Section, string Key)[] legacyKeys)
        {
            bool newKeyAlreadyExists = ModConfigDefinitions.HasConfigKey(config, section, key);
            ConfigEntry<T> entry = config.Bind(section, key, defaultValue, description);
            if (newKeyAlreadyExists) return entry;
            if (!EqualityComparer<T>.Default.Equals(entry.Value, defaultValue)) return entry;

            for (int i = 0; i < legacyKeys.Length; i++)
            {
                if (ModConfigDefinitions.TryReadConfigValue<T>(config, legacyKeys[i].Section, legacyKeys[i].Key, out T legacyValue))
                {
                    entry.Value = legacyValue;
                    break;
                }
            }
            return entry;
        }
    }
}
