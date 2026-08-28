using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace Ezomic.Shared
{
    /// <summary>
    /// Which biome each item comes from, worked out from the tables the game already keeps.
    ///
    /// <b>This file is shared source, not a library.</b> Same arrangement as Prefabs.cs beside
    /// it: it lives in the core repo because that is where shared things live, and it is
    /// <i>linked</i> into each mod's csproj rather than compiled into EzomicCore.dll:
    ///
    /// <code>
    /// &lt;Compile Include="..\core\shared\BiomeIndex.cs" Link="shared\BiomeIndex.cs" /&gt;
    /// </code>
    ///
    /// The reason is the same reason, and it is worth writing out again rather than pointing
    /// at the other file. Core is a soft dependency everywhere in this suite: a mod without
    /// Core loses the version gate and the host's settings and otherwise works. Classification
    /// is not like that. A mod that could not answer "which biome is this item from" would
    /// still have to do something - Yoke has to pick a stack size for every item in the
    /// database, and the mod this was lifted for has to decide whether a craft is allowed - so
    /// owning this in Core's DLL would make Core mandatory for every consumer of it, which is
    /// exactly the property the suite is built to avoid.
    ///
    /// The alternative considered and rejected was a runtime fallback, Core when present and a
    /// local copy when not. That is two code paths where the second only ever runs on machines
    /// nobody tests on. One shared file is one code path. What it costs is that a fix here
    /// means rebuilding every mod that links it.
    ///
    /// Everything this file needs from its host is a static seam with a working default - see
    /// the block below <see cref="Log"/>. It compiles in a project that has never heard of any
    /// particular mod, and left entirely unconfigured it still builds an index; it simply has
    /// no hand-written overrides and cannot place a boss's own drops.
    ///
    /// Nothing here is a list of items. The world's vegetation table says a copper deposit
    /// belongs to the Black Forest and the deposit says it drops copper ore, so copper ore is
    /// a Black Forest item; the spawn table says Fulings belong to the Plains and the Fuling
    /// says it drops black metal scrap, so black metal is a Plains item. Both facts are the
    /// game's, which means they stay true through an update and cover whatever a content mod
    /// adds without anyone typing it here.
    ///
    /// Four routes in:
    ///
    ///   ZoneSystem.m_vegetation  - berries, mushrooms, thistle, rocks, ore deposits, trees
    ///   SpawnSystem spawn lists  - every creature, through its CharacterDrop
    ///   smelter/cooking recipes  - bars and cooked food, inheriting from what they are made of
    ///   BiomeOverrides           - the handful none of the above can reach
    ///
    /// That last one exists because of iron. Iron scrap is neither placed in the world nor
    /// dropped by anything that spawns in it - it is inside Sunken Crypts, and a location
    /// holds its prefab as a SoftReference that is not loaded until the game wants it. Walking
    /// dungeon interiors to find it would mean forcing asset loads on the way into a world, to
    /// learn a fact that fits on one config line.
    ///
    /// Earliest biome wins. Plenty of items appear in several - wood is everywhere - and the
    /// one that matters is where you first have to haul it.
    ///
    /// ---------------------------------------------------------------------------------------
    /// Three bugs this file used to have, all visible in the shipped item list of 1.0.4, and
    /// all three fixed here. They are written out because each one looked like a small thing
    /// and each one was structural.
    ///
    /// 1. BlackMetal was plains and BoltBlackmetal was meadows, in the same index. The bolt is
    ///    black metal, wood and feathers. On the first pass Craft() ran before Convert(), so
    ///    the bar - which is a blast furnace conversion, not a drop - was not on the map yet,
    ///    the recipe saw only wood and feathers and answered meadows, and the plain
    ///    earliest-wins guard then made meadows permanent: on every later pass the recipe knew
    ///    the right answer, plains, and was refused for being later than what was already
    ///    there. A recipe classified from a partial ingredient list, frozen by the very rule
    ///    that is supposed to protect it. Nineteen items across the index were frozen this
    ///    way. See Craft().
    ///
    /// 2. The meadows tier was carrying Mistlands and Ashlands drops. Carapace, CharredBone,
    ///    the Draugr, Seeker, Skeleton and Fuling trophies - all "meadows". Vanilla ships
    ///    spawn rows like "Charred Melee [Other biomes when Fader is defeated]" whose biome
    ///    mask names Meadows but which are gated behind m_requiredGlobalKey, and Harvest()
    ///    read the mask and nothing else. A row you cannot reach until Fader is dead is not a
    ///    meadows row. Ten vanilla rows do this. Twelve rows are dropped in all, because the
    ///    clause that drops them also catches the two conditional on weather rather than on a
    ///    key - the rain-only Neck and the storm-only Serpent - and both of those name their
    ///    own creature's biome, so dropping them moves nothing. Ten is the number that
    ///    mattered; twelve is the number the code touches. Both counts are a measurement and
    ///    not a rule - instrumented simulation of the build against game 0.221.12, run
    ///    2026-08-28 - so an update that adds or retires a gated spawn row moves them without
    ///    a line here changing. See HarvestSpawnLists().
    ///
    /// 3. Both of the above were permanent rather than transient because every writer took
    ///    the earliest answer and no writer could ever raise one. A better-sourced answer had
    ///    no way to correct a worse-sourced one. See Record() and Placement.
    /// ---------------------------------------------------------------------------------------
    /// </summary>
    public static class BiomeIndex
    {
        // -----------------------------------------------------------------------------------
        // The seams
        //
        // Everything this file used to reach for by name - the mod's logger, the mod's boss
        // table, the mod's config - is a static here with a default that works. Plain
        // delegates rather than an interface or a settings object, for the same reason
        // Prefabs.cs exposes a bare ManualLogSource property: there is one host per process
        // per assembly, the whole surface is three values, and an interface would buy an
        // abstraction nobody has a second implementation of.
        //
        // Every default is chosen so that an unconfigured consumer FAILS BY DOING LESS rather
        // than by throwing or by inventing an answer. No boss table means boss drops are not
        // placed and key-gated spawn rows fall back to their mask - which is precisely what
        // the code already does for a key the table says nothing about. No overrides means no
        // overrides. Set them once, before the first Prepare().
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Where this writes. Set it to the plugin's own logger at init, so a line about a
        /// biome is attributed to the mod that asked rather than to a shared name that says
        /// nothing about which of them is talking.
        ///
        /// Left unset it makes its own source, because a missing assignment must not be an
        /// exception thrown from a Harmony patch.
        /// </summary>
        public static ManualLogSource Log
        {
            // Fully qualified: UnityEngine has a Logger too, and this file is compiled with
            // "using UnityEngine" in every project that links it.
            get { return _log ?? (_log = BepInEx.Logging.Logger.CreateLogSource("BiomeIndex")); }
            set { _log = value; }
        }

        private static ManualLogSource _log;

        /// <summary>
        /// The biome a boss's global key belongs to, or null when the host has no opinion.
        ///
        /// This is the one fact in the whole file that cannot be read off the game: nothing in
        /// the tables says defeated_moder is a Mountain thing. It is a table the host keeps -
        /// in Yoke it is the progression tiers a player can edit in a .cfg - and two places
        /// here need it. Bosses() uses it to put a boss's own drops in its own biome, and
        /// SpawnIndex() uses it to clamp a key-gated spawn row no earlier than the biome that
        /// key unlocks.
        ///
        /// A delegate rather than a dictionary passed in, because the host's table is
        /// re-parsed whenever its config changes and a copy taken at init would go stale
        /// without anything saying so.
        ///
        /// Null by default, and null is a real answer rather than an error: both callers
        /// already have a "the table says nothing about this key" path, because a modded boss
        /// key nobody has tiered has always been possible.
        /// </summary>
        public static Func<string, string> BiomeForKey;

        /// <summary>
        /// The hand-written overrides, as "IronScrap:swamp, Coins:blackforest".
        ///
        /// A delegate returning the raw string rather than the string itself, so it is read
        /// afresh on every build. The host's copy of this is a config entry a player edits
        /// while the game is running, and Yoke rebuilds the index on every world load; a value
        /// captured at init would answer for whatever was in the file at startup forever.
        /// </summary>
        public static Func<string> Overrides;

        /// <summary>
        /// What the host calls that setting, for the one log line that tells a reader where to
        /// go and fix a guess.
        ///
        /// A string and not a hardcoded name because the point of that warning is to be
        /// actionable, and sending someone to look for a setting their mod does not have is
        /// worse than not naming one. The default is the name Yoke uses, which is also the
        /// name any consumer of this file should reach for first.
        /// </summary>
        public static string OverrideSetting = "BiomeOverrides";

        private static string BossBiome(string globalKey)
        {
            Func<string, string> table = BiomeForKey;
            return table == null ? null : table(globalKey);
        }

        // -----------------------------------------------------------------------------------
        // The vocabulary
        //
        // The names and their order are part of the shared contract, not a per-consumer
        // detail. Two mods that disagree about whether it is "blackforest" or "black_forest",
        // or about whether the Ocean sits before or after the Swamp, would be two mods giving
        // a player different answers about the same item out of the same tables - and the
        // config entries they each ask a player to type would silently not transfer.
        // -----------------------------------------------------------------------------------

        public const string None = "none";

        /// <summary>
        /// The pseudo-group a tier may name to mean "everything at once".
        ///
        /// Nothing in this file reads it - it is the host's tier table that does - and it is
        /// still part of the shared vocabulary rather than a constant each consumer declares.
        /// It is a value a player types into a config entry beside the biome names, and a
        /// second mod that spelled it "any" or "*" would be asking for a different word in
        /// what looks like the same setting.
        /// </summary>
        public const string Everything = "all";

        /// <summary>
        /// Biomes in progression order, which is what "earliest" means. Ocean and Deep North
        /// are here so their items are visible in the item list rather than silently unsorted;
        /// no boss unlocks them by default.
        /// </summary>
        private static readonly KeyValuePair<string, Heightmap.Biome>[] Ordered =
        {
            new KeyValuePair<string, Heightmap.Biome>("meadows", Heightmap.Biome.Meadows),
            new KeyValuePair<string, Heightmap.Biome>("blackforest", Heightmap.Biome.BlackForest),
            new KeyValuePair<string, Heightmap.Biome>("ocean", Heightmap.Biome.Ocean),
            new KeyValuePair<string, Heightmap.Biome>("swamp", Heightmap.Biome.Swamp),
            new KeyValuePair<string, Heightmap.Biome>("mountain", Heightmap.Biome.Mountain),
            new KeyValuePair<string, Heightmap.Biome>("plains", Heightmap.Biome.Plains),
            new KeyValuePair<string, Heightmap.Biome>("mistlands", Heightmap.Biome.Mistlands),
            new KeyValuePair<string, Heightmap.Biome>("ashlands", Heightmap.Biome.AshLands),
            new KeyValuePair<string, Heightmap.Biome>("deepnorth", Heightmap.Biome.DeepNorth)
        };

        public static readonly string[] All = BuildNames();

        private static string[] BuildNames()
        {
            var names = new string[Ordered.Length + 1];
            for (var i = 0; i < Ordered.Length; i++) names[i] = Ordered[i].Key;
            names[Ordered.Length] = None;
            return names;
        }

        // -----------------------------------------------------------------------------------
        // How well an answer is sourced.
        //
        // Bug 3 above: with one flat dictionary of biome indices and one flat "keep whichever
        // is earlier" rule, a guess made on pass one outranks a fact learned on pass two
        // forever, purely because the guess happened to be earlier. Recording *where* an
        // answer came from alongside the answer is what lets a fact overwrite a guess.
        //
        // Only three levels, and the gaps between them are the whole design:
        //
        //   Pinned   - somebody typed it into BiomeOverrides. It exists precisely because the
        //              tables are wrong or silent about that item, so it beats everything.
        //   Derived  - read off the game's own tables: vegetation, an ungated spawn row, a
        //              boss's key, a station conversion, or a recipe every ingredient of which
        //              is already placed. These are all facts of equal standing.
        //   Guessed  - a recipe classified from only some of its ingredients, after the
        //              closure has stopped moving. An honest last resort, and logged as one.
        //
        // Note what is deliberately NOT here: the task of splitting Derived into "world
        // placement beats recipe closure". It is tempting and it is wrong. Charred warriors
        // drop bronze, so the Ashlands spawn tables genuinely place Bronze in the Ashlands,
        // and the only thing that drags it back to the Black Forest where it belongs is the
        // recipe (copper + tin) winning on being earlier. Rank world placement above recipes
        // and Bronze becomes an Ashlands item, along with every mead the ashland pots drop.
        // World facts and recipe facts are peers; earliest still decides between them.
        // -----------------------------------------------------------------------------------
        private const int Guessed = 0;
        private const int Derived = 1;
        private const int Pinned = 2;

        private struct Placement
        {
            public int Index;
            public int Confidence;
        }

        /// <summary>Prefab name to where it was placed. Absent means no biome was found.</summary>
        private static Dictionary<string, Placement> _biomeOf;

        /// <summary>
        /// Whether the last build had its sources. ZoneSystem, SpawnSystem and ZNetScene only
        /// exist once a world is loading, so the first pass of a session - which runs from
        /// ObjectDB.Awake, long before any of that - would otherwise cache an empty index and
        /// keep answering "no biome" for the rest of the session.
        /// </summary>
        private static bool _complete;

        public static void Invalidate()
        {
            _biomeOf = null;
            _complete = false;
        }

        public static bool Complete
        {
            get { return _complete; }
        }

        /// <summary>
        /// A pure lookup. Building is Prepare's job and is done once per pass, not once per
        /// item: while the index is incomplete there is nothing to stop a build-on-demand
        /// running again for every item in the database, and the first version of this walked
        /// the vegetation table and every prefab in the scene a thousand times over.
        /// </summary>
        public static string BiomeOf(string prefabName)
        {
            Placement placement;
            if (_biomeOf != null && _biomeOf.TryGetValue(prefabName, out placement))
                return Ordered[placement.Index].Key;

            return None;
        }

        /// <summary>Builds the index if it is not already complete. Called once per pass.</summary>
        public static void Prepare()
        {
            Build();
        }

        /// <summary>How many items the index has placed, for the log and the item list.</summary>
        public static int Count
        {
            get { return _biomeOf == null ? 0 : _biomeOf.Count; }
        }

        /// <summary>
        /// Caps on the two loops below. Neither is the reason the build terminates - see the
        /// termination note on Record - they are belt and braces against a future source that
        /// writes to the dictionary without going through Record.
        /// </summary>
        private const int MaxClosurePasses = 12;

        private const int MaxFallbackRounds = 4;

        private static void Build()
        {
            if (_complete && _biomeOf != null) return;

            var zone = ZoneSystem.instance;
            var spawn = UnityEngine.Object.FindObjectOfType<SpawnSystem>();
            var scene = ZNetScene.instance;

            // Rebuilt from scratch each attempt rather than topped up, so a partial early
            // build cannot leave a wrong earliest-biome behind once the rest arrives.
            var found = new Dictionary<string, Placement>();

            if (zone != null && zone.m_vegetation != null)
                foreach (var vegetation in zone.m_vegetation)
                    if (vegetation != null && vegetation.m_enable)
                        Harvest(found, vegetation.m_prefab, Earliest(vegetation.m_biome));

            if (spawn != null) HarvestSpawnLists(found, spawn);

            if (scene != null && scene.m_prefabs != null) Bosses(found, scene);

            // Before the conversions and recipes, not only after. An override is a root fact -
            // copper ore is Black Forest - and everything made from it should inherit that.
            // Applied last as well, so a hand-written answer still beats a derived one.
            // Copper bars came out unplaced when this only ran at the end: the smelter pass
            // went looking for copper ore before the override had put it on the map.
            ApplyOverrides(found);

            // Together and until it settles, rather than one after the other. The chains cross
            // between the two: barley is milled to flour by a station, the flour is made into
            // dough by a recipe, and the dough is baked back at a station. Running all the
            // conversions and then all the recipes leaves bread unplaced however many passes
            // each gets, because the step it is waiting on happens in the other list.
            //
            // Two nested loops rather than one, which is bug 1's fix. The inner loop is the
            // strict closure: a recipe answers only once every single one of its ingredients
            // is on the map, so nothing is ever classified from a partial list. That is what
            // stops BoltBlackmetal answering "meadows" from wood and feathers alone on the
            // pass before the blast furnace conversion has placed the bar.
            //
            // The outer loop exists because strict alone is not enough. Some vanilla recipes
            // name an ingredient nothing in the game can ever place - Recipe_AxeEarly wants
            // AxeHead1, which is neither dropped nor grown nor cooked - and those would defer
            // forever and silently lose their item. So once the strict closure has stopped
            // moving, and only then, deferred recipes are allowed to answer from what is
            // known. By that point "still unplaced" means "unplaceable", so ignoring those
            // ingredients is exactly right rather than merely convenient. The answer is
            // recorded as Guessed and every item it touches is named in the log, because the
            // whole failure being fixed here was a guess that looked like a fact.
            var guesses = new List<string>();

            for (var round = 0; round < MaxFallbackRounds; round++)
            {
                var deferred = 0;

                for (var pass = 0; pass < MaxClosurePasses; pass++)
                {
                    var moved = Craft(found, false, out deferred, null);
                    if (scene != null && scene.m_prefabs != null) moved |= Convert(found, scene);
                    if (!moved) break;
                }

                if (deferred == 0) break;

                int ignored;
                if (!Craft(found, true, out ignored, guesses)) break;
            }

            ApplyOverrides(found);

            _biomeOf = found;
            _complete = zone != null && spawn != null && scene != null;

            if (_complete)
            {
                Log.LogInfo("Biome index built: " + found.Count + " item(s) placed.");
                ReportGuesses(found, guesses);
                return;
            }

            // Named, because "incomplete" on its own sends you looking in the wrong place. The
            // three appear at different moments on the way into a world and this says which
            // one has not turned up yet.
            var missing = new List<string>();
            if (zone == null) missing.Add("ZoneSystem");
            if (spawn == null) missing.Add("SpawnSystem");
            if (scene == null) missing.Add("ZNetScene");

            Log.LogInfo(
                "Biome index partial: " + found.Count + " item(s) placed, still waiting on "
                + string.Join(" and ", missing.ToArray()) + ".");
        }

        /// <summary>
        /// Says out loud which items were classified from an incomplete ingredient list.
        ///
        /// A warning rather than an info line, and it names them rather than counting them.
        /// The bug this file was rewritten for was invisible for exactly as long as it was:
        /// the index reported "665 items placed" and nineteen of those were guesses that had
        /// then been frozen in place. A number that goes up is not a diagnostic. A list of
        /// names is something you can go and check.
        ///
        /// Which is exactly why it is filtered against the finished index rather than printed
        /// raw. A name goes on the list when its guess is written, and a later fallback round
        /// can then overwrite that guess with a Derived answer - the closure having caught up
        /// in between. Printing the raw list names those corrected items too, and every one of
        /// them is a reader sent to check an entry that no longer needs checking.
        ///
        /// So the filter makes the list true about everything it names. It does not make it
        /// complete, and that is the honest limit of this warning: it names only the items
        /// guessed *directly*, the ones Craft answered from a short ingredient list. Guessed
        /// is contagious and nothing downstream is added. Craft floors a recipe's confidence to
        /// its worst-sourced ingredient, so a dish made of a guess is recorded Guessed while
        /// `guessing` stays false and its name is never collected; Inherit floors the same way,
        /// so a bar smelted out of a guessed ore, and the mead fermented out of that, are all
        /// Guessed and all silent. An item can therefore stand at Guessed in the index and be
        /// absent from this warning. The list is where to start looking, not the whole of what
        /// a guess touched.
        /// </summary>
        private static void ReportGuesses(Dictionary<string, Placement> found, List<string> guesses)
        {
            if (guesses == null || guesses.Count == 0) return;

            var standing = new List<string>(guesses.Count);
            foreach (var name in guesses)
            {
                Placement placement;
                if (found.TryGetValue(name, out placement) && placement.Confidence == Guessed)
                    standing.Add(name);
            }

            if (standing.Count == 0) return;

            standing.Sort(StringComparer.Ordinal);

            Log.LogWarning(
                "Biome index: " + standing.Count + " item(s) classified from an INCOMPLETE "
                + "ingredient list, because the closure could not place everything their "
                + "recipe asks for. Their biome is a best guess and " + OverrideSetting
                + " is the cure: " + string.Join(", ", standing.ToArray()) + ".");
        }

        // -----------------------------------------------------------------------------------
        // Spawn rows
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Every creature the world spawns, recorded against the biome you can actually meet
        /// it in - which is not always the biome its row's mask names.
        ///
        /// This is bug 2. SpawnSystem.SpawnData carries a great many gates beyond the mask;
        /// UpdateSpawnList and FindBaseSpawnPoint between them read m_enabled,
        /// m_requiredGlobalKey, m_requiredEnvironments, m_spawnAtDay/m_spawnAtNight,
        /// m_spawnChance, altitude, tilt, forest, lava, ocean depth and distance from centre.
        /// The old code read m_enabled and the mask and stopped. Vanilla then walked straight
        /// into it: ten rows name Meadows in their mask and gate the real spawn on a global
        /// key, and those ten dragged Carapace, CharredBone, Entrails, AncientSeed,
        /// Pukeberries, Coins, BugMeat and six trophies into the meadows tier.
        ///
        /// Every count and row number in this docstring is one reading of one version's spawn
        /// tables - instrumented simulation of the build against game 0.221.12, run 2026-08-28
        /// - and not a property of the rule below. The rule reads whatever rows it is given; an
        /// update that adds, retires or renumbers a gated row makes these numbers stale on its
        /// own. Re-measure before quoting them.
        ///
        /// Two further rows are conditional on weather rather than on a key - base row 3, the
        /// Neck in rain, and base row 54, the Serpent in a storm - and the sibling clause below
        /// drops those as well, which is where the count of twelve dropped rows comes from.
        /// Both name their own creature's biome, so neither drop changes an answer. The
        /// distinction is worth keeping straight: ten rows were doing the damage, twelve rows
        /// stop being read.
        ///
        /// The rule, and it is deliberately narrow:
        ///
        ///   * m_devDisabled joins m_enabled. The runtime never reads it - it is an editor
        ///     flag - but a row carrying it is a row nobody intends to ship, and reading it
        ///     costs nothing. Exactly one vanilla row sets it and that row is disabled anyway.
        ///
        ///   * A row with no global key and no required environment is unconditional, and its
        ///     mask is taken at face value.
        ///
        ///   * A conditional row for a creature that ALSO has an unconditional row somewhere
        ///     is dropped outright. The unconditional row is a strictly better statement about
        ///     where that creature lives, and every one of the twelve offenders has one:
        ///     Seekers are in the Mistlands ungated, Charred are in the Ashlands ungated,
        ///     Skeletons are in the Swamp ungated. This is the clause that actually fixes it.
        ///
        ///   * A conditional row that is the ONLY row for its creature is kept, not dropped,
        ///     and this matters: SeekerBrood and Tick appear exclusively on rows gated behind
        ///     defeated_queen. Drop those and RoyalJelly, GiantBloodSack and the Tick trophy
        ///     vanish from the index entirely. Kept, but clamped: a key-gated row cannot be
        ///     reached before its key is set, so its drops belong no earlier than the biome
        ///     that key unlocks. max(mask, key's tier), never min.
        ///
        ///   * A key the host's boss table says nothing about - including the case of a host
        ///     that supplied no table at all, see BiomeForKey - cannot be ordered against
        ///     anything, so its row falls back to its mask. Failing by doing less.
        ///
        /// And what is deliberately NOT honoured, because "gated" and "unreachable" are not
        /// the same word:
        ///
        ///   * m_spawnAtNight / m_spawnAtDay. Night comes round on its own. A night-only
        ///     greydwarf is a Black Forest greydwarf; skipping those rows would lose real
        ///     creatures for no reason.
        ///   * m_spawnChance, m_maxSpawned, m_spawnInterval. Rarity is not absence.
        ///   * altitude, tilt, forest, lava, ocean depth, distance from centre. All of these
        ///     narrow *where in the biome*, not *which biome*, which is the only question
        ///     being asked here.
        ///   * m_requiredEnvironments on its own is treated like a key-less condition: it can
        ///     lose to an unconditional row for the same creature, but it is never relocated,
        ///     because weather cycles and a key does not. The rain-only Neck row and the
        ///     storm-only Serpent row both have plain siblings, so vanilla notices no
        ///     difference either way; the clause is here for the modded row that will not.
        /// </summary>
        private static void HarvestSpawnLists(Dictionary<string, Placement> found, SpawnSystem spawn)
        {
            if (spawn.m_spawnLists == null) return;

            // Collected in full before anything is recorded, because the unconditional row
            // that rescues a creature may live in a different list from the gated one that
            // slanders it - Seeker's honest row is in the Mistlands list, its "other biomes"
            // row is in the same list but the Charred pair straddle the Ashlands list, and
            // nothing guarantees the order.
            var unconditional = new HashSet<string>();

            foreach (var list in spawn.m_spawnLists)
            {
                if (list == null || list.m_spawners == null) continue;
                foreach (var row in list.m_spawners)
                {
                    if (!Runs(row) || Conditional(row)) continue;
                    unconditional.Add(row.m_prefab.name);
                }
            }

            foreach (var list in spawn.m_spawnLists)
            {
                if (list == null || list.m_spawners == null) continue;
                foreach (var row in list.m_spawners)
                {
                    if (!Runs(row)) continue;
                    Harvest(found, row.m_prefab, SpawnIndex(row, unconditional));
                }
            }
        }

        /// <summary>Whether this row is one the shipped game would ever act on at all.</summary>
        private static bool Runs(SpawnSystem.SpawnData row)
        {
            return row != null && row.m_enabled && !row.m_devDisabled && row.m_prefab != null;
        }

        /// <summary>Whether the row's spawn waits on something outside the biome itself.</summary>
        private static bool Conditional(SpawnSystem.SpawnData row)
        {
            return !string.IsNullOrEmpty(row.m_requiredGlobalKey)
                   || (row.m_requiredEnvironments != null && row.m_requiredEnvironments.Count > 0);
        }

        /// <summary>The biome this row's drops really belong to, or -1 to ignore the row.</summary>
        private static int SpawnIndex(SpawnSystem.SpawnData row, HashSet<string> unconditional)
        {
            var masked = Earliest(row.m_biome);

            // A mask naming no biome at all is a row UpdateSpawnList never reaches - it fails
            // m_heightmap.HaveBiome before anything else is read - so there is nothing to
            // attribute and nowhere to attribute it to. Bailing here and not below, because
            // the clamp further down would otherwise happily hand such a row its key's biome
            // and invent a spawn out of a row that never spawns.
            if (masked < 0) return -1;

            if (!Conditional(row)) return masked;

            if (unconditional.Contains(row.m_prefab.name)) return -1;

            var key = row.m_requiredGlobalKey;
            if (string.IsNullOrEmpty(key)) return masked;

            var biome = BossBiome(key);
            if (biome == null) return masked;

            var gate = IndexOf(biome);
            if (gate < 0) return masked;

            return masked > gate ? masked : gate;
        }

        // -----------------------------------------------------------------------------------
        // Walking a prefab for what it yields
        //
        // What these walks are actually worth, measured over a full build rather than argued
        // from the field names. It is written down because the commit that added the last few
        // of them implied they carried the fix, and they do not.
        //
        // THE MEASUREMENT, and every "zero writes" and "today" below is this one reading:
        // instrumented simulation of the build against game 0.221.12, run 2026-08-28. It is a
        // snapshot of what one version of the game's tables happen to contain, not a property
        // of the code, so a game update can make any of these numbers wrong without anything
        // here changing. Re-measure before quoting them; do not read them as current.
        //
        //   PickableItem, the older MineRock, LootSpawner, Container.m_defaultItems and
        //   Plant.m_grownPrefabs contribute ZERO writes. Delete all five and the index is the
        //   same 665 items with the same answers. The sub-log chain does fire - it derives
        //   RoundLog as blackforest and FineWood as meadows - but BiomeOverrides pins both, so
        //   it changes no answer either.
        //
        // Bug 2 is fixed entirely by the sibling-drop of key-gated spawn rows in SpawnIndex.
        // That clause on its own moves every item the meadows tier was wrongly carrying; none
        // of the walks below move any of them.
        //
        // They stay, and not out of sentiment. Each one is a correct reading of a table the
        // game really keeps, and the argument this whole file rests on is that it classifies
        // content nobody typed here - a mod that hangs an ore off a MineRock, or ships a crate
        // with a default inventory, lands in the right tier for free. Zero writes in vanilla is
        // what future-proofing looks like when the future has not arrived yet. The thing to
        // avoid is mistaking that for evidence they are load-bearing.
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Everything this prefab yields, recorded against the biome index it belongs to.
        ///
        /// In children, not just on the root: an ore deposit carries its MineRock5 on a child,
        /// and a creature its CharacterDrop beside the body rather than on it.
        ///
        /// The index is passed in rather than a mask, because a spawn row's answer is no
        /// longer "the earliest biome in its mask" - see SpawnIndex.
        /// </summary>
        private static void Harvest(Dictionary<string, Placement> found, GameObject prefab, int index)
        {
            if (prefab == null || index < 0) return;

            foreach (var pickable in prefab.GetComponentsInChildren<Pickable>(true))
                if (pickable != null) Record(found, pickable.m_itemPrefab, index, Derived);

            // PickableItem is the other half of picking things up - it holds an ItemDrop
            // rather than a GameObject, and its m_randomItemPrefabs is how one prefab in the
            // world stands in for several possible items. Both fields read off the decompiled
            // class, not guessed: a wrong field name in this file would throw at type-init and
            // take every Harmony patch the class carries down with it.
            //
            // Nothing the vegetation or spawn tables reach carried one as measured - 0.221.12,
            // 2026-08-28, see the block above - so this wrote nothing then. It is here for the
            // mod that does.
            foreach (var pickable in prefab.GetComponentsInChildren<PickableItem>(true))
            {
                if (pickable == null) continue;

                if (pickable.m_itemPrefab != null)
                    Record(found, pickable.m_itemPrefab.gameObject, index, Derived);

                if (pickable.m_randomItemPrefabs == null) continue;
                foreach (var random in pickable.m_randomItemPrefabs)
                    if (random.m_itemPrefab != null)
                        Record(found, random.m_itemPrefab.gameObject, index, Derived);
            }

            foreach (var rock in prefab.GetComponentsInChildren<MineRock5>(true))
                if (rock != null) Record(found, rock.m_dropItems, index, Derived);

            // MineRock and MineRock5 are two separate classes that both declare a public
            // DropTable m_dropItems, and vanilla uses both - MineRock5 is the newer batched
            // one and the older single-collider MineRock never went away. Walking only the
            // newer one is how a deposit would quietly contribute nothing.
            //
            // "Would": every deposit the vegetation table placed as measured - 0.221.12,
            // 2026-08-28, see the block above - was a MineRock5, so this loop wrote nothing
            // then. The older class is still live in the game and a mod may well use it, which
            // is the whole reason to read both.
            foreach (var rock in prefab.GetComponentsInChildren<MineRock>(true))
                if (rock != null) Record(found, rock.m_dropItems, index, Derived);

            foreach (var destroyed in prefab.GetComponentsInChildren<DropOnDestroyed>(true))
                if (destroyed != null) Record(found, destroyed.m_dropWhenDestroyed, index, Derived);

            // A LootSpawner is the thing that keeps re-stocking a fixed spot in the world -
            // the offerings on a stone, the loot in a dvergr crate. Where one exists its
            // DropTable is the only statement anywhere that those items belong to that place.
            // None hung off a vegetation or spawn prefab as measured - 0.221.12, 2026-08-28,
            // see the block above - because they live in locations, which this file cannot
            // walk, so this wrote nothing then.
            foreach (var loot in prefab.GetComponentsInChildren<LootSpawner>(true))
                if (loot != null) Record(found, loot.m_items, index, Derived);

            // A container standing in the world arrives holding its default items, and where
            // that table is filled it is the only route to what is inside. Same story as the
            // LootSpawner above: the containers that carry one are placed by locations, so
            // nothing reachable from here had a non-empty m_defaultItems as measured -
            // 0.221.12, 2026-08-28, see the block above.
            foreach (var container in prefab.GetComponentsInChildren<Container>(true))
                if (container != null) Record(found, container.m_defaultItems, index, Derived);

            foreach (var drop in prefab.GetComponentsInChildren<CharacterDrop>(true))
                RecordDrops(found, drop, index);

            // Spawners: a greydwarf nest, a surtling geyser, the skeleton piles in a crypt.
            // The creature is not in the world's spawn table at all - the thing that makes it
            // is - so without this every trophy and drop that only comes from a nest is
            // invisible to the index.
            foreach (var area in prefab.GetComponentsInChildren<SpawnArea>(true))
            {
                if (area == null || area.m_prefabs == null) continue;

                foreach (var spawn in area.m_prefabs)
                {
                    if (spawn == null || spawn.m_prefab == null) continue;

                    foreach (var drop in spawn.m_prefab.GetComponentsInChildren<CharacterDrop>(true))
                        RecordDrops(found, drop, index);
                }
            }

            // A cultivated plant becomes something else, and the something else is what you
            // actually pick. One hop and no further: m_grownPrefabs is an unconstrained
            // GameObject[] and a chain of plants growing into plants is not a shape vanilla
            // has, so recursing here would buy nothing and risk a cycle.
            //
            // Zero writes as measured - 0.221.12, 2026-08-28, see the block above - because a
            // cultivated plant is something you plant rather than something the world places,
            // and its crop is already on the map from the wild version. It is the mod that
            // grows a new thing this is for.
            foreach (var plant in prefab.GetComponentsInChildren<Plant>(true))
            {
                if (plant == null || plant.m_grownPrefabs == null) continue;

                foreach (var grown in plant.m_grownPrefabs)
                {
                    if (grown == null) continue;

                    foreach (var pickable in grown.GetComponentsInChildren<Pickable>(true))
                        if (pickable != null) Record(found, pickable.m_itemPrefab, index, Derived);

                    foreach (var drop in grown.GetComponentsInChildren<CharacterDrop>(true))
                        RecordDrops(found, drop, index);
                }
            }

            foreach (var tree in prefab.GetComponentsInChildren<TreeBase>(true))
            {
                if (tree == null) continue;
                Record(found, tree.m_dropWhenDestroyed, index, Derived);

                // The stump you leave behind is a separate prefab with its own drop table, so
                // the wood you get for clearing it is not reachable from the tree.
                //
                // DropOnDestroyed and not Destructible.m_spawnWhenDestroyed, which is the
                // field that looks right and is not: it is a GameObject, the *thing* the stump
                // turns into, not an item table. Recording it would have put a prefab name
                // that is not an item into the index and called it a drop.
                if (tree.m_stubPrefab != null)
                    foreach (var stub in tree.m_stubPrefab.GetComponentsInChildren<DropOnDestroyed>(true))
                        if (stub != null) Record(found, stub.m_dropWhenDestroyed, index, Derived);

                // A felled tree becomes a log, and the log is what actually drops the wood -
                // except that on every full log in the game as measured (0.221.12,
                // 2026-08-28: beech_log, Birch_log, Oak_log, FirTree_log, PineTree_log)
                // m_dropWhenDestroyed is EMPTY. The wood is one
                // more hop down, on the half-log the full log breaks into, reached through
                // m_subLogPrefab. Stopping at the full log is why FineWood and RoundLog were
                // reachable only through a hand-written override; following the chain derives
                // them from the tables the way everything else here is derived.
                //
                // Derives, and then does not decide. As measured (0.221.12, 2026-08-28) it
                // answers blackforest for RoundLog and meadows for FineWood, and
                // BiomeOverrides pins both names, so the chain
                // changes no shipped answer - and on RoundLog the two disagree, with the
                // typed-in meadows winning on purpose. What the chain buys is that the
                // overrides stop being the only thing holding those two up, and that a modded
                // tree brings its own wood along without anybody typing its name.
                HarvestLogChain(found, tree.m_logPrefab, index);
            }
        }

        /// <summary>
        /// A log, the half-log it breaks into, and so on down.
        ///
        /// Bounded by a seen-set rather than by a depth count, because the honest failure mode
        /// is a modded log whose sub-log points back at itself, and a depth cap would walk
        /// that four times before giving up rather than once.
        /// </summary>
        private static void HarvestLogChain(Dictionary<string, Placement> found, GameObject log, int index)
        {
            if (log == null) return;

            var seen = new HashSet<GameObject>();
            var pending = new Queue<GameObject>();
            pending.Enqueue(log);

            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (current == null || !seen.Add(current)) continue;

                foreach (var piece in current.GetComponentsInChildren<TreeLog>(true))
                {
                    if (piece == null) continue;

                    Record(found, piece.m_dropWhenDestroyed, index, Derived);
                    if (piece.m_subLogPrefab != null) pending.Enqueue(piece.m_subLogPrefab);
                }
            }
        }

        private static void RecordDrops(Dictionary<string, Placement> found, CharacterDrop drop, int index)
        {
            if (drop == null || drop.m_drops == null) return;

            foreach (var entry in drop.m_drops)
                if (entry != null) Record(found, entry.m_prefab, index, Derived);
        }

        // -----------------------------------------------------------------------------------
        // Conversions, bosses, recipes
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Bars and cooked food, taking the biome of what they are made from.
        ///
        /// Without this the mapping covers the ore you mine and not the bar you carry home,
        /// which is backwards - the bar is the load.
        /// </summary>
        private static bool Convert(Dictionary<string, Placement> found, ZNetScene scene)
        {
            var moved = false;

            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;

                var smelter = prefab.GetComponent<Smelter>();
                if (smelter != null && smelter.m_conversion != null)
                    foreach (var conversion in smelter.m_conversion)
                        moved |= Inherit(found, conversion.m_from, conversion.m_to);

                var cooking = prefab.GetComponent<CookingStation>();
                if (cooking != null && cooking.m_conversion != null)
                    foreach (var conversion in cooking.m_conversion)
                        moved |= Inherit(found, conversion.m_from, conversion.m_to);

                // Every mead in the game is a fermenter conversion and nothing else, so
                // without this the whole shelf stays unplaced.
                var fermenter = prefab.GetComponent<Fermenter>();
                if (fermenter != null && fermenter.m_conversion != null)
                    foreach (var conversion in fermenter.m_conversion)
                        moved |= Inherit(found, conversion.m_from, conversion.m_to);
            }

            return moved;
        }

        /// <summary>
        /// A boss's own drops - its trophy, and the thing you put on the sacrificial stones.
        ///
        /// Bosses are not in the spawn tables. They stand in a location that the world places
        /// deliberately, so nothing else here can see them. But the boss prefab knows the
        /// global key its death sets, and the host's boss table already says which biome that
        /// key belongs to, so the two together place its drops in its own biome without either
        /// side naming an item. Moder's trophy is a Mountain item because Moder's key is the
        /// Mountain's key.
        ///
        /// It also means a modded boss added to that table brings its own drops with it.
        ///
        /// A host that supplies no table at all - BiomeForKey left null - loses this pass
        /// entirely and nothing else. Boss trophies and their sacrificial offerings come out
        /// unplaced; every other route into the index is untouched, because none of them ask.
        /// </summary>
        private static void Bosses(Dictionary<string, Placement> found, ZNetScene scene)
        {
            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;

                var character = prefab.GetComponent<Character>();
                if (character == null || string.IsNullOrEmpty(character.m_defeatSetGlobalKey)) continue;

                var biome = BossBiome(character.m_defeatSetGlobalKey);
                if (biome == null) continue;

                var index = IndexOf(biome);
                if (index < 0) continue;

                foreach (var drop in prefab.GetComponentsInChildren<CharacterDrop>(true))
                    RecordDrops(found, drop, index);
            }
        }

        private static int IndexOf(string biome)
        {
            for (var i = 0; i < Ordered.Length; i++)
                if (Ordered[i].Key == biome) return i;

            return -1;
        }

        /// <summary>
        /// Everything craftable, taking the biome of its **latest** ingredient.
        ///
        /// Latest and not earliest, which is the opposite of the rule everywhere else, and for
        /// a reason: a found item belongs to the first place it turns up, but a made item
        /// belongs to the point you could first make it, and that is decided by the ingredient
        /// you get last. Bread is a Plains thing because barley is, however common the rest of
        /// it is.
        ///
        /// This is most of what the world's tables cannot reach on their own - every mead,
        /// every cooked meal, nails, arrows - and it lands them without a list. It also quietly
        /// corrects a wrong answer: charred warriors drop bronze, so the spawn tables place it
        /// in the Ashlands, and the recipe placing it in the Black Forest wins because the
        /// index keeps whichever is earlier.
        ///
        /// Two things this used to get wrong, and they are the same bug seen twice.
        ///
        /// First, it answered from whatever subset of its ingredients happened to be placed
        /// already. On pass one Craft runs before Convert, so no bar out of a smelter or blast
        /// furnace exists yet, and BoltBlackmetal - black metal, wood, feathers - answered
        /// "meadows" off the wood. So unless `permissive`, a recipe now says nothing at all
        /// until every one of its ingredients is on the map, and reports itself deferred so
        /// the caller knows to come back.
        ///
        /// Second, "latest" is not the operator for every recipe. Exactly one vanilla recipe
        /// sets m_requireOnlyOneIngredient - Recipe_Fish1, raw fish from any of Fish1..Fish12 -
        /// and for that one the ingredient list is an OR, not an AND. Taking the latest of an
        /// OR puts raw fish in the Ashlands, because three of the twelve fish live there.
        /// Earliest is the right reducer there, and it also means such a recipe never needs
        /// deferring: the earliest over a subset can only be too late, never too early, so it
        /// corrects itself downwards on later passes under the ordinary equal-confidence rule.
        /// </summary>
        private static bool Craft(Dictionary<string, Placement> found, bool permissive,
                                  out int deferred, List<string> guesses)
        {
            deferred = 0;

            var db = ObjectDB.instance;
            if (db == null || db.m_recipes == null) return false;

            var placedSomething = false;

            foreach (var recipe in db.m_recipes)
            {
                if (recipe == null || recipe.m_item == null || recipe.m_resources == null) continue;
                if (recipe.m_item.gameObject == null) continue;

                var total = 0;
                var known = 0;
                var latest = -1;
                var earliest = int.MaxValue;

                // The worst-sourced ingredient decides how well sourced the answer can be. A
                // dish made of a guess is a guess, however confidently the recipe is read.
                var confidence = Derived;

                foreach (var requirement in recipe.m_resources)
                {
                    if (requirement == null || requirement.m_resItem == null) continue;
                    if (requirement.m_resItem.gameObject == null) continue;

                    total++;

                    Placement placement;
                    if (!found.TryGetValue(requirement.m_resItem.gameObject.name, out placement)) continue;

                    known++;
                    if (placement.Index > latest) latest = placement.Index;
                    if (placement.Index < earliest) earliest = placement.Index;
                    if (placement.Confidence < confidence) confidence = placement.Confidence;
                }

                // Nothing it is made of has a biome yet. It may on a later pass.
                if (known == 0) continue;

                var single = recipe.m_requireOnlyOneIngredient;
                var value = single ? earliest : latest;

                var guessing = false;

                if (!single && known < total)
                {
                    deferred++;
                    if (!permissive) continue;

                    // Past the strict closure, so every ingredient still missing is one no
                    // source in the game can supply. Answering from the rest is the best
                    // available fact, but it is not the same kind of fact, and it is named.
                    confidence = Guessed;
                    guessing = true;
                }

                if (!Record(found, recipe.m_item.gameObject.name, value, confidence)) continue;

                placedSomething = true;

                // Named only once the guess has actually landed, which is why this sits after
                // Record and reads its answer. A Guessed value is refused outright where a
                // Derived placement already exists, and the first version of this added the
                // name regardless - so the warning listed items whose biome was never a guess
                // at all. A diagnostic that cries wolf about the well-sourced answers is worse
                // than no diagnostic, because this list is the only thing standing between a
                // guess and another release of it looking like a fact.
                if (guessing && guesses != null && !guesses.Contains(recipe.m_item.gameObject.name))
                    guesses.Add(recipe.m_item.gameObject.name);
            }

            return placedSomething;
        }

        /// <summary>Returns whether this actually placed something, so the loop knows to stop.</summary>
        private static bool Inherit(Dictionary<string, Placement> found, ItemDrop from, ItemDrop to)
        {
            if (from == null || to == null || from.gameObject == null || to.gameObject == null) return false;

            Placement placement;
            if (!found.TryGetValue(from.gameObject.name, out placement)) return false;

            // Capped at Derived rather than copied. A conversion out of a hand-pinned root is
            // still only derived - nobody typed the bar's biome, they typed the ore's - and
            // letting the bar inherit Pinned would make it unanswerable by any later fact.
            // Floored the other way too: a conversion out of a guess is a guess.
            var confidence = placement.Confidence < Derived ? placement.Confidence : Derived;

            return Record(found, to.gameObject.name, placement.Index, confidence);
        }

        /// <summary>"IronScrap:swamp, Coins:blackforest" - the ones no table can reach.</summary>
        private static void ApplyOverrides(Dictionary<string, Placement> found)
        {
            // Through the seam and read afresh, never captured: the host's copy of this is a
            // line in a .cfg and the index is rebuilt on every world load. A null host, or a
            // host whose setting is empty, means no overrides and no complaint - the loop
            // below simply has nothing to walk.
            var supplier = Overrides;
            var raw = (supplier == null ? null : supplier()) ?? "";

            foreach (var entry in raw.Split(','))
            {
                var text = entry.Trim();
                if (text.Length == 0) continue;

                var parts = text.Split(':');
                if (parts.Length != 2)
                {
                    Log.LogWarning(
                        "Ignoring biome override '" + text + "': expected prefab:biome.");
                    continue;
                }

                var biome = parts[1].Trim().ToLowerInvariant();
                var index = IndexOf(biome);

                if (index < 0)
                {
                    Log.LogWarning(
                        "Ignoring biome override '" + text + "': '" + biome + "' is not a biome.");
                    continue;
                }

                // Overwrites rather than taking the earliest, because it is the answer someone
                // typed on purpose and the whole reason it exists is that the tables are wrong
                // or silent about that item. Written at Pinned, which is what makes that stick
                // through the closure: an override applied before the loop can no longer be
                // undercut by a derived answer that merely happens to be earlier.
                found[parts[0].Trim()] = new Placement { Index = index, Confidence = Pinned };
            }
        }

        private static void Record(Dictionary<string, Placement> found, DropTable table,
                                   int index, int confidence)
        {
            if (table == null || table.m_drops == null) return;

            foreach (var drop in table.m_drops)
                if (drop.m_item != null) Record(found, drop.m_item.name, index, confidence);
        }

        private static void Record(Dictionary<string, Placement> found, GameObject prefab,
                                   int index, int confidence)
        {
            if (prefab != null) Record(found, prefab.name, index, confidence);
        }

        /// <summary>
        /// The one place anything is written, and the reason the whole build terminates.
        ///
        /// A write happens only when the pair (Confidence, -Index) strictly increases for that
        /// key, or when the key is new. Confidence is one of three values and Index is one of
        /// Ordered.Length, so any single key can be written at most 3 x 9 = 27 times before no
        /// write is possible for it ever again, and the key set is bounded by the number of
        /// distinct prefab names the tables mention. The total number of writes across a build
        /// is therefore finite. Every loop in this file continues only while something was
        /// written, so every loop must reach a pass that writes nothing. There is no cycle to
        /// find and no ordering to get right - which is the point, because the old code's
        /// answer depended on which pass a fact arrived in.
        ///
        /// Note the >= in the equal-confidence case: an equal answer is not a write. Without
        /// it two sources agreeing would report movement forever and the loop would only ever
        /// stop on its iteration cap.
        /// </summary>
        private static bool Record(Dictionary<string, Placement> found, string prefabName,
                                   int index, int confidence)
        {
            if (string.IsNullOrEmpty(prefabName) || index < 0 || index >= Ordered.Length) return false;

            Placement existing;
            if (found.TryGetValue(prefabName, out existing))
            {
                if (confidence < existing.Confidence) return false;
                if (confidence == existing.Confidence && index >= existing.Index) return false;
            }

            found[prefabName] = new Placement { Index = index, Confidence = confidence };
            return true;
        }

        /// <summary>
        /// The first biome in progression order named by this mask.
        ///
        /// A mask, not a value: an entry may name several biomes at once, and the earliest is
        /// the one where you first have to carry the thing home.
        /// </summary>
        private static int Earliest(Heightmap.Biome mask)
        {
            for (var i = 0; i < Ordered.Length; i++)
                if ((mask & Ordered[i].Value) != 0) return i;

            return -1;
        }
    }
}
