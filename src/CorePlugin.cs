using System;
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
        public const string PluginVersion = "1.0.2";
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

        /// <summary>
        /// The crash net. See <see cref="SaveGuard"/> for why it is here rather than in a mod
        /// of its own: it registers nothing, shows nothing, and every player in the pack
        /// already has this DLL.
        /// </summary>
        internal static ConfigEntry<bool> SaveAfterChanges;
        internal static ConfigEntry<float> SaveGap;

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

            SaveAfterChanges = Config.Bind("Character", "SaveAfterChanges", true,
                "Write your character file shortly after your inventory changes, instead of "
                + "only on the game's own thirty-minute timer.\n"
                + "Vanilla saves your character every 1800 seconds, when you quit cleanly, "
                + "and when you sleep. A crash between two of those throws away everything "
                + "since the last one. On a server it is worse than a rollback: the world "
                + "saved on its own schedule, so loot you took out of a chest before the "
                + "crash is gone from the chest and gone from you.\n"
                + "This does not replace the thirty-minute save, it adds to it - the map is "
                + "still written on the vanilla schedule, because recompressing it is the "
                + "expensive part of a save and fog is not worth paying for every minute.");

            SaveGap = Config.Bind("Character", "SaveGapSeconds", 30f,
                "The shortest time between two of those saves, in seconds.\n"
                + "This is what stops a trip to the base becoming one save per chest. "
                + "Emptying your pack into a wall of chests changes your inventory twenty "
                + "times in under a minute; at 30 that is two writes and you still lose at "
                + "most half a minute. Lower it to trade more writes for a smaller window - "
                + "each save is a file write and, on a cloud save, a Steam round trip.");

            // Core puts itself on its own gate. It was not on it before, which left the one
            // mod every other mod depends on as the only one whose mismatch went unreported -
            // and a Core mismatch is worse than any of theirs, because it is the handshake
            // itself that differs.
            Suite.Register(PluginGuid, PluginName, PluginVersion, Config, Requirement.Everyone,
                typeof(CorePlugin).Assembly);

            // Personal, and client-side in full: it decides when this machine writes its own
            // character file, which is nobody else's business and cannot desync anything.
            // Left synced it would be a host silently deciding how much of a guest's evening
            // a crash is allowed to eat.
            Suite.Local(SaveAfterChanges, SaveGap);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(NetworkPatches));
            _harmony.PatchAll(typeof(ConfigSync));
            _harmony.PatchAll(typeof(ConnectError));

            // InventoryRows was the one class here driven purely from Update, so it had
            // never needed registering. It does now: its Player.Load prefix is what stops
            // a character load destroying every item sitting in a claimed row.
            _harmony.PatchAll(typeof(InventoryRows));
            _harmony.PatchAll(typeof(InventoryLoad));
            _harmony.PatchAll(typeof(SaveGuard));

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
            SaveGuard.Tick();
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
        /// The entries of this mod's config the host decides, filled in at registration.
        /// All of them bar keybinds and whatever the mod held back with Suite.Local - see
        /// Suite.Register.
        /// </summary>
        internal readonly Dictionary<string, ConfigEntryBase> Synced =
            new Dictionary<string, ConfigEntryBase>();

        /// <summary>
        /// Entries the mod declared as the player's own, through Suite.Local. Kept as keys
        /// rather than entries because a mod may declare one before it is bound, and because
        /// this has to survive the mod re-registering.
        /// </summary>
        internal readonly HashSet<string> Local = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Entries the mod insisted on syncing through Suite.Sync, which is what overrides
        /// the personal-setting exception. Empty for every mod that has no reason to.
        /// </summary>
        internal readonly HashSet<string> Forced = new HashSet<string>(StringComparer.Ordinal);
    }
}
