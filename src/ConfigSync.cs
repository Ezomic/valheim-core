using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace Ezomic.Core
{
    /// <summary>
    /// The host's settings, for as long as you are on the host's world.
    ///
    /// Nothing is written to the client's config file. The values are swapped in memory, the
    /// originals are held, and they go back on disconnect - so a player who joins a server
    /// with x4 stacks does not quietly find their own single-player world changed the next
    /// evening. That property is the reason this does not simply rewrite the file, which
    /// would be a great deal less code.
    /// </summary>
    internal static class ConfigSync
    {
        /// <summary>What the client had before the host said otherwise.</summary>
        private static readonly Dictionary<ConfigEntryBase, object> Original =
            new Dictionary<ConfigEntryBase, object>();

        /// <summary>What the host said, so a local edit can be put straight back.</summary>
        private static readonly Dictionary<ConfigEntryBase, object> Imposed =
            new Dictionary<ConfigEntryBase, object>();

        /// <summary>Config files already being watched, so a reconnect does not subscribe twice.</summary>
        private static readonly HashSet<ConfigFile> Watched = new HashSet<ConfigFile>();

        /// <summary>Set while this class is the one doing the writing.</summary>
        private static bool _applying;

        /// <summary>Every synced entry of every registered mod, as strings.</summary>
        internal static ZPackage BuildConfig()
        {
            List<string[]> rows = new List<string[]>();

            foreach (KeyValuePair<string, ModEntry> pair in Suite.Mods)
            {
                foreach (KeyValuePair<string, ConfigEntryBase> synced in pair.Value.Synced)
                {
                    string value;
                    try
                    {
                        value = TomlTypeConverter.ConvertToString(
                            synced.Value.BoxedValue, synced.Value.SettingType);
                    }
                    catch (Exception e)
                    {
                        // One unserialisable entry must not cost the client every other
                        // value. Skipping it leaves that setting local, which is the same
                        // behaviour as never having synced it.
                        CorePlugin.Log.LogWarning("Cannot send " + pair.Key + " "
                            + synced.Key + ": " + e.Message);
                        continue;
                    }

                    rows.Add(new[] { pair.Key, synced.Key, value });
                }
            }

            ZPackage pkg = new ZPackage();
            pkg.Write(rows.Count);

            foreach (string[] row in rows)
            {
                pkg.Write(row[0]);
                pkg.Write(row[1]);
                pkg.Write(row[2]);
            }

            return pkg;
        }

        /// <summary>Client side. Everything the host sent, applied at once.</summary>
        internal static void ReceiveConfig(ZRpc rpc, ZPackage pkg)
        {
            if (ZNet.instance != null && ZNet.instance.IsServer()) return;
            if (!CorePlugin.EnforceConfig.Value) return;

            int count = pkg.ReadInt();
            int applied = 0;

            _applying = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    string guid = pkg.ReadString();
                    string key = pkg.ReadString();
                    string raw = pkg.ReadString();

                    if (Apply(guid, key, raw)) applied++;
                }
            }
            finally
            {
                _applying = false;
            }

            CorePlugin.Log.LogInfo("Host settings applied: " + applied + " of " + count
                + ". Your own config file is untouched and comes back on disconnect.");
        }

        private static bool Apply(string guid, string key, string raw)
        {
            ModEntry mod;
            if (!Suite.Mods.TryGetValue(guid, out mod)) return false;

            ConfigEntryBase entry;
            if (!mod.Synced.TryGetValue(key, out entry)) return false;

            object value;
            try
            {
                value = TomlTypeConverter.ConvertToValue(raw, entry.SettingType);
            }
            catch (Exception e)
            {
                CorePlugin.Log.LogWarning("Cannot read host value for " + key + ": " + e.Message);
                return false;
            }

            // Only the first override records the original. A second push during the same
            // session - a host editing live - must not record the previous host value as
            // what to restore.
            if (!Original.ContainsKey(entry)) Original[entry] = entry.BoxedValue;

            Imposed[entry] = value;
            entry.BoxedValue = value;
            SetReadOnly(entry, true);

            Watch(mod.Config);
            return true;
        }

        /// <summary>
        /// A local edit while the host is deciding gets put back. Without this, the in-game
        /// config window happily lets someone drag a slider that has no effect, and the mod
        /// looks broken rather than governed.
        /// </summary>
        private static void Watch(ConfigFile config)
        {
            if (config == null || !Watched.Add(config)) return;

            config.SettingChanged += (sender, args) =>
            {
                if (_applying) return;

                object imposed;
                if (!Imposed.TryGetValue(args.ChangedSetting, out imposed)) return;

                _applying = true;
                try
                {
                    args.ChangedSetting.BoxedValue = imposed;
                }
                finally
                {
                    _applying = false;
                }
            };
        }

        /// <summary>
        /// Leaving a server gives everything back. Shutdown covers every way out - quitting
        /// to the menu, being kicked, the connection dropping - because they all end here.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        private static void RestoreOnShutdown()
        {
            if (Original.Count == 0) return;

            _applying = true;
            try
            {
                foreach (KeyValuePair<ConfigEntryBase, object> pair in Original)
                {
                    pair.Key.BoxedValue = pair.Value;
                    SetReadOnly(pair.Key, false);
                }
            }
            finally
            {
                _applying = false;
            }

            CorePlugin.Log.LogInfo("Restored " + Original.Count + " of your own settings.");

            Original.Clear();
            Imposed.Clear();
        }

        /// <summary>
        /// Greys the entry out in ConfigurationManager, if the mod gave it a display tag and
        /// if ConfigurationManager is even installed. Both are optional, so this is a
        /// best-effort nicety and never a reason to fail.
        /// </summary>
        private static void SetReadOnly(ConfigEntryBase entry, bool readOnly)
        {
            if (entry.Description == null || entry.Description.Tags == null) return;

            foreach (object tag in entry.Description.Tags)
            {
                ConfigurationManagerAttributes attributes = tag as ConfigurationManagerAttributes;
                if (attributes != null) attributes.ReadOnly = readOnly;
            }
        }
    }
}
