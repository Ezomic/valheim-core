using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace Ezomic.Core
{
    /// <summary>
    /// The one place the handshake lives.
    ///
    /// Every mod here needs the same two things from multiplayer - refuse a client whose
    /// version does not match, and make the host's settings the ones that count - and both
    /// are built out of a single RPC pair on the connection. Nine copies of that would be
    /// nine chances to get the handshake ordering wrong, and worse, nine RPCs racing each
    /// other on the same peer. So it is registered once here and the mods declare what they
    /// want rather than how it happens.
    ///
    /// This is a hard dependency of every Ezomic mod. Mod managers resolve it from the
    /// manifest, so a player never installs it deliberately.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    // No BepInProcess. It used to say valheim.exe, and that quietly defeated the entire
    // point of this plugin: a dedicated server runs valheim_server.exe, so Core never loaded
    // there, so the gate in NetworkPatches only ever ran on a listen host. Every dedicated
    // server in the family was unguarded, and RPC_PeerInfo's IsServer branch - the only
    // branch that can actually refuse a connection - was unreachable.
    //
    // It also breaks any mod that declares Core a hard dependency and carries no
    // BepInProcess of its own: on a dedicated server the dependency is simply absent, so
    // BepInEx refuses to load that mod at all. That is how this was found.
    public class CorePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ezomic.valheim.core";
        public const string PluginName = "Core";
        public const string PluginVersion = "1.0.1";
        public const string PluginAuthor = "Robbin Thijssen";

        internal static ManualLogSource Log;

        /// <summary>
        /// Off is a supported answer. A player running these solo, or an admin who would
        /// rather sort mismatches out by talking to people, should not be forced through a
        /// gate that can only ever reject them.
        /// </summary>
        internal static ConfigEntry<bool> EnforceVersions;
        internal static ConfigEntry<bool> EnforceBuilds;
        internal static ConfigEntry<bool> EnforceConfig;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            EnforceVersions = Config.Bind("Multiplayer", "EnforceVersions", true,
                "Refuse a connection when the client and the server disagree about which "
                + "Ezomic mods are installed, or about their versions. Turning this off does "
                + "not make a mismatch safe; it makes it silent.");

            EnforceBuilds = Config.Bind("Multiplayer", "EnforceBuilds", true,
                "Also refuse a connection when both ends claim the same version but are "
                + "actually different builds.\n"
                + "A version string is whatever was last remembered to be edited, and during "
                + "development every build says 0.1.0 - so a client three commits ahead of "
                + "the server matches perfectly and connects. That is the mismatch that "
                + "actually happens, and a version check is the least able to see it. This "
                + "compares the compiler's build id instead, which no one has to remember.\n"
                + "Turn it off if you build the mods yourself on more than one machine: "
                + "deterministic builds also depend on source paths, so the same commit "
                + "checked out to a different folder produces a different id.");

            EnforceConfig = Config.Bind("Multiplayer", "EnforceConfig", true,
                "The host's settings win. Clients keep their own file untouched and get it "
                + "back the moment they disconnect - nothing is overwritten on disk.");

            // Core puts itself on its own gate. It was not on it before, which left the one
            // mod every other mod depends on as the only one whose mismatch went unreported -
            // and a Core mismatch is worse than any of theirs, because it is the handshake
            // itself that differs.
            Suite.Register(PluginGuid, PluginName, PluginVersion, Config, Requirement.Everyone,
                typeof(CorePlugin).Assembly);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(NetworkPatches));
            _harmony.PatchAll(typeof(ConfigSync));
            _harmony.PatchAll(typeof(ConnectError));

            // InventoryRows was the one class here driven purely from Update, so it had
            // never needed registering. It does now: its Player.Load prefix is what stops
            // a character load destroying every item sitting in a claimed row.
            _harmony.PatchAll(typeof(InventoryRows));

            Log.LogInfo(PluginName + " " + PluginVersion + " by " + PluginAuthor + " - ready.");
        }

        /// <summary>
        /// Core owns the timing for anything shared, so no mod has to. Both of these are
        /// cheap when nothing has changed - they compare against what they last wrote and
        /// return.
        /// </summary>
        private void Update()
        {
            InventoryRows.Tick();
            InventoryRows.Backdrop.Tick();
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }

    /// <summary>How much of a mod has to be on both ends of a connection.</summary>
    public enum Requirement
    {
        /// <summary>
        /// Both sides need it, at the same version. Anything that registers a prefab or
        /// changes item data is this, whether it looks like it or not: a client that cannot
        /// resolve a prefab hash discards the ZDO as junk rather than failing loudly.
        /// </summary>
        Everyone,

        /// <summary>
        /// Only the host needs it. Clients without it are let in, and clients *with* it are
        /// still checked against the host - a half-installed group is the case that actually
        /// happens, and it is worse than nobody having it.
        /// </summary>
        HostOnly
    }

    /// <summary>What one mod told Core about itself.</summary>
    internal sealed class ModEntry
    {
        internal string Guid;
        internal string Name;
        internal string Version;
        internal Requirement Requirement;
        internal ConfigFile Config;

        /// <summary>
        /// Short id of the exact build, from the assembly's module version id. Empty when it
        /// could not be read, which is compared as "unknown" rather than as a mismatch.
        /// </summary>
        internal string Fingerprint;

        /// <summary>
        /// Hash of a data file the mod reads, when it declares one through Suite.Data. Empty
        /// for a mod that is only a DLL, and compared as "unknown" rather than a mismatch, so
        /// an older Core on the far end costs the check and nothing else.
        /// </summary>
        internal string Data;

        /// <summary>Entries the host dictates, keyed by "section.key" as sent on the wire.</summary>
        /// <summary>
        /// Every entry of this mod's config, filled in at registration. All of it is the
        /// host's to decide - see Suite.Register.
        /// </summary>
        internal readonly Dictionary<string, ConfigEntryBase> Synced =
            new Dictionary<string, ConfigEntryBase>();

    }
}
