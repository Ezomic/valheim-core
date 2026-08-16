using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    ///     Suite.Register(PluginGuid, PluginName, PluginVersion, config);
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
        /// <param name="owner">
        /// The mod's own assembly, used for its build fingerprint. Left null it is taken from
        /// the caller, which is right for every normal case.
        /// </param>
        // NoInlining is load-bearing, not decoration: GetCallingAssembly answers relative to
        // this frame, and a JIT that inlined Register into the caller would make it report
        // Core rather than the mod - producing one fingerprint shared by all nine, which
        // would compare equal always and quietly detect nothing.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Register(
            string guid,
            string name,
            string version,
            ConfigFile config,
            Requirement requirement = Requirement.Everyone,
            Assembly owner = null)
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
            entry.Fingerprint = FingerprintOf(owner ?? Assembly.GetCallingAssembly());

            // Everything, not an opt-in list.
            //
            // It was opt-in and almost nothing opted in: two entries across thirteen mods. A
            // setting that changes what happens in the world has to match on both ends or the
            // two disagree silently, and the way that shows up is not an error - it is a
            // client on a cheapened level curve being told it has picks the server never
            // granted, for an evening, with both logs looking reasonable.
            //
            // Forcing all of it is safe because of how it is applied: values are swapped in
            // memory, never written to the player's file, and put back on disconnect. A host
            // deciding your keybind lasts exactly as long as you are on that host.
            AbsorbConfig(entry);

            _lastRegistered = guid;

            CorePlugin.Log.LogInfo("Registered " + name + " " + version
                + " (" + requirement + ") build " + entry.Fingerprint);
        }

        /// <summary>
        /// A short, stable identity for the exact build of an assembly.
        ///
        /// The module version id, which the compiler stamps into every assembly it produces.
        /// Two things make it the right choice over hashing the file. It costs a property
        /// read rather than opening and digesting a DLL during startup. And because the .NET
        /// SDK builds deterministically by default, it is a function of the compilation
        /// inputs - so the same source produces the same id, and any real change produces a
        /// different one. That is exactly the question being asked.
        ///
        /// Why this exists at all: the gate used to compare version strings, and a version
        /// string is whatever somebody last remembered to edit. During development every
        /// build says 0.1.0, so a client three commits ahead of the server matched perfectly
        /// and connected - which is the case that actually happens, and the one a version
        /// check is least able to see.
        ///
        /// Truncated for logs. Twelve hex digits is 48 bits; two different builds colliding
        /// is not a thing that will happen to anyone.
        /// </summary>
        private static string FingerprintOf(Assembly assembly)
        {
            if (assembly == null) return "";

            try
            {
                return assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12);
            }
            catch (Exception e)
            {
                // A missing fingerprint is compared as "unknown" rather than as a mismatch,
                // so failing here costs the build check and nothing else.
                CorePlugin.Log.LogWarning("Could not read a build id: " + e.Message);
                return "";
            }
        }

        /// <summary>
        /// Mark an entry as the host's to decide.
        ///
        /// Sync anything a mismatch would desync: item data, stack sizes, ranges that decide
        /// whether two clients agree a chest is in reach. Leave keybinds, messages and
        /// anything cosmetic alone - forcing a host's keybinds onto a guest is the kind of
        /// sync that gets a mod uninstalled.
        /// </summary>
        /// <summary>
        /// Declare the contents of a data file this mod reads, so the gate can tell whether
        /// both ends have the same one.
        ///
        /// <code>
        /// Suite.Data(File.ReadAllText(cardsPath));
        /// </code>
        ///
        /// The version gate already catches two ends running different builds. It cannot catch
        /// two ends running the same build over different data, and several mods here are a
        /// DLL plus a text file that decides what the mod actually does. Boon is the case that
        /// prompted it: its catalogue names what every rank is worth, effects are applied
        /// client-side from that file, and the server only ever checks the rank - so a client
        /// with an edited line gets whatever it wrote there.
        ///
        /// A hash rather than the file: the handshake is not the place to ship content, and
        /// all the gate needs to know is same or not the same.
        /// </summary>
        public static void Data(string contents, string guid = null)
        {
            guid = guid ?? _lastRegistered;
            if (string.IsNullOrEmpty(guid)) return;

            ModEntry entry;
            if (!Mods.TryGetValue(guid, out entry)) return;

            entry.Data = HashOf(contents);
            CorePlugin.Log.LogInfo(entry.Name + " data " + entry.Data + ".");
        }

        /// <summary>
        /// A short, stable, order-dependent hash. Not cryptographic - this is a mismatch
        /// check, not a defence against someone building a file to collide with yours, and
        /// anyone able to do that could patch the assembly instead.
        ///
        /// Line endings are normalised first: the same file checked out on two machines can
        /// differ by CRLF alone, and refusing a connection over that would be a bug rather
        /// than a catch.
        /// </summary>
        private static string HashOf(string contents)
        {
            if (string.IsNullOrEmpty(contents)) return "";

            contents = contents.Replace("\r\n", "\n").Replace("\r", "\n");

            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < contents.Length; i++)
                {
                    hash ^= contents[i];
                    hash *= 16777619;
                }

                return hash.ToString("x8");
            }
        }

        /// <summary>
        /// Marks an entry as synced. A formality now: registering a mod syncs its whole
        /// config, so everything is already covered. Kept because mods call it, and because
        /// naming an entry here is a way of saying out loud that it is the host's.
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
        /// <summary>
        /// Take every entry a mod has bound. Called again when the manifest is built, because
        /// a mod that binds config after registering would otherwise never have those entries
        /// carried - and the order of two lines in someone's Awake is not a thing this should
        /// depend on.
        /// </summary>
        internal static void AbsorbConfig(ModEntry entry)
        {
            if (entry == null || entry.Config == null) return;

            // Through the indexer rather than TryGetEntry: that one is generic over the
            // setting's type, which is exactly what is not known when walking a whole file.
            foreach (ConfigDefinition definition in entry.Config.Keys)
            {
                ConfigEntryBase bound = entry.Config[definition];
                if (bound == null) continue;

                entry.Synced[definition.Section + "." + definition.Key] = bound;
            }
        }

        internal static string Key(ConfigEntryBase entry)
        {
            return entry.Definition.Section + "." + entry.Definition.Key;
        }

        /// <summary>
        /// Say why the connection about to fail is failing, on the screen that announces it.
        ///
        /// Valheim's refusal screen has one line per ConnectionStatus and no room for detail,
        /// so a mod that refuses somebody can otherwise only write to a log the refused player
        /// may not be able to read. Call this just before dropping them.
        ///
        /// Client-side only in effect - it is the client that draws the screen - and harmless
        /// to call on a server, where nothing ever consumes it.
        /// </summary>
        public static void ExplainRefusal(string reason)
        {
            ConnectError.Expect(reason);
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
