using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Ezomic.Shared
{
    /// <summary>
    /// Handing a runtime-built prefab to the game, done once instead of once per mod.
    ///
    /// <b>This file is shared source, not a library.</b> It lives in the core repo because
    /// that is where shared things live, and it is <i>linked</i> into each mod's csproj
    /// rather than compiled into EzomicCore.dll:
    ///
    /// <code>
    /// &lt;Compile Include="..\core\shared\Prefabs.cs" Link="shared\Prefabs.cs" /&gt;
    /// </code>
    ///
    /// Core is a soft dependency everywhere in this suite, and deliberately so: a mod without
    /// Core loses the version gate and the host's settings and otherwise works. Registration
    /// is not like that. A mod that could not register its prefab would load, patch nothing
    /// into the world and look broken, so putting this in Core would have made Core
    /// mandatory for five mods to do anything at all.
    ///
    /// The alternative considered and rejected was a runtime fallback - Core when present,
    /// a local copy when not. That is two code paths where the second one only ever runs on
    /// machines nobody tests on, which is precisely how the bug described below survived
    /// long enough to destroy something. One shared file is one code path.
    ///
    /// What it costs: a fix here means rebuilding every mod that links it, and on Thunderstore
    /// that is a release each rather than one release of Core. Locally it costs nothing,
    /// because sync-all.ps1 rebuilds everything anyway and the version gate compares build
    /// ids, so these travel together regardless.
    ///
    /// Five mods here have their own copy of this, and the copies are not the interesting
    /// part. The interesting part is that getting it wrong destroys saved objects silently. The
    /// scene and the item database are torn down and rebuilt on every world load - including
    /// logging out to the menu and back in - and a mod that answers "have I registered yet?"
    /// from a static bool says yes to a scene that has never heard of the prefab.
    /// Registration then early-returns, ZNetScene finds no prefab for the saved hash, and it
    /// discards every ZDO of that prefab as junk. No exception, no log line, and the piece a
    /// player built is gone. That happened once, to Stow, on 2026-08-16.
    ///
    /// So the rule this class exists to enforce: <b>ask the world, never a flag</b>. Every
    /// check here is a live lookup against whatever ZNetScene and ObjectDB exist right now,
    /// which costs a dictionary read and cannot go stale.
    ///
    /// <code>
    /// // once, in Awake - Core drives it from there
    /// Prefabs.Keep(StowPost.Name, StowPost.Build, buildTool: "Hammer");
    /// </code>
    ///
    /// The prefab itself is built once and kept for the process. That much caching is safe
    /// and worth having: it is meshes read off disk and materials borrowed from vanilla, and
    /// none of that changes between worlds. It is only the <i>registration</i> that is per
    /// world.
    /// </summary>
    public static class Prefabs
    {
        /// <summary>
        /// Where this writes. Set it to the plugin's own logger in Awake, so a line about a
        /// prefab is attributed to the mod that owns it rather than to a shared name that
        /// says nothing about which of them is talking.
        ///
        /// Left unset it makes its own source, because a missing assignment must not be an
        /// exception thrown from an update.
        /// </summary>
        public static ManualLogSource Log
        {
            // Fully qualified: UnityEngine has a Logger too, and this file is compiled with
            // "using UnityEngine" in five projects that will never agree to drop it.
            get { return _log ?? (_log = BepInEx.Logging.Logger.CreateLogSource("Prefabs")); }
            set { _log = value; }
        }

        private static ManualLogSource _log;

        private static GameObject _holder;

        /// <summary>Standing registrations, in the order they were declared.</summary>
        private static readonly List<Kept> Standing = new List<Kept>();

        /// <summary>How many attempts a failing builder gets before it is left alone.</summary>
        private const int MaxFailures = 5;

        /// <summary>What one mod asked Core to keep registered.</summary>
        private sealed class Kept
        {
            internal string Name;
            internal Func<GameObject> Build;
            internal bool Item;
            internal string Tool;

            internal GameObject Prefab;

            /// <summary>
            /// Builds that threw or returned nothing. A builder is retried, because the usual
            /// reason one fails is that something it reads off the scene is not there yet,
            /// and that fixes itself a frame later. A builder that is simply broken must not
            /// write a stack trace every frame forever, so it gets a few attempts and is then
            /// left alone.
            /// </summary>
            internal int Failures;
            internal bool Abandoned;
        }

        // ------------------------------------------------------------------ standing list

        /// <summary>
        /// Keep a prefab registered, for as long as the game is running and in whatever world
        /// is loaded at the time.
        ///
        /// Core calls <paramref name="build"/> at most once and re-registers the result into
        /// every world after that. The builder runs when a scene exists, so it may look up
        /// donor prefabs, materials and item data - which is why this takes a delegate rather
        /// than a finished GameObject.
        /// </summary>
        /// <param name="name">
        /// The prefab name, which is also its network identity. ZNetScene keys on
        /// <c>name.GetStableHashCode()</c> and saved ZDOs store that hash, so this string is
        /// permanent: renaming it destroys every one already standing in a world.
        /// </param>
        /// <param name="item">
        /// Also register it with ObjectDB. True for anything carrying an ItemDrop - an item
        /// missing from ObjectDB cannot be found by name, crafted, or named by a recipe.
        /// </param>
        /// <param name="buildTool">
        /// Prefab name of the tool whose build menu this piece belongs in - "Hammer",
        /// "Cultivator", "Hoe" - or null for anything that is not a buildable piece.
        /// </param>
        public static void Keep(string name, Func<GameObject> build,
            bool item = false, string buildTool = null)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (build == null) throw new ArgumentNullException(nameof(build));

            // Declared twice is a mod reloading, not a bug. Keep the prefab already built.
            foreach (Kept existing in Standing)
            {
                if (existing.Name != name) continue;

                existing.Build = build;
                existing.Item = item;
                existing.Tool = buildTool;
                existing.Failures = 0;
                existing.Abandoned = false;
                return;
            }

            Standing.Add(new Kept { Name = name, Build = build, Item = item, Tool = buildTool });
            Log.LogInfo("Keeping " + name + " registered.");
        }

        /// <summary>
        /// Stop keeping a prefab registered.
        ///
        /// It stays in the world it is already in. Taking it back out of ZNetScene would
        /// discard its ZDOs, which is the thing this class exists to prevent, so this only
        /// stops the re-registration on the next world.
        /// </summary>
        public static void Drop(string name)
        {
            Standing.RemoveAll(k => k.Name == name);
        }

        /// <summary>
        /// Call this from the plugin's Update. Every check inside is a lookup that returns
        /// immediately once satisfied, so the steady-state cost is one dictionary read per
        /// kept prefab per frame and nothing else.
        ///
        /// There is no hook that could replace it. ZNetScene and ObjectDB do not exist at
        /// load and are rebuilt per world, so there is no single moment to patch - only a
        /// question worth asking cheaply and often.
        /// </summary>
        public static void Tick()
        {
            if (Standing.Count == 0) return;

            // No scene, no registration. This is the menu, or the moment between worlds.
            ZNetScene scene = ZNetScene.instance;
            if (scene == null) return;

            for (int i = 0; i < Standing.Count; i++) Apply(Standing[i], scene);
        }

        private static void Apply(Kept kept, ZNetScene scene)
        {
            if (kept.Abandoned) return;

            // Unity overloads ==, so this also catches a prefab destroyed out from under us.
            // Rebuilt rather than mourned.
            if (kept.Prefab == null && !TryBuild(kept)) return;

            if (scene.GetPrefab(kept.Name) == null) Register(kept.Prefab);

            if (kept.Item) RegisterItem(kept.Prefab);

            if (!string.IsNullOrEmpty(kept.Tool)) AddToTool(kept.Prefab, kept.Tool);
        }

        private static bool TryBuild(Kept kept)
        {
            GameObject built = null;

            try
            {
                built = kept.Build();
            }
            catch (Exception e)
            {
                Log.LogWarning("Building " + kept.Name + " failed: " + e.Message);
            }

            if (built != null)
            {
                kept.Prefab = built;
                kept.Failures = 0;

                // Named here as well as by the builder, because the name is the network
                // identity: a builder that forgot Instantiate's "(Clone)" suffix would
                // otherwise register something no saved ZDO can resolve.
                if (built.name != kept.Name) built.name = kept.Name;
                return true;
            }

            if (++kept.Failures < MaxFailures) return false;

            kept.Abandoned = true;
            Log.LogError(kept.Name + " could not be built after " + MaxFailures
                + " attempts and will not be retried. Anything already built with it in a "
                + "world is untouched; it simply will not be placeable this session.");
            return false;
        }

        // ------------------------------------------------------------------ the pieces

        /// <summary>
        /// Whether the world that exists right now can name this prefab. Also the whole of
        /// the "have I done this yet" test - registration is idempotent, and this is what
        /// makes it so without a flag that can outlive the scene it was true for.
        /// </summary>
        public static bool Known(string name)
        {
            return ZNetScene.instance != null && ZNetScene.instance.GetPrefab(name) != null;
        }

        /// <summary>
        /// A disabled, undestroyed parent for anything built at runtime.
        ///
        /// Templates have to live somewhere that will not run their Awake, will not draw them
        /// and will not be swept up by a scene change. One hidden root does all three, and
        /// one shared root means a mod does not have to remember to make its own.
        /// </summary>
        public static Transform Holder
        {
            get
            {
                if (_holder == null)
                {
                    // Named after whoever is logging, because each mod linking this file
                    // gets its own holder and a row of identically named objects in a scene
                    // dump is not a thing anyone can read.
                    _holder = new GameObject(Log.SourceName + "Prefabs");
                    _holder.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(_holder);
                }

                return _holder.transform;
            }
        }

        /// <summary>
        /// Clone a donor into the holder with network init suppressed.
        ///
        /// The suppression is the load-bearing part. A clone taken under an active parent
        /// gets its ZNetView's Awake, which tries to register the thing on the network while
        /// it is still half-built. The flag goes down before Instantiate and back up in a
        /// finally, because leaving it set breaks every legitimate spawn after this one.
        /// </summary>
        public static GameObject Clone(GameObject source, string name)
        {
            if (source == null) return null;

            bool previous = ZNetView.m_forceDisableInit;
            ZNetView.m_forceDisableInit = true;

            GameObject clone;
            try
            {
                clone = UnityEngine.Object.Instantiate(source, Holder);
            }
            finally
            {
                ZNetView.m_forceDisableInit = previous;
            }

            // Instantiate appends "(Clone)", and the name is the hash the whole network
            // identity hangs off, so this is not cosmetic.
            clone.name = name;
            return clone;
        }

        /// <summary>
        /// The first of several candidate donors the scene can actually produce.
        ///
        /// A list rather than a name, because the streaming manifest lists what is on disk
        /// rather than what is loaded: Stoker's first candidate list came off that manifest
        /// and 2 of 16 resolved. A donor that reads as certain in a text file is a guess
        /// until a runtime lookup agrees.
        /// </summary>
        public static GameObject Donor(string commaSeparated, out string chosen)
        {
            chosen = null;
            if (ZNetScene.instance == null) return null;

            foreach (string candidate in (commaSeparated ?? "").Split(','))
            {
                string name = candidate.Trim();
                if (name.Length == 0) continue;

                GameObject prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab == null) continue;

                chosen = name;
                return prefab;
            }

            return null;
        }

        /// <summary>
        /// Into both of the places ZNetScene looks.
        ///
        /// The list alone is not enough once Awake has run: the dictionary the lookup
        /// actually uses is built there from that list and never rebuilt, so a prefab added
        /// to the list afterwards exists and cannot be found - which looks exactly like it
        /// was never added.
        /// </summary>
        public static bool Register(GameObject prefab)
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null || prefab == null) return false;
            if (scene.GetPrefab(prefab.name) != null) return true;

            if (!scene.m_prefabs.Contains(prefab)) scene.m_prefabs.Add(prefab);

            try
            {
                NamedPrefabs(scene)[prefab.name.GetStableHashCode()] = prefab;
            }
            catch (Exception e)
            {
                // Once, not once a frame. The only way this fails is the field being gone
                // after a game update, and that condition does not clear by itself - it
                // would write the same line for the rest of the session.
                ComplainOnce("scene:" + prefab.name, "Could not register " + prefab.name
                    + " with ZNetScene: " + e.Message);
                return false;
            }

            Log.LogInfo("Registered " + prefab.name + " with ZNetScene.");
            return true;
        }

        /// <summary>
        /// Into ObjectDB, and then its lookup tables rebuilt.
        ///
        /// m_items alone is not enough: GetItemPrefab reads m_itemByHash, which is built once
        /// in the private UpdateRegisters and never again. Without the rebuild the item is in
        /// the list and cannot be found by name.
        /// </summary>
        public static bool RegisterItem(GameObject prefab)
        {
            ObjectDB db = ObjectDB.instance;
            if (db == null || prefab == null) return false;

            // The first ObjectDB.Awake of a session fires against a stub: two status effects
            // and no items. Registering into that one succeeds and is then thrown away with
            // it, and anything looked up there fails. An empty item list is the tell.
            if (db.m_items == null || db.m_items.Count == 0) return false;

            if (db.GetItemPrefab(prefab.name) != null) return true;

            if (!db.m_items.Contains(prefab)) db.m_items.Add(prefab);

            try
            {
                UpdateRegisters(db);
            }
            catch (Exception e)
            {
                ComplainOnce("db:" + prefab.name, "Could not refresh ObjectDB for "
                    + prefab.name + ": " + e.Message);
                return false;
            }

            Log.LogInfo("Registered " + prefab.name + " with ObjectDB.");
            return true;
        }

        /// <summary>
        /// The build menu of a tool, for the ObjectDB that exists now.
        ///
        /// Asked of the table every time rather than remembered, for the same reason
        /// <see cref="Known"/> asks the scene: ObjectDB is rebuilt per world, and a Hammer
        /// from the last one is a different object with a different list. Remembering meant a
        /// piece left the build menu on the second world of a session - the mild version of
        /// the failure at the top of this file.
        /// </summary>
        public static PieceTable ToolPieces(string toolPrefab)
        {
            if (ObjectDB.instance == null || string.IsNullOrEmpty(toolPrefab)) return null;

            GameObject tool = ObjectDB.instance.GetItemPrefab(toolPrefab);
            ItemDrop drop = tool != null ? tool.GetComponent<ItemDrop>() : null;
            if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null)
                return null;

            PieceTable table = drop.m_itemData.m_shared.m_buildPieces;
            return table != null && table.m_pieces != null ? table : null;
        }

        /// <summary>Whether a tool's build menu already carries this piece.</summary>
        public static bool InTool(GameObject prefab, string toolPrefab)
        {
            PieceTable table = ToolPieces(toolPrefab);
            return table != null && prefab != null && table.m_pieces.Contains(prefab);
        }

        /// <summary>Puts a piece in a tool's build menu, if it is not already there.</summary>
        public static bool AddToTool(GameObject prefab, string toolPrefab)
        {
            if (prefab == null) return false;

            PieceTable table = ToolPieces(toolPrefab);
            if (table == null) return false;
            if (table.m_pieces.Contains(prefab)) return true;

            table.m_pieces.Add(prefab);

            // Logged on the add rather than on the call: this is retried every frame, and an
            // already-satisfied retry would write a line per frame.
            Log.LogInfo(prefab.name + " added to the " + toolPrefab + ".");
            return true;
        }

        // ------------------------------------------------------------------ complaints

        private static readonly HashSet<string> Complained = new HashSet<string>();

        /// <summary>
        /// Say it once. Both callers sit inside something retried every frame, and the faults
        /// they report - a private field or method renamed by a game update - do not clear on
        /// their own, so the honest count of how much use a second line of it is is zero.
        /// </summary>
        private static void ComplainOnce(string key, string message)
        {
            if (!Complained.Add(key)) return;

            Log.LogError(message);
        }

        // ------------------------------------------------------------------ reflection

        // Resolved once. AccessTools walks the type on every call, and these are reached from
        // an update.
        private static AccessTools.FieldRef<ZNetScene, Dictionary<int, GameObject>> _named;
        private static MethodInfo _updateRegisters;

        private static Dictionary<int, GameObject> NamedPrefabs(ZNetScene scene)
        {
            if (_named == null)
                _named = AccessTools.FieldRefAccess<ZNetScene, Dictionary<int, GameObject>>(
                    "m_namedPrefabs");

            return _named(scene);
        }

        private static void UpdateRegisters(ObjectDB db)
        {
            if (_updateRegisters == null)
                _updateRegisters = AccessTools.Method(typeof(ObjectDB), "UpdateRegisters");

            if (_updateRegisters == null)
                throw new MissingMethodException("ObjectDB.UpdateRegisters");

            _updateRegisters.Invoke(db, null);
        }
    }
}
