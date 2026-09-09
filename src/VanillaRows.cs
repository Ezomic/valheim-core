using HarmonyLib;
using UnityEngine;

namespace Ezomic.Core
{
    /// <summary>
    /// Contests the inventory height at the moment Valheim 1.0 asserts it, which is the only
    /// moment that counts.
    ///
    /// **The bug this exists for, because it cost a player a stack of ten.** 1.0 grew its own
    /// inventory-rows feature, and it evicts anything below the row count it believes in:
    ///
    ///     Game.SpawnPlayer      LoadPlayerData(player)  ->  player.OnSpawned(...)
    ///     Player.OnSpawned      reads the unique key "invrows"  ->  SetInventorySize(4)
    ///     Player.SetInventorySize   m_inventory.SetHeight(4)  ->  DropInvalidItems()
    ///     Humanoid.DropInvalidItems every item with gridPos.y >= GetHeight() is DROPPED
    ///
    /// Those first two calls are consecutive statements in one method, so no mod Update can
    /// interleave. A character carrying "invrows 4" therefore has its grid forced back to four
    /// rows on every single login, and anything a mod put in rows five and six is thrown on the
    /// ground at the spawn point - where it sits on ItemDrop's one-hour destruction clock.
    ///
    /// None of this exists in 0.221.12: DropInvalidItems, SetInventorySize and the invrows key
    /// are all new. Which is why the old approach worked for a year and cannot work now.
    ///
    /// **Why Core's old approach is unfixable rather than broken.** InventoryRows owned
    /// Inventory.m_height by reflection and wrote it from Update. That is a race it must always
    /// lose, because vanilla re-asserts the height from the saved key inside SpawnPlayer and the
    /// eviction happens in the same frame. The log shows exactly that: "Dropping 1 invalid
    /// positioned items." at 17:29:57, and Core's row tick twenty lines later. Core was writing
    /// six rows onto a grid vanilla had already emptied. A postfix on OnSpawned is no better -
    /// DropInvalidItems has already run inside SetInventorySize by then.
    ///
    /// So the height is claimed where vanilla sets it, and the eviction is fenced separately.
    /// </summary>
    [HarmonyPatch]
    internal static class VanillaRows
    {
        /// <summary>
        /// Vanilla's own key for the row count. Reading it is not required - the value arrives
        /// as SetInventorySize's argument - but the postfix has to write it back.
        /// </summary>
        private const string RowsKey = "invrows";

        /// <summary>
        /// Take vanilla's number as the base and hand back the total, before anything is
        /// applied.
        ///
        /// A prefix and not a postfix, and the ordering inside SetInventorySize is the reason:
        /// Mathf.Clamp, SetHeight, AddUniqueKeyValue, InventoryGui.SetInventorySize and
        /// DropInvalidItems all run after this returns. Growing the number here means the grid
        /// is already tall enough when vanilla scans it, so nothing is ever invalid, and the
        /// inventory panel is sized to the same number Core believes in rather than fighting it.
        ///
        /// The incoming value is a better baseline than reading the height ever was: it is
        /// vanilla's own count, before any mod touched anything. It also follows a player who
        /// buys rows through vanilla's shop, which the old reflection baseline could not tell
        /// apart from a mod's grant.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), nameof(Player.SetInventorySize))]
        private static void Grow(Player __instance, ref int rows)
        {
            try
            {
                // Plain == null and ReferenceEquals, never ?. - Unity overloads equality and the
                // null-propagating operators sail past a destroyed object.
                if (__instance == null) return;
                if (!ReferenceEquals(__instance, Player.m_localPlayer)) return;

                InventoryRows.LearnBase(rows);
                rows = rows + InventoryRows.Total;
            }
            catch (System.Exception e)
            {
                CorePlugin.Log.LogError("Core could not claim its inventory rows from "
                    + "SetInventorySize, so this login gets vanilla's height. " + e.Message);
            }
        }

        /// <summary>
        /// Put vanilla's own number back in the character.
        ///
        /// This is not tidiness, it is the difference between working and growing without
        /// bound. SetInventorySize ends with AddUniqueKeyValue("invrows", rows.ToString()), so
        /// it persists whatever the prefix above handed it. Leave that and the next login reads
        /// six, the prefix takes six as the new base, writes eight, and the character gains two
        /// rows every time it loads until vanilla's clamp of nine stops it.
        ///
        /// Writing the base back keeps the saved character describing vanilla, which also means
        /// uninstalling Core leaves a plain four-row character rather than a mystery. The cost
        /// is that vanilla then drops whatever sat in the granted rows - on the ground, not into
        /// nothing, and that is the honest outcome for rows a mod is no longer providing.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), nameof(Player.SetInventorySize))]
        private static void Restore(Player __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!ReferenceEquals(__instance, Player.m_localPlayer)) return;

                var vanilla = InventoryRows.Base;
                if (vanilla < 0) return;

                __instance.AddUniqueKeyValue(RowsKey, vanilla.ToString());
            }
            catch (System.Exception e)
            {
                CorePlugin.Log.LogError("Core could not restore the vanilla row count in the "
                    + "character, so the saved value now includes Core's rows and would compound "
                    + "on the next login. Set invrows back by hand if rows keep growing. "
                    + e.Message);
            }
        }

        /// <summary>
        /// Nothing standing in the grid gets thrown out, whatever the height says.
        ///
        /// The prefix above already prevents the ordinary case, so this is the fence rather than
        /// the fix - and it is the half that must not be skipped. SetInventorySize clamps to
        /// nine, so claims beyond that are silently truncated and everything below row nine
        /// would be evicted; and DropInvalidItems has other callers, including the console's
        /// inventory clean-up.
        ///
        /// Widening to what the items actually occupy, rather than to base plus claims, is
        /// deliberate: the question here is only "is anything about to be thrown away", and the
        /// answer must not depend on what any mod is currently claiming.
        ///
        /// It does neuter a console command that exists to clear a stuck inventory. That is an
        /// acceptable trade against dropping a player's things on the floor without asking.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DropInvalidItems))]
        private static void Hold(Humanoid __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!ReferenceEquals(__instance, Player.m_localPlayer)) return;

                var inventory = __instance.GetInventory();
                if (inventory == null) return;

                var occupied = 0;
                foreach (var item in inventory.GetAllItems())
                {
                    if (item == null) continue;
                    if (item.m_gridPos.y + 1 > occupied) occupied = item.m_gridPos.y + 1;
                }

                if (occupied <= inventory.GetHeight()) return;

                CorePlugin.Log.LogWarning("Vanilla was about to drop items from rows below its "
                    + "own count - holding the grid at " + occupied + " rows so nothing is thrown "
                    + "on the ground. Rows claimed: " + InventoryRows.Total + ".");

                inventory.SetHeight(occupied);
            }
            catch (System.Exception e)
            {
                CorePlugin.Log.LogError("Core could not fence DropInvalidItems, so vanilla may "
                    + "drop items from rows it does not know about. " + e.Message);
            }
        }
    }
}
