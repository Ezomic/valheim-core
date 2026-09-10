using System;
using System.Collections.Generic;
using System.Reflection;
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
        public const string PluginVersion = "1.2.1";
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
        /// Whether the handshake patches actually applied this session.
        ///
        /// It exists because of how a Core failure used to read in the log. Every mod calls
        /// Suite.Register from its own Awake and gets back "Registered Yoke 1.1.0 (Everyone)
        /// build a1b2c3", whether or not the ZNet patches that make that registration MEAN
        /// anything ever went on. Twelve confident lines describing a gate that is not there,
        /// and the only tell was the absence of one earlier line nobody greps for by habit.
        /// Register consults this now, so the mod list itself says when it is unenforced.
        /// </summary>
        internal static bool GateWired { get; private set; }

        /// <summary>Whether the host's settings will actually be imposed. Same argument.</summary>
        internal static bool ConfigWired { get; private set; }

        /// <summary>
        /// Whether the extra inventory rows may be driven at all. False unless BOTH the rows and
        /// their load protection applied, because rows claimed without that protection is the one
        /// combination that silently destroys items.
        /// </summary>
        internal static bool RowsSafe { get; private set; }

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

            _harmony = new Harmony(PluginGuid);

            // Five groups, each applied so that its failure cannot take the others with it,
            // and the inventory pair FIRST. Both of those are deliberate, and they were not
            // always so.
            //
            // It used to be five bare PatchAll calls in the other order, and that arrangement
            // has a failure mode worth naming because a game update is exactly what triggers
            // it. A throw in NetworkPatches - one renamed ZNet method, one renamed `rpc` or
            // `peer` parameter, since Harmony injects by name - propagated out of Awake, so
            // the last two calls never ran. But the component was still added and Update()
            // still ticked, so InventoryRows kept claiming extra rows from Update while the
            // Player.Load prefix that protects those rows was absent. Inventory.Load's
            // positional AddItem refuses anything outside the grid and its caller discards
            // that result, so the item is instantiated, refused, destroyed, and the container
            // saves again without it. Rows claimed with no load protection is the one
            // combination that eats the bottom row on every relog and at every grave.
            //
            // So: protect the data first, then wire the network. If the gate cannot be
            // applied, Core loses the gate. It no longer also loses the thing standing
            // between a saved inventory and silent item destruction.
            bool loadWired = Apply("inventory load protection", typeof(InventoryLoad));
            bool rowsWired = Apply("inventory rows", typeof(InventoryRows));

            // The one that actually keeps the granted rows, on Valheim 1.0. Vanilla asserts the
            // inventory height from the character's own key inside SpawnPlayer and drops
            // everything below it in the same frame, so writing the height from Update is a race
            // Core always loses. Without this group the extra rows are not merely absent - they
            // empty onto the ground - which is why RowsSafe requires it rather than treating it
            // as an improvement.
            // Both classes, because Harmony.PatchAll(Type) does not recurse into nested types
            // and does not follow a holder class to its siblings - it patches exactly the type it
            // is handed. Patching VanillaRows alone attached nothing twice over.
            bool vanillaWired = Apply("inventory row claim", typeof(VanillaRowsSize))
                              & Apply("inventory row eviction fence", typeof(VanillaRowsDrop));

            // Applied is not attached. Both of these decide whether a player keeps what is in a
            // granted row, so they are checked rather than assumed.
            Verify(AccessTools.Method(typeof(Player), nameof(Player.SetInventorySize)),
                   AccessTools.Method(typeof(Humanoid), nameof(Humanoid.DropInvalidItems)));

            // InventoryRows without InventoryLoad is worse than either alone, for the reason
            // above, so the rows do not get to run half-protected.
            //
            // Refused by not driving them rather than by unpatching them. Harmony's Unpatch
            // wants the original method, and an unpatch that itself throws would leave the rows
            // live while this code had already logged that they were rolled back - a log that
            // lies about item safety is worse than the bug. RowsSafe is read from Update, which
            // is the only thing that makes InventoryRows do anything at all.
            RowsSafe = rowsWired && loadWired && vanillaWired;

            if (rowsWired && !(loadWired && vanillaWired))
                Log.LogError("Core applied its extra inventory rows but NOT the load protection "
                    + "that keeps them safe, which would destroy every item in a claimed row on "
                    + "the next relog and at every grave. The rows are being left undriven - the "
                    + "inventory stays vanilla and nothing any mod claimed will be honoured.");

            GateWired = Apply("version gate", typeof(NetworkPatches));
            ConfigWired = Apply("config sync", typeof(ConfigSync));
            Apply("connect-error text", typeof(ConnectError));

            // Core puts itself on its own gate. It was not on it before, which left the one
            // mod every other mod depends on as the only one whose mismatch went unreported -
            // and a Core mismatch is worse than any of theirs, because it is the handshake
            // itself that differs.
            //
            // After the patches now, not before: registering first meant Core announced itself
            // into a gate it had not yet wired, and on the one run where the wiring fails that
            // is precisely the wrong order to have logged in.
            Suite.Register(PluginGuid, PluginName, PluginVersion, Config, Requirement.Everyone,
                typeof(CorePlugin).Assembly);

            if (GateWired && ConfigWired && RowsSafe)
            {
                Log.LogInfo(PluginName + " " + PluginVersion + " by " + PluginAuthor + " - ready.");
                return;
            }

            // Deliberately NOT the "ready." line. That line is what every other mod's absence
            // check greps for, and printing it after a partial apply is how a broken Core reads
            // as a working one.
            Log.LogError(PluginName + " " + PluginVersion + " came up DEGRADED"
                + (GateWired ? "" : " - no version gate, so mismatched builds are NOT refused")
                + (ConfigWired ? "" : " - no config sync, so the host's settings are NOT imposed")
                + (RowsSafe ? "" : " - no extra inventory rows")
                + ". Read the errors above before playing on a shared world.");
        }

        /// <summary>
        /// Count what actually attached, and say so.
        ///
        /// Twice in one day a patch here "applied" without patching anything: stacked
        /// [HarmonyPatch] attributes that Harmony merged into one target, and then a class
        /// describing two different target types that attached to neither. Both times PatchAll
        /// returned cleanly, Core printed its ready line, and the feature was simply absent -
        /// the second one was only caught because a log line that should have appeared on every
        /// login never did.
        ///
        /// So the question "did it apply" is answered by asking Harmony which methods it holds
        /// patches on, rather than by the absence of an exception. Anything expected and missing
        /// is an error, because for these two methods missing means a player's items go on the
        /// floor.
        /// </summary>
        private void Verify(params MethodBase[] expected)
        {
            foreach (var method in expected)
            {
                if (method == null) continue;

                var info = Harmony.GetPatchInfo(method);
                var owned = info != null
                            && (Owns(info.Prefixes) || Owns(info.Postfixes) || Owns(info.Transpilers));

                if (owned) continue;

                Log.LogError("Core has NO patch on " + method.DeclaringType.Name + "."
                    + method.Name + " despite applying cleanly. On Valheim 1.0 that method is "
                    + "what decides whether items in mod-granted inventory rows are kept or "
                    + "dropped on the ground - treat those rows as unsafe.");
            }
        }

        private static bool Owns(IEnumerable<Patch> patches)
        {
            if (patches == null) return false;

            foreach (var patch in patches)
                if (patch.owner == PluginGuid) return true;

            return false;
        }

        /// <summary>
        /// One patch group, applied so that its failure cannot take the rest of Core with it.
        ///
        /// Harmony throws out of PatchAll when a target method cannot be resolved - a rename, a
        /// changed signature, an ambiguous overload - which is ordinary on the first launch after
        /// a game update. Catching per group turns "Core did nothing" into "Core lost one
        /// feature", and names which.
        /// </summary>
        private bool Apply(string what, Type patches)
        {
            try
            {
                _harmony.PatchAll(patches);
                return true;
            }
            catch (Exception e)
            {
                Log.LogError("Core could not apply its " + what + " patches, so that feature is "
                    + "off for this session. The rest of Core is unaffected. " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Core owns the timing for anything shared, so no mod has to. Both of these are
        /// cheap when nothing has changed - they compare against what they last wrote and
        /// return.
        /// </summary>
        private void Update()
        {
            // The guard is here rather than inside Tick because this is the only caller, and
            // because it has to cover the backdrop too: growing the wooden panel to fit rows
            // that are not being claimed would leave a stretched, half-empty window.
            if (!RowsSafe) return;

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
