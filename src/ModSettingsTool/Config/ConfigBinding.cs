using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace ModSettingsTool.Config
{
    // Which uGUI control a config entry maps to. The "Mods" tab builds a control per binding from this.
    internal enum ControlKind
    {
        Toggle,         // bool
        Slider,         // numeric WITH an AcceptableValueRange (Min..Max known)
        IntInput,       // integral numeric, no range -> input field
        FloatInput,     // floating numeric, no range -> input field
        TextInput,      // free string
        EnumDropdown,   // enum -> the Enum names
        ChoiceDropdown, // string/other WITH an AcceptableValueList -> the allowed values
        KeyBind,        // UnityEngine.KeyCode -> a "press a key" rebind control
        Unsupported,    // a type we don't render yet (still listed, read-only)
    }

    // A single editable BepInEx config entry wrapped for the UI: its identity, the control it maps to,
    // any range/choices, and LIVE get/set through ConfigEntryBase.BoxedValue. Setting Value writes the
    // owning mod's own live ConfigEntry, so that mod (which reads its ConfigEntry.Value) picks the change
    // up exactly as if it had been changed in its own UI, and BepInEx persists it to the .cfg. This mod
    // never edits another mod's .cfg file directly; it drives the live entry object.
    internal sealed class ConfigBinding
    {
        public ConfigEntryBase Entry { get; }
        public string Section { get; }
        public string Key { get; }
        public string Description { get; }
        public Type Type { get; }
        public ControlKind Kind { get; }
        public double Min { get; }            // Slider only
        public double Max { get; }            // Slider only
        public string[] Choices { get; }      // EnumDropdown / ChoiceDropdown / KeyBind

        // The entry's own declared default (ConfigEntryBase.DefaultValue), captured once. Drives the "modified"
        // marker (live-or-staged != default) and Reset-to-default. May be null for an exotic entry.
        public object? Default { get; }

        // Whether DefaultValue was successfully captured, TRUE even when the default IS null (a nullable/string
        // entry, which Reset must be able to apply), FALSE only when reading it threw (then we don't know the
        // default, so Reset is not offered and never writes a bogus null).
        public bool HasDefault { get; }

        // De-facto ConfigurationManagerAttributes read from ConfigDescription.Tags (fail-soft reflection by
        // member name; null = unspecified). Surface-everything: nothing here ever fully hides a setting.
        public int? Order { get; }            // sort within a section (higher = earlier), ahead of SettingOrder
        public string? DispName { get; }      // display label override (falls back to Key)
        public string? Category { get; }      // optional sub-group within a section
        public bool ReadOnly { get; }         // render the control read-only
        public bool IsAdvanced { get; }       // IsAdvanced==true OR Browsable==false -> folded into "Advanced"
        public bool HideDefaultButton { get; } // hide the per-row reset

        // The label shown for this setting (the author's DispName when set, else the key).
        public string DisplayName => string.IsNullOrEmpty(DispName) ? Key : DispName!;

        // True when this binding carries a real numeric range (Slider, or a ranged numeric input).
        public bool HasRange => Max > Min;

        private static readonly string[] NoChoices = Array.Empty<string>();

        public object? Value
        {
            get { try { return Entry.BoxedValue; } catch { return null; } }
            set { try { if (value != null) Entry.BoxedValue = value; } catch { } }
        }

        // True for integral numeric types (Slider whole-numbers; stepper step = 1).
        public bool IsWholeNumber =>
            Type == typeof(int) || Type == typeof(long) || Type == typeof(short) || Type == typeof(byte) ||
            Type == typeof(uint) || Type == typeof(ulong) || Type == typeof(ushort) || Type == typeof(sbyte);

        // Type-correct writes for the generic controls, each converts to the entry's actual SettingType
        // before assigning BoxedValue (the live ConfigEntry) and fails soft. NEVER writes the .cfg directly.
        public void SetNumber(double v)
        {
            try { Entry.BoxedValue = Convert.ChangeType(v, Type); } catch { }
        }

        // Convert the raw text STRAIGHT to the entry's SettingType (no double round-trip), so a wide ulong/long/
        // decimal keeps full precision. Returns the boxed typed value, or null if the text is not representable
        // (blank, non-numeric, or out of the type's range like -1 into a uint). Callers stage only a non-null
        // result, so SaveStaged can never clear an edit whose conversion would silently fail or round.
        public object? ParseTyped(string s)
        {
            try { return Convert.ChangeType(s, Type); } catch { return null; }
        }

        // True when v is acceptable to this entry's AcceptableValues (range or list), or there is none. Uses
        // BepInEx's own IsValid on the BOXED TYPED value, the exact check it applies on save, so a wide
        // ulong/long/decimal bound is honored precisely rather than through a lossy double comparison.
        public bool InRange(object? v)
        {
            if (v == null) return true;
            try
            {
                AcceptableValueBase? av = Entry.Description?.AcceptableValues;
                return av == null || av.IsValid(v);
            }
            catch { return true; }
        }

        public void SetChoice(int index)
        {
            try
            {
                if (Choices == null || index < 0 || index >= Choices.Length) return;
                if (Type.IsEnum) Entry.BoxedValue = Enum.Parse(Type, Choices[index]);
                else if (Type == typeof(string)) Entry.BoxedValue = Choices[index];
                else Entry.BoxedValue = Convert.ChangeType(Choices[index], Type);
            }
            catch { }
        }

        // Index of the current value within Choices (enum name / list string); 0 if not found.
        public int CurrentChoiceIndex()
        {
            try
            {
                string s = Value?.ToString() ?? "";
                for (int i = 0; i < Choices.Length; i++)
                    if (string.Equals(Choices[i], s, StringComparison.Ordinal)) return i;
            }
            catch { }
            return 0;
        }

        // Index of the entry's DEFAULT within Choices (for the reset/snap of a dropdown); 0 if not found.
        public int DefaultChoiceIndex()
        {
            try
            {
                string s = Default?.ToString() ?? "";
                for (int i = 0; i < Choices.Length; i++)
                    if (string.Equals(Choices[i], s, StringComparison.Ordinal)) return i;
            }
            catch { }
            return 0;
        }

        // The typed value for a choice index (same conversion as SetChoice, but returned not written), used to
        // compare a staged dropdown pick against the default for the modified marker. Null if out of range.
        public object? ChoiceValue(int index)
        {
            try
            {
                if (Choices == null || index < 0 || index >= Choices.Length) return null;
                if (Type.IsEnum) return Enum.Parse(Type, Choices[index]);
                if (Type == typeof(string)) return Choices[index];
                return Convert.ChangeType(Choices[index], Type);
            }
            catch { return null; }
        }

        // Stage-free reset: write the entry's OWN captured DefaultValue straight through BoxedValue (correct
        // type, no parse, no clamp). Never writes the .cfg, BepInEx persists from the live entry on save.
        // Gated on HasDefault, NOT on Default != null, so a genuine null default (a nullable/string entry) is
        // applied (BoxedValue accepts null there) while a default we failed to read is never written as null.
        public void ResetToDefault()
        {
            try { if (HasDefault) Entry.BoxedValue = Default!; } catch { }
        }

        public void SetKey(UnityEngine.KeyCode key)
        {
            try
            {
                // Most KeyCode entries are UnityEngine.KeyCode; some IL2CPP mods use a distinct KeyCode enum
                // type (same names/values). Box the value as the ENTRY's own enum type so BepInEx's typed setter
                // doesn't reject a cross-type assignment (KeyCode's underlying type is int).
                if (Type == typeof(UnityEngine.KeyCode)) Entry.BoxedValue = key;
                else if (Type.IsEnum) Entry.BoxedValue = Enum.ToObject(Type, (int)key);
                else Entry.BoxedValue = key;
            }
            catch { }
        }

        private ConfigBinding(ConfigEntryBase entry, string section, string key, string description,
                              Type type, ControlKind kind, double min, double max, string[] choices)
        {
            Entry = entry;
            Section = section;
            Key = key;
            Description = description;
            Type = type;
            Kind = kind;
            Min = min;
            Max = max;
            Choices = choices;

            try { Default = entry.DefaultValue; HasDefault = true; } catch { Default = null; HasDefault = false; }

            // De-facto ConfigurationManagerAttributes (ConfigDescription.Tags). Fail-soft, by member name.
            try
            {
                object[]? tags = SafeTags(entry);
                if (tags != null)
                {
                    foreach (object? tag in tags)
                    {
                        if (tag == null) continue;
                        Type tt = tag.GetType();
                        if (!string.Equals(tt.Name, "ConfigurationManagerAttributes", StringComparison.Ordinal)) continue;
                        Order = GetInt(tt, tag, "Order") ?? Order;
                        DispName = GetStr(tt, tag, "DispName") ?? DispName;
                        Category = GetStr(tt, tag, "Category") ?? Category;
                        if (GetBool(tt, tag, "ReadOnly") == true) ReadOnly = true;
                        if (GetBool(tt, tag, "IsAdvanced") == true) IsAdvanced = true;
                        if (GetBool(tt, tag, "Browsable") == false) IsAdvanced = true; // non-browsable -> Advanced, never hidden
                        if (GetBool(tt, tag, "HideDefaultButton") == true) HideDefaultButton = true;
                    }
                }
            }
            catch { }
        }

        private static object[]? SafeTags(ConfigEntryBase e)
        {
            try { return e.Description?.Tags; } catch { return null; }
        }

        // Reflect a FIELD or PROPERTY by name (ConfigurationManagerAttributes uses public fields), fail-soft.
        private static object? GetMember(Type t, object o, string name)
        {
            try { FieldInfo? f = t.GetField(name); if (f != null) return f.GetValue(o); } catch { }
            try { PropertyInfo? p = t.GetProperty(name); if (p != null) return p.GetValue(o); } catch { }
            return null;
        }

        private static int? GetInt(Type t, object o, string n)
        {
            object? v = GetMember(t, o, n);
            try { return v == null ? (int?)null : Convert.ToInt32(v); } catch { return null; }
        }

        private static string? GetStr(Type t, object o, string n)
        {
            string? v = GetMember(t, o, n) as string;
            return string.IsNullOrEmpty(v) ? null : v;
        }

        private static bool? GetBool(Type t, object o, string n)
        {
            object? v = GetMember(t, o, n);
            return v is bool b ? b : (bool?)null;
        }

        // Build the binding list for one mod's ConfigFile (null/empty => no settings). Never throws.
        public static List<ConfigBinding> FromConfigFile(ConfigFile? config)
        {
            var list = new List<ConfigBinding>();
            if (config == null) return list;
            try
            {
                // Snapshot the entries first; a foreach straight over the live dictionary could throw if a
                // mod mutates its config on another thread mid-enumeration.
                var entries = new List<ConfigEntryBase>();
                try { foreach (var kv in config) entries.Add(kv.Value); }
                catch { entries.Clear(); foreach (var v in config.Values) entries.Add(v); }

                foreach (ConfigEntryBase entry in entries)
                {
                    ConfigBinding? b = TryClassify(entry);
                    if (b != null) list.Add(b);
                }
            }
            catch
            {
                // leave whatever was gathered
            }

            // PRESERVE the ConfigFile insertion order (the author's Bind() declaration order), the config-UI
            // norm. The "Mods" tab re-orders for display per the [UI] SettingOrder key (author vs alphabetical)
            // and the per-entry ConfigurationManagerAttributes Order, so no sort happens here.
            return list;
        }

        private static ConfigBinding? TryClassify(ConfigEntryBase entry)
        {
            try
            {
                if (entry == null) return null;
                Type t = entry.SettingType;
                if (t == null) return null;

                string section = SafeSection(entry);
                string key = SafeKey(entry);
                string desc = "";
                AcceptableValueBase? av = null;
                try { desc = entry.Description?.Description ?? ""; av = entry.Description?.AcceptableValues; } catch { }

                // KeyCode first: it IS an enum, but a ~300-item dropdown is awful, give it a rebind control,
                // UNLESS the mod restricts it to an AcceptableValueList (then only a dropdown of the allowed
                // keys keeps the choice inside the list). IsKeyCode also catches interop variants of the type
                // (some IL2CPP mods surface their hotkey as a distinct KeyCode enum) so those get the keycap too.
                if (IsKeyCode(t))
                {
                    string[]? keyChoices = ChoicesOf(av);
                    return keyChoices != null
                        ? Make(entry, section, key, desc, t, ControlKind.EnumDropdown, keyChoices)
                        : Make(entry, section, key, desc, t, ControlKind.KeyBind, EnumNames(t));
                }

                // Enum: a dropdown of the AcceptableValueList subset when the mod declares one, else all names.
                // For a [Flags] enum the live value can be a combination with no single named member (e.g.
                // "Read, Write"), so ensure it is one of the choices, otherwise CurrentChoiceIndex falls back to
                // index 0 and the tab shows the wrong current value.
                if (t.IsEnum)
                {
                    string[] enumChoices = ChoicesOf(av) ?? EnumNames(t);
                    if (t.IsDefined(typeof(FlagsAttribute), false))
                        enumChoices = WithComboValues(entry, enumChoices);
                    return Make(entry, section, key, desc, t, ControlKind.EnumDropdown, enumChoices);
                }

                if (t == typeof(bool))
                {
                    // A bool restricted by an AcceptableValueList (e.g. only false) becomes a dropdown of the
                    // allowed values, so a free toggle can't stage a value BepInEx will reject.
                    string[]? boolChoices = ChoicesOf(av);
                    return boolChoices != null
                        ? Make(entry, section, key, desc, t, ControlKind.ChoiceDropdown, boolChoices)
                        : Make(entry, section, key, desc, t, ControlKind.Toggle, NoChoices);
                }

                if (IsNumeric(t))
                {
                    if (av != null && TryRange(av, out double mn, out double mx))
                    {
                        // Slider only when the range is float-safe; otherwise a TYPED INPUT THAT CARRIES THE
                        // RANGE, so it can reject out-of-range edits (an unranged input would let BepInEx clamp
                        // them on save while we clear the dirty state and leave the bad text on screen).
                        if (IsSliderSafe(t, mn, mx))
                            return new ConfigBinding(entry, section, key, desc, t, ControlKind.Slider, mn, mx, NoChoices);
                        ControlKind rk = IsIntegral(t) ? ControlKind.IntInput : ControlKind.FloatInput;
                        return new ConfigBinding(entry, section, key, desc, t, rk, mn, mx, NoChoices);
                    }
                    // A numeric AcceptableValueList<T> (discrete allowed values, not a range) must be a dropdown,
                    // not a free-form input, otherwise the player can stage a value BepInEx will reject on save.
                    string[]? numChoices = ChoicesOf(av);
                    if (numChoices != null)
                        return Make(entry, section, key, desc, t, ControlKind.ChoiceDropdown, numChoices);
                    ControlKind k = IsIntegral(t) ? ControlKind.IntInput : ControlKind.FloatInput;
                    return Make(entry, section, key, desc, t, k, NoChoices);
                }

                if (t == typeof(string))
                {
                    string[]? choices = ChoicesOf(av);
                    return choices != null
                        ? Make(entry, section, key, desc, t, ControlKind.ChoiceDropdown, choices)
                        : Make(entry, section, key, desc, t, ControlKind.TextInput, NoChoices);
                }

                return Make(entry, section, key, desc, t, ControlKind.Unsupported, NoChoices);
            }
            catch
            {
                return null;
            }
        }

        private static ConfigBinding Make(ConfigEntryBase e, string s, string k, string d, Type t, ControlKind kind, string[] choices)
            => new(e, s, k, d, t, kind, 0, 0, choices);

        private static string SafeSection(ConfigEntryBase e)
        {
            try { return e.Definition?.Section ?? ""; } catch { return ""; }
        }

        private static string SafeKey(ConfigEntryBase e)
        {
            try { return e.Definition?.Key ?? ""; } catch { return ""; }
        }

        private static string[] EnumNames(Type t)
        {
            try { return Enum.GetNames(t); } catch { return NoChoices; }
        }

        // Append the entry's current AND default values (as strings) to choices if absent, for a [Flags] enum
        // whose live or default value is a combination with no single named member (e.g. "Read, Write"), so the
        // dropdown can show + re-select either (Enum.Parse handles the "A, B" form on commit). Including the
        // default matters for Reset/snap: DefaultChoiceIndex must find the default combo or it snaps to option 0
        // while Save applies the real default, a visible/staged mismatch.
        private static string[] WithComboValues(ConfigEntryBase e, string[] choices)
        {
            try
            {
                var list = new List<string>(choices);
                object? def = null; try { def = e.DefaultValue; } catch { }
                AppendIfMissing(list, e.BoxedValue?.ToString() ?? "");
                AppendIfMissing(list, def?.ToString() ?? "");
                return list.Count == choices.Length ? choices : list.ToArray();
            }
            catch { return choices; }
        }

        private static void AppendIfMissing(List<string> list, string value)
        {
            if (value.Length == 0) return;
            foreach (string c in list) if (string.Equals(c, value, StringComparison.Ordinal)) return;
            list.Add(value);
        }

        // True for UnityEngine.KeyCode OR an interop variant of it: some IL2CPP mods surface their hotkey
        // setting as a distinct KeyCode enum type (e.g. FullName "BepInEx.Unity.IL2CPP.UnityEngine.KeyCode").
        // Matching any enum named "KeyCode" lets those render as the press-a-key keycap rather than a ~300-item
        // enum dropdown (DF-06). Paired with the type-correct SetKey above.
        private static bool IsKeyCode(Type t)
        {
            if (t == typeof(UnityEngine.KeyCode)) return true;
            // Only a *UnityEngine.KeyCode (incl. IL2CPP-wrapped namespaces like "Il2CppUnityEngine.KeyCode" or
            // "BepInEx.Unity.IL2CPP.UnityEngine.KeyCode") gets the press-a-key control. A mod's OWN enum that
            // merely happens to be named "KeyCode" must stay an enum dropdown, SetKey writes UnityEngine.KeyCode
            // numeric values, which would be undefined/wrong for an unrelated enum.
            if (!t.IsEnum) return false;
            string fn = t.FullName ?? "";
            return fn.EndsWith("UnityEngine.KeyCode", StringComparison.Ordinal);
        }

        private static bool IsIntegral(Type t) =>
            t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
            t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

        private static bool IsNumeric(Type t) =>
            IsIntegral(t) || t == typeof(float) || t == typeof(double) || t == typeof(decimal);

        // A Unity Slider is float-backed, so only use one when the whole range is exactly representable in a
        // float (≤ 2^24). A wide long/ulong/large-int range, or any decimal, would display or save a rounded
        // value through the float slider, so those fall back to a typed input instead.
        private static bool IsSliderSafe(Type t, double min, double max)
        {
            if (t == typeof(decimal)) return false;
            const double exact = 16_777_216d; // 2^24, float's exact-integer limit
            return Math.Abs(min) <= exact && Math.Abs(max) <= exact;
        }

        // AcceptableValueRange<T> exposes MinValue/MaxValue; read them generically and convert to double.
        private static bool TryRange(AcceptableValueBase av, out double min, out double max)
        {
            min = 0;
            max = 0;
            try
            {
                Type t = av.GetType();
                PropertyInfo? minP = t.GetProperty("MinValue");
                PropertyInfo? maxP = t.GetProperty("MaxValue");
                if (minP == null || maxP == null) return false;
                object? mn = minP.GetValue(av);
                object? mx = maxP.GetValue(av);
                if (mn == null || mx == null) return false;
                min = Convert.ToDouble(mn);
                max = Convert.ToDouble(mx);
                return max > min;
            }
            catch
            {
                return false;
            }
        }

        // AcceptableValueList<T> exposes AcceptableValues (the allowed set); stringify it for a dropdown.
        private static string[]? ChoicesOf(AcceptableValueBase? av)
        {
            if (av == null) return null;
            try
            {
                PropertyInfo? p = av.GetType().GetProperty("AcceptableValues");
                if (p?.GetValue(av) is IEnumerable en)
                {
                    var list = new List<string>();
                    foreach (object? o in en) list.Add(o?.ToString() ?? "");
                    return list.Count > 0 ? list.ToArray() : null;
                }
            }
            catch
            {
                // fall through
            }
            return null;
        }
    }
}
