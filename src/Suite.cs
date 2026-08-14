using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace Ezomic.Core
{
    /// <summary>
    /// Everything a mod in this suite calls. Three methods, and two of them are optional.
    ///
    /// <code>
    /// private void Awake()
    /// {
    ///     HoardConfig.Bind(Config);
    ///     Suite.Register(PluginGuid, PluginName, PluginVersion, Config);
    ///     Suite.Sync(HoardConfig.StackMultiplier);
    /// }
    /// </code>
    ///
    /// Registering is what puts a mod on the version gate. Syncing an entry is what makes
    /// the host's value the one that counts. A mod that does neither still works - it just
    /// gets none of this - so wiring the nine can happen one at a time.
    /// </summary>
    public static class Suite
    {
        internal static readonly Dictionary<string, ModEntry> Mods =
            new Dictionary<string, ModEntry>(StringComparer.Ordinal);

        /// <summary>Guid of the mod that registered last, so Sync needs no argument for it.</summary>
        private static string _lastRegistered;

        /// <summary>
        /// Declare a mod to the gate.
        ///
        /// Call it after binding config, because <see cref="Sync"/> needs entries that
        /// already exist, and before anything network-facing. Awake is the right place for
        /// both.
        /// </summary>
        public static void Register(
            string guid,
            string name,
            string version,
            ConfigFile config,
            Requirement requirement = Requirement.Everyone)
        {
            if (string.IsNullOrEmpty(guid)) throw new ArgumentNullException(nameof(guid));

            // Re-registering is a reload, not a bug. Keep the entry so anything already
            // synced against it survives, and take the new metadata.
            ModEntry entry;
            if (!Mods.TryGetValue(guid, out entry))
            {
                entry = new ModEntry();
                Mods[guid] = entry;
            }

            entry.Guid = guid;
            entry.Name = name;
            entry.Version = version;
            entry.Requirement = requirement;
            entry.Config = config;

            _lastRegistered = guid;

            CorePlugin.Log.LogInfo("Registered " + name + " " + version
                + " (" + requirement + ")");
        }

        /// <summary>
        /// Mark an entry as the host's to decide.
        ///
        /// Sync anything a mismatch would desync: item data, stack sizes, ranges that decide
        /// whether two clients agree a chest is in reach. Leave keybinds, messages and
        /// anything cosmetic alone - forcing a host's keybinds onto a guest is the kind of
        /// sync that gets a mod uninstalled.
        /// </summary>
        public static void Sync(ConfigEntryBase entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            if (_lastRegistered == null || !Mods.ContainsKey(_lastRegistered))
                throw new InvalidOperationException(
                    "Suite.Sync was called before Suite.Register. Register the mod first.");

            Mods[_lastRegistered].Synced[Key(entry)] = entry;
        }

        /// <summary>Syncs several at once, for the common case of a whole section.</summary>
        public static void Sync(params ConfigEntryBase[] entries)
        {
            foreach (ConfigEntryBase entry in entries) Sync(entry);
        }

        /// <summary>
        /// The wire name of an entry. Section and key, because that pair is what a config
        /// file is addressed by and what survives a mod reordering its own fields.
        /// </summary>
        internal static string Key(ConfigEntryBase entry)
        {
            return entry.Definition.Section + "." + entry.Definition.Key;
        }

        /// <summary>
        /// A tag for ConfigurationManager, so the in-game window is ordered and readable
        /// rather than alphabetical.
        ///
        /// ConfigurationManager finds this by duck typing - it reflects over whatever
        /// objects are in a ConfigDescription's tags and reads any field whose name it
        /// recognises. That is why this is a plain class of public fields and not an
        /// attribute, and why it costs nothing when ConfigurationManager is absent.
        /// </summary>
        public static ConfigurationManagerAttributes Display(
            int order = 0, bool advanced = false, string name = null)
        {
            return new ConfigurationManagerAttributes
            {
                // Higher sorts first, which is backwards from every other Order in the
                // world, so callers pass a normal ascending number and it is flipped here.
                Order = -order,
                IsAdvanced = advanced,
                DispName = name
            };
        }
    }

    /// <summary>
    /// Recognised by ConfigurationManager by field name. Only the fields actually used are
    /// declared; the class is matched structurally, so a partial one is fine.
    /// </summary>
    public sealed class ConfigurationManagerAttributes
    {
        public int? Order;
        public bool? Browsable;
        public bool? IsAdvanced;
        public bool? HideDefaultButton;
        public string DispName;

        /// <summary>
        /// Flipped on while a host is dictating this value, so the slider greys out instead
        /// of letting someone drag it and watch it snap back with no explanation.
        /// </summary>
        public bool? ReadOnly;
    }
}
