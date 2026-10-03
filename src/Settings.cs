using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace Ezomic.Core
{
    /// <summary>Which half of a mod's page a setting sits in.</summary>
    public enum SettingsGroup
    {
        Display,
        Hotkeys
    }

    /// <summary>
    /// The settings screen's registration API (LHM-51): a mod lists the entries a player may
    /// change from the compendium, and Core draws one page per mod.
    ///
    /// <code>
    /// SettingsPanel.Add(JafnaConfig.HoldKey, "Raise hold", SettingsGroup.Hotkeys, "while levelling with the hoe");
    /// </code>
    ///
    /// <b>Only a player's own settings are ever listed.</b> An entry is listed when the host has
    /// no say in it: a KeyCode or KeyboardShortcut, which Core never imposes, or an entry the mod
    /// declared with <see cref="Suite.Local"/>. The test is the one config sync itself uses, whether
    /// the entry is in the mod's synced set, asked when the page is drawn rather than when the mod
    /// registers, so a mod may call Local before or after Add. Anything else is skipped
    /// silently, because a value the host puts back the moment it is written would be a control
    /// that does nothing on a server, and the panel's promise is that what it lists is exactly what
    /// a player can change.
    ///
    /// <b>Additive.</b> Nothing existing changed meaning. A mod that never calls this is untouched,
    /// a mod that calls it with Core absent never reaches this assembly (see the guarded call in
    /// each mod), and a Core without this class just never gets the call.
    ///
    /// Attributed to a mod by the calling assembly, the way <see cref="Suite.Owns"/> is, so a mod
    /// passes no guid. A mod that registered with the gate is named as it registered, and the row
    /// waits until it has, so the order of two lines in somebody's Awake does not matter. A mod
    /// that never registers, Devkit being the one, is named by its plugin and may list key
    /// entries only: nothing is imposed on a mod the gate has not heard of, but nothing says an
    /// entry of its is personal either, and a key is the one kind that always is.
    /// </summary>
    public static class SettingsPanel
    {
        internal static readonly List<SettingRow> Rows = new List<SettingRow>();

        /// <param name="entry">The cfg entry the screen reads and writes.</param>
        /// <param name="label">What the row says: short, a noun for a key ("Raise hold").</param>
        /// <param name="group">Display for what is drawn on screen, Hotkeys for keys.</param>
        /// <param name="whenItActs">
        /// Hotkeys only: the situation the key does something in, in a few words ("while mining a
        /// deposit"). It is what lets the screen tell two features sharing a key apart.
        /// </param>
        /// <param name="summary">
        /// How the row reads in the one-line summary under the mod's name in the list. Left null
        /// a key reads "label KEY", a switch "label on" or "label off" and a choice its value.
        /// "on|off" gives a switch its own two phrases, either side may be empty to say nothing
        /// in that state, "{0}" stands for the current value, and an empty string leaves the row
        /// out of the summary.
        /// </param>
        /// <param name="readsThroughZInput">
        /// Hotkeys only. True when the mod reads the key through ZInput, which sees every mouse
        /// button. A mod reading through UnityEngine.Input, as BepInEx's own
        /// KeyboardShortcut.IsDown does, misses the middle mouse button here, so it passes false
        /// and the screen refuses a mouse button for that row instead of binding one that would
        /// silently do nothing.
        /// </param>
        // NoInlining for the reason Suite.Register has it: GetCallingAssembly answers relative
        // to this frame, and an inlined call would attribute every row to Core.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Add(ConfigEntryBase entry, string label, SettingsGroup group,
            string whenItActs = null, string summary = null, bool readsThroughZInput = true)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(label)) throw new ArgumentNullException(nameof(label));

            if (!SettingRow.Supports(entry))
            {
                CorePlugin.Log.LogWarning("Settings screen: " + Suite.Key(entry) + " is a "
                    + entry.SettingType.Name + ", which the screen cannot edit, so it is not listed.");
                return;
            }

            var row = new SettingRow
            {
                Entry = entry,
                Label = label,
                Group = group,
                When = string.IsNullOrEmpty(whenItActs) ? null : whenItActs,
                Summary = summary,
                ReadsThroughZInput = readsThroughZInput,
                Owner = Assembly.GetCallingAssembly()
            };

            // Added again is a reload, not a second row.
            for (int i = 0; i < Rows.Count; i++)
            {
                if (!ReferenceEquals(Rows[i].Entry, entry)) continue;
                Rows[i] = row;
                return;
            }

            Rows.Add(row);
        }
    }

    internal sealed class SettingRow
    {
        internal ConfigEntryBase Entry;
        internal string Label;
        internal SettingsGroup Group;
        internal string When;
        internal string Summary;
        internal Assembly Owner;
        internal bool ReadsThroughZInput = true;

        internal Type Type { get { return Entry.SettingType; } }

        internal bool IsKey
        {
            get { return Type == typeof(KeyCode) || Type == typeof(KeyboardShortcut); }
        }

        internal bool IsBool { get { return Type == typeof(bool); } }
        internal bool IsInt { get { return Type == typeof(int); } }
        
        /// <summary>A choice among named values. KeyCode is an enum too, and is a key.</summary>
        internal bool IsEnum { get { return Type.IsEnum && Type != typeof(KeyCode); } }

        internal static bool Supports(ConfigEntryBase entry)
        {
            Type type = entry.SettingType;
            return type == typeof(bool) || type == typeof(int) || type.IsEnum
                || type == typeof(KeyboardShortcut);
        }
    }

    /// <summary>One mod's page: the rows that survived the eligibility test, in the order added.</summary>
    internal sealed class ModPage
    {
        internal string Name;
        internal string Guid;
        internal readonly List<SettingRow> Rows = new List<SettingRow>();

        /// <summary>A key on this page is also another mod's, so the list marks it.</summary>
        internal bool SharesKey;
    }

    /// <summary>
    /// What the screen reads and does, apart from drawing it, so the console probe a Devkit
    /// scenario uses goes through exactly the code a click does.
    /// </summary>
    internal static class SettingsModel
    {
        internal static List<ModPage> Pages()
        {
            var byGuid = new Dictionary<string, ModPage>(StringComparer.Ordinal);
            var pages = new List<ModPage>();

            foreach (SettingRow row in SettingsPanel.Rows)
            {
                string name, guid;
                if (!Owner(row, out name, out guid)) continue;

                if (!Personal(row, guid)) continue;

                ModPage page;
                if (!byGuid.TryGetValue(guid, out page))
                {
                    page = new ModPage { Name = name, Guid = guid };
                    byGuid[guid] = page;
                    pages.Add(page);
                }

                page.Rows.Add(row);
            }

            pages.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            foreach (ModPage page in pages)
            {
                foreach (SettingRow row in page.Rows)
                {
                    if (!row.IsKey || KeyOf(row.Entry) == KeyCode.None) continue;
                    if (UsersOf(pages, row).Exists(user => user.Page != page)) page.SharesKey = true;
                }
            }

            return pages;
        }

        /// <summary>
        /// Who a row belongs to: the mod as it registered with the gate, else the plugin whose
        /// assembly made the call. False when neither is known yet, which is a mod that has not
        /// finished loading.
        /// </summary>
        private static bool Owner(SettingRow row, out string name, out string guid)
        {
            name = null;
            guid = null;

            string fingerprint = Suite.FingerprintOf(row.Owner);
            if (!string.IsNullOrEmpty(fingerprint))
            {
                foreach (ModEntry mod in Suite.Mods.Values)
                {
                    if (mod == null || mod.Fingerprint != fingerprint) continue;

                    name = mod.Name;
                    guid = mod.Guid;
                    return true;
                }
            }

            foreach (PluginInfo info in Chainloader.PluginInfos.Values)
            {
                if (info == null || info.Instance == null || info.Instance.GetType().Assembly != row.Owner) continue;

                name = info.Metadata.Name;
                guid = info.Metadata.GUID;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the host has no say in this entry. For a mod on the gate that is the config
        /// sync's own test, absence from the synced set. For one that is not, only a key.
        /// </summary>
        private static bool Personal(SettingRow row, string guid)
        {
            ModEntry mod;
            if (!Suite.Mods.TryGetValue(guid, out mod)) return row.IsKey;

            // Again here, since a mod that binds an entry after registering would otherwise be
            // judged against a synced set that has not seen it.
            Suite.AbsorbConfig(mod);
            return !mod.Synced.ContainsKey(Suite.Key(row.Entry));
        }

        internal struct User
        {
            internal ModPage Page;
            internal SettingRow Row;
        }

        /// <summary>Every listed key row on the same key as this one, this row included.</summary>
        internal static List<User> UsersOf(List<ModPage> pages, SettingRow row)
        {
            var users = new List<User>();
            KeyCode key = KeyOf(row.Entry);
            if (key == KeyCode.None) return users;

            foreach (ModPage page in pages)
                foreach (SettingRow other in page.Rows)
                    if (other.IsKey && KeyOf(other.Entry) == key)
                        users.Add(new User { Page = page, Row = other });

            return users;
        }

        // ------------------------------------------------------------------ keys ----------

        internal static KeyCode KeyOf(ConfigEntryBase entry)
        {
            return KeyIn(entry.BoxedValue);
        }

        internal static KeyCode DefaultKeyOf(ConfigEntryBase entry)
        {
            return KeyIn(entry.DefaultValue);
        }

        private static KeyCode KeyIn(object value)
        {
            if (value is KeyCode) return (KeyCode)value;
            if (value is KeyboardShortcut) return ((KeyboardShortcut)value).MainKey;
            return KeyCode.None;
        }

        /// <summary>
        /// A key as it would be compared: its main key and its modifiers, so Alt + F and F are
        /// two different binds. Null for None, which holds nothing and clashes with nothing.
        /// </summary>
        private static string ChordIn(object value)
        {
            KeyCode main = KeyIn(value);
            if (main == KeyCode.None) return null;

            var modifiers = new List<int>();
            if (value is KeyboardShortcut)
            {
                IEnumerable<KeyCode> held = ((KeyboardShortcut)value).Modifiers;
                if (held != null)
                    foreach (KeyCode modifier in held) modifiers.Add((int)modifier);
            }

            modifiers.Sort();

            var sb = new StringBuilder();
            sb.Append((int)main);
            foreach (int modifier in modifiers) sb.Append('+').Append(modifier);
            return sb.ToString();
        }

        private static bool IsMouse(KeyCode key)
        {
            return key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6;
        }

        /// <summary>
        /// The value a capture writes: the key itself for a KeyCode entry, and for a shortcut
        /// the key with the modifiers the entry already had, so rebinding Alt + F to G gives
        /// Alt + G rather than quietly dropping the Alt.
        /// </summary>
        private static object Proposed(SettingRow row, KeyCode key)
        {
            if (row.Type != typeof(KeyboardShortcut)) return key;

            var current = (KeyboardShortcut)row.Entry.BoxedValue;
            var modifiers = new List<KeyCode>();
            if (key != KeyCode.None && current.Modifiers != null)
                foreach (KeyCode modifier in current.Modifiers)
                    if (modifier != key) modifiers.Add(modifier);

            return new KeyboardShortcut(key, modifiers.ToArray());
        }

        /// <summary>
        /// Why this row may not take this value, or null.
        ///
        /// Two things refuse. A mouse button on a row whose mod does not read through ZInput.
        /// And a bind another listed entry currently holds, whole shortcut against whole shortcut,
        /// where the refusal names the holder. The one exception is a bind that is shared by
        /// design: the value is this row's own default AND every other row holding it has it as
        /// its own default too. Three mods ship on Left Alt on purpose, and resetting any of them,
        /// or capturing Left Alt by hand, has to land. The same rule covers Reset, so a row cannot
        /// be reset onto a key another mod has since moved onto, and it cannot be captured onto
        /// its default behind the back of a holder that is only borrowing it. The page's box under
        /// the rows warns about the by-design case instead of refusing it.
        /// </summary>
        private static string Refused(List<ModPage> pages, SettingRow row, object proposed)
        {
            string chord = ChordIn(proposed);
            if (chord == null) return null;

            if (!row.ReadsThroughZInput && IsMouse(KeyIn(proposed)))
                return "Mouse buttons cannot be used for " + Lower(row.Label)
                    + ", its mod does not read them.";

            bool mine = chord == ChordIn(row.Entry.DefaultValue);

            foreach (ModPage page in pages)
            {
                foreach (SettingRow other in page.Rows)
                {
                    if (ReferenceEquals(other, row) || !other.IsKey) continue;
                    if (ChordIn(other.Entry.BoxedValue) != chord) continue;
                    if (mine && ChordIn(other.Entry.DefaultValue) == chord) continue;

                    return KeyName(KeyIn(proposed)) + " is already " + page.Name + "'s " + Lower(other.Label) + ".";
                }
            }

            return null;
        }

        /// <summary>Bind a key, or say why not. Null means it was written.</summary>
        internal static string TryBind(List<ModPage> pages, SettingRow row, KeyCode key)
        {
            object proposed = Proposed(row, key);

            string refusal = Refused(pages, row, proposed);
            if (refusal != null) return refusal;

            row.Entry.BoxedValue = proposed;
            return null;
        }

        // ---------------------------------------------------------------- values ----------

        internal static string ValueText(SettingRow row)
        {
            return TextOf(row, row.Entry.BoxedValue);
        }

        internal static string DefaultText(SettingRow row)
        {
            return TextOf(row, row.Entry.DefaultValue);
        }

        private static string TextOf(SettingRow row, object value)
        {
            if (row.IsBool) return (bool)value ? "On" : "Off";
            if (row.IsInt) return ((int)value).ToString(CultureInfo.InvariantCulture);
            if (row.IsEnum) return Sentence(Humanize(value.ToString()));

            KeyCode key = KeyIn(value);
            string name = KeyName(key);

            if (!(value is KeyboardShortcut)) return name;

            var sb = new StringBuilder();
            IEnumerable<KeyCode> modifiers = ((KeyboardShortcut)value).Modifiers;
            if (modifiers != null)
                foreach (KeyCode modifier in modifiers)
                    sb.Append(KeyName(modifier)).Append(" + ");

            return sb.Append(name).ToString();
        }

        internal static bool IsDefault(SettingRow row)
        {
            return Equals(row.Entry.BoxedValue, row.Entry.DefaultValue);
        }

        /// <summary>Back to the default, or the refusal that stops a key row doing so. Null means it was written.</summary>
        internal static string Reset(List<ModPage> pages, SettingRow row)
        {
            if (row.IsKey)
            {
                string refusal = Refused(pages, row, row.Entry.DefaultValue);
                if (refusal != null) return refusal;
            }

            row.Entry.BoxedValue = row.Entry.DefaultValue;
            return null;
        }

        internal static void Toggle(SettingRow row)
        {
            row.Entry.BoxedValue = !(bool)row.Entry.BoxedValue;
        }

        /// <summary>The next or previous choice, wrapping round.</summary>
        internal static void Cycle(SettingRow row, int step)
        {
            Array values = Enum.GetValues(row.Type);
            int at = Array.IndexOf(values, row.Entry.BoxedValue);
            int next = ((at + step) % values.Length + values.Length) % values.Length;
            row.Entry.BoxedValue = values.GetValue(next);
        }

        /// <summary>One step up or down, held inside the entry's range when it has one.</summary>
        internal static void Nudge(SettingRow row, int step)
        {
            int value = (int)row.Entry.BoxedValue + step;

            var range = row.Entry.Description != null
                ? row.Entry.Description.AcceptableValues as AcceptableValueRange<int> : null;
            if (range != null) value = Mathf.Clamp(value, range.MinValue, range.MaxValue);

            row.Entry.BoxedValue = value;
        }

        internal static bool CanNudge(SettingRow row, int step)
        {
            int value = (int)row.Entry.BoxedValue;

            var range = row.Entry.Description != null
                ? row.Entry.Description.AcceptableValues as AcceptableValueRange<int> : null;
            if (range == null) return true;

            return step < 0 ? value > range.MinValue : value < range.MaxValue;
        }

        // ------------------------------------------------------------------ words ---------

        /// <summary>"TopCentre" as "Top centre", "LeftAlt" as "Left Alt".</summary>
        internal static string Humanize(string name)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool afterLower = i > 0 && char.IsLower(name[i - 1]);

                if (i > 0 && afterLower && (char.IsUpper(c) || char.IsDigit(c))) sb.Append(' ');
                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>"Top Centre" as "Top centre": a choice reads as a phrase, a key as its name.</summary>
        internal static string Sentence(string text)
        {
            return text.Length < 2 ? text : text.Substring(0, 1) + text.Substring(1).ToLowerInvariant();
        }

        internal static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.None: return "None";
                case KeyCode.KeypadPeriod: return "Keypad .";
                case KeyCode.KeypadPlus: return "Keypad +";
                case KeyCode.KeypadMinus: return "Keypad -";
                case KeyCode.KeypadMultiply: return "Keypad *";
                case KeyCode.KeypadDivide: return "Keypad /";
                case KeyCode.KeypadEquals: return "Keypad =";
            }

            string name = key.ToString();
            if (name.StartsWith("Alpha", StringComparison.Ordinal) && name.Length == 6) return name.Substring(5);

            return Humanize(name);
        }

        /// <summary>
        /// A phrase set into the middle of a sentence: its first letter lower, unless that
        /// letter starts something said in capitals ("XP bar", "H").
        /// </summary>
        internal static string Lower(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.Length > 1 && char.IsUpper(text[1])) return text;
            if (text.Length == 1) return text;

            return char.ToLowerInvariant(text[0]) + text.Substring(1);
        }

        // ---------------------------------------------------------------- summary ---------

        /// <summary>The line under a mod's name in the list: its rows as short phrases in a row.</summary>
        internal static string SummaryOf(ModPage page)
        {
            var parts = new List<string>();

            foreach (SettingRow row in page.Rows)
            {
                string part = Phrase(row);
                if (!string.IsNullOrEmpty(part)) parts.Add(part);
            }

            if (parts.Count == 0) return "";

            for (int i = 0; i < parts.Count; i++)
                parts[i] = i == 0 ? char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1) : Lower(parts[i]);

            return string.Join(", ", parts.ToArray());
        }

        private static string Phrase(SettingRow row)
        {
            string value = ValueText(row);

            if (row.IsBool)
            {
                bool on = (bool)row.Entry.BoxedValue;

                if (row.Summary != null && row.Summary.IndexOf('|') >= 0)
                {
                    string[] sides = row.Summary.Split('|');
                    return on ? sides[0] : sides[1];
                }

                if (row.Summary != null) return row.Summary;
                return row.Label + (on ? " on" : " off");
            }

            if (row.Summary != null) return row.Summary.Replace("{0}", value);
            if (row.IsEnum) return value;

            return row.Label + " " + value;
        }

        // ---------------------------------------------------------------- the page --------

        /// <summary>
        /// What the Settings entry says when the panel cannot be drawn: every listed setting with
        /// its value and its default, and where the file is. Less than the panel, and nothing in
        /// it is false.
        /// </summary>
        internal static string TextPage()
        {
            List<ModPage> pages = Pages();
            var sb = new StringBuilder();

            sb.Append("Settings you can change here apply at once and are yours alone. ")
              .Append("The panel could not be drawn, so they are listed instead; each mod's file ")
              .Append("in BepInEx\\config holds the same values.\n");

            if (pages.Count == 0) return sb.Append("\nNo mod has listed a setting.").ToString();

            foreach (ModPage page in pages)
            {
                sb.Append("\n").Append(page.Name).Append("\n");

                foreach (SettingRow row in page.Rows)
                    sb.Append("  ").Append(row.Label).Append(": ").Append(ValueText(row))
                      .Append(" (default ").Append(DefaultText(row)).Append(")\n");
            }

            return sb.ToString();
        }

        private static readonly string[] Words =
            { "no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };

        internal static string Count(int n)
        {
            return n >= 0 && n < Words.Length ? Words[n] : n.ToString(CultureInfo.InvariantCulture);
        }
    }
}
