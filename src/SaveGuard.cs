using HarmonyLib;
using UnityEngine;

namespace Ezomic.Core
{
    /// <summary>
    /// Closes the window a crash steals your inventory through.
    ///
    /// Your character file is not written when you pick something up. It is written by
    /// Game.UpdateSaving on a timer, by Shutdown on a clean quit, and by SleepStop when you
    /// sleep. The timer is Game.m_saveInterval, and it is **1800 seconds**. So the honest
    /// description of vanilla is that up to half an hour of your character exists only in
    /// memory, and a crash - or a power cut, or the game being killed - throws all of it away.
    ///
    /// On a dedicated server that is worse than a rollback, because only one of the two
    /// machines rolls back. Pull a stack of iron out of a chest and crash: the server saved
    /// the chest empty on its own schedule, your character reverts to before you opened it,
    /// and the iron now exists nowhere. The same trade in the other direction duplicates it.
    /// The moment your inventory and the world disagree is the moment worth writing, and it
    /// is the moment vanilla is least likely to be writing.
    ///
    /// **Why Inventory.Changed and not the container window.** Hooking a chest closing covers
    /// chests. Changed is the game's own answer to "the player's items are not what they
    /// were", and every route reaches it - chests, crafting, pickups, drops, quick-stack,
    /// eating, a repair that consumes nothing. One patch, no list of doors to keep up to date.
    ///
    /// **Why it is a flag and not a save.** Dumping a haul into a wall of chests fires Changed
    /// once per chest, and saving each time would be twenty writes in twenty seconds - twenty
    /// Steam cloud round trips for one trip to the base. So Changed only marks the character
    /// dirty; Tick does at most one save per <see cref="CorePlugin.SaveGap"/> seconds and
    /// clears the flag. A run of chests collapses into a handful of saves, and the last one
    /// always lands, because the flag stays set until a save actually happens.
    ///
    /// **Why it skips the minimap.** Game.SavePlayerProfile is three things: SavePlayerData,
    /// which serialises the player and is cheap; Minimap.SaveMapData, which recompresses the
    /// explored map - 8.4MB down to 9.3KB in this machine's own log - and is by far the most
    /// expensive; and profile.Save(), the 20KB write. Only the first and third are needed to
    /// keep your items, and both are public, so this calls those two directly. The map is
    /// still written by vanilla's own thirty-minute save, which is left running untouched:
    /// m_saveTimer is deliberately **not** reset here, so this adds saves rather than
    /// replacing them, and losing a few minutes of fog is not the failure anyone minds.
    /// </summary>
    internal static class SaveGuard
    {
        /// <summary>Set by the patch, cleared by the save. Never read from anywhere else.</summary>
        private static bool _dirty;

        private static float _lastSave;

        /// <summary>
        /// Which Player the timings above belong to. A respawn builds a new one, and its
        /// inventory is refilled from the profile on the way in - see Tick.
        /// </summary>
        private static Player _player;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Inventory), "Changed")]
        private static void Noticed(Inventory __instance)
        {
            if (!CorePlugin.SaveAfterChanges.Value) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            // Every container in the world has an Inventory and they all raise this. Ours is
            // the only one whose contents live in the character file; a chest's contents are
            // a ZDO and the server's problem.
            if (!ReferenceEquals(__instance, player.GetInventory())) return;

            _dirty = true;
        }

        internal static void Tick()
        {
            if (!CorePlugin.SaveAfterChanges.Value) return;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                _player = null;
                _dirty = false;
                return;
            }

            if (!ReferenceEquals(player, _player))
            {
                // Loading a character re-adds every item one at a time, so spawning raises
                // Changed dozens of times for a state that came off the disk moments ago.
                // Starting the clock here rather than at zero folds all of that away and
                // costs nothing: the first real change still saves one gap later.
                _player = player;
                _dirty = false;
                _lastSave = Time.time;
                return;
            }

            if (!_dirty) return;
            if (Time.time - _lastSave < CorePlugin.SaveGap.Value) return;

            Save();
        }

        private static void Save()
        {
            var game = Game.instance;
            if (game == null) return;

            var profile = game.GetPlayerProfile();
            if (profile == null) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            // The intro is the one state vanilla itself refuses to write a position for, and
            // a character that has not finished arriving has nothing worth keeping anyway.
            if (player.InIntro()) return;

            // Vanilla checks this before every save it makes, and for the same reason: below
            // the block limit the write fails, and a failed write can leave the file worse
            // than the one it replaced. Being dead is not checked - a corpse whose items are
            // in a tombstone is a correct thing to save, and the tombstone is the world's.
            if (ZNet.instance != null && !ZNet.instance.EnoughDiskSpaceAvailable(out _)) return;

            profile.SavePlayerData(player);
            profile.Save();

            _dirty = false;
            _lastSave = Time.time;

            // Debug rather than info. This runs every half minute of active play, and a log
            // line that frequent stops being read and starts hiding the ones that matter.
            CorePlugin.Log.LogDebug("Character saved after an inventory change.");
        }
    }
}
