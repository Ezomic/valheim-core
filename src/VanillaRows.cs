using HarmonyLib;

namespace Ezomic.Core
{
    /// <summary>
    /// Contests the inventory height at the moment Valheim 1.0 asserts it, which is the only
    /// moment that counts.
    ///
    /// **The bug this exists for, because it cost a player four stacks.** 1.0 grew its own
    /// inventory-rows feature, and it evicts anything below the row count it believes in:
    ///
    ///     Game.SpawnPlayer          LoadPlayerData(player) -> player.OnSpawned(...)
    ///     Player.OnSpawned          reads the "invrows" key -> SetInventorySize(4)
    ///     Player.SetInventorySize   m_inventory.SetHeight(4) -> DropInvalidItems()
    ///     Humanoid.DropInvalidItems every item with gridPos.y >= GetHeight() is DROPPED
    ///
    /// Those first two calls are consecutive statements in one method, so no mod Update can
    /// interleave. A character carrying "invrows 4" therefore has its grid forced back to four
    /// rows on every login, and anything a mod put in rows five and six is thrown on the ground
    /// at the spawn point - where it sits on ItemDrop's one-hour destruction clock.
    ///
    /// None of this exists in 0.221.12: DropInvalidItems, SetInventorySize and the invrows key
    /// are all new. Which is why the old approach worked for a year and cannot work now.
    ///
    /// **Why Core's old approach is unfixable rather than broken.** InventoryRows owned
    /// Inventory.m_height by reflection and wrote it from Update. That is a race it must always
    /// lose, because vanilla re-asserts the height from the saved key inside SpawnPlayer and the
    /// eviction happens in the same frame. The log showed exactly that: "Dropping 1 invalid
    /// positioned items." and Core's row tick twenty lines later, writing six rows onto a grid
    /// vanilla had already emptied. A postfix on OnSpawned is no better - DropInvalidItems has
    /// already run inside SetInventorySize by then.
    ///
    /// **Two top-level classes, one per patched method, and it took three tries to get there.**
    /// First shape: one class, a bare [HarmonyPatch], three method attributes naming two
    /// different types - Harmony takes the class attribute as the target and merges the method
    /// ones into it, so a class describing two targets describes neither, and nothing attached.
    /// Second shape: nested classes per target - correct in isolation, but Harmony.PatchAll(Type)
    /// does not recurse into nested types, so CorePlugin patched the outer shell and again
    /// nothing attached. Both times the game ran, Core printed its ready line, and the only
    /// symptom was a player's items on the floor.
    ///
    /// Hence CorePlugin.Verify: it asks Harmony which methods it actually holds patches on and
    /// names any that are missing. That check is what caught the second failure at startup
    /// instead of after another round of losing things.
    /// </summary>
    internal static class VanillaRows
    {
        /// <summary>
        /// Vanilla's own key for the row count. Reading it is not required - the value arrives
        /// as SetInventorySize's argument - but the postfix has to write it back.
        ///
        /// Internal rather than private because the two patch classes below are siblings now,
        /// not nested, and both halves of the mechanism have to agree on the spelling.
        /// </summary>
        internal const string RowsKey = "invrows";

        /// <summary>
        /// The tallest grid vanilla will accept - Player.SetInventorySize does
        /// Mathf.Clamp(rows, 0, 9) and there is no way round it from a prefix.
        ///
        /// Read from the game rather than written down where that is possible, but this one is a
        /// literal inside a Mathf.Clamp call with no field or property behind it, so it has to be
        /// copied. If a future version moves it, the symptom is Core granting fewer rows than it
        /// could - never more, and never an eviction - because the fence in VanillaRowsDrop
        /// catches the other direction.
        /// </summary>
        internal const int Ceiling = 9;
    }

    /// <summary>Where vanilla decides how tall the grid is, and then enforces it.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.SetInventorySize))]
    internal static class VanillaRowsSize
    {
        /// <summary>
        /// Take vanilla's number as the base and hand back the total, before anything is applied.
        ///
        /// A prefix and not a postfix, and the ordering inside SetInventorySize is the reason:
        /// Mathf.Clamp, SetHeight, AddUniqueKeyValue, InventoryGui.SetInventorySize and
        /// DropInvalidItems all run after this returns. Growing the number here means the grid is
        /// already tall enough when vanilla scans it, so nothing is ever invalid, and the
        /// inventory panel is sized to the number Core believes in rather than fighting it.
        ///
        /// The incoming value is a better baseline than reading the height ever was: it is
        /// vanilla's own count, before any mod touched anything, and it follows a player who buys
        /// rows through vanilla's shop - which the old measured baseline could not tell apart
        /// from a mod's grant.
        /// </summary>
        [HarmonyPrefix]
        private static void Grow(Player __instance, ref int rows)
        {
            try
            {
                // Plain == null and ReferenceEquals, never ?. - Unity overloads equality and the
                // null-propagating operators sail past a destroyed object.
                if (__instance == null) return;
                if (!ReferenceEquals(__instance, Player.m_localPlayer)) return;

                InventoryRows.LearnBase(rows);

                // Clamped here rather than left to vanilla, and Core is the one that yields.
                //
                // SetInventorySize does Mathf.Clamp(rows, 0, 9) immediately after this returns and
                // then calls DropInvalidItems, so handing it a bigger number does not buy a taller
                // grid - it buys an eviction. That is reachable without any mod misbehaving:
                // Valheim 1.0 sells inventory rows through a trader (StoreGui checks
                // m_incrementKey == "invrows" and calls SetInventorySize), and the console sets
                // them too. A player who has bought four rows arrives here with a base of eight,
                // and eight plus two claimed is ten.
                //
                // So the claim gives way to the purchase. A player who paid for space keeps every
                // row and every item in it; the mod grants whatever is left and says so. The
                // reverse - refusing the trader to protect a mod's bonus - would take a vanilla
                // feature away to preserve something a mod added, which is the wrong way round,
                // and it would not help a character that arrives already carrying nine rows.
                var room = VanillaRows.Ceiling - rows;
                if (room < 0) room = 0;

                var claimed = InventoryRows.Total;
                var granted = claimed < room ? claimed : room;

                if (granted < claimed)
                    InventoryRows.SayTruncated(rows, claimed, granted);

                rows = rows + granted;
            }
            catch (System.Exception e)
            {
                CorePlugin.Log.LogError("Core could not claim its inventory rows from "
                    + "SetInventorySize, so this login gets vanilla's height and anything in a "
                    + "granted row is about to be dropped. " + e.Message);
            }
        }

        /// <summary>
        /// Put vanilla's own number back in the character.
        ///
        /// This is not tidiness, it is the difference between working and growing without bound.
        /// SetInventorySize ends with AddUniqueKeyValue("invrows", rows.ToString()), so it
        /// persists whatever the prefix above handed it. Leave that and the next login reads six,
        /// treats six as the base, writes eight, and the character gains the claim total on every
        /// load until vanilla's clamp of nine stops it.
        ///
        /// Writing the base back also means uninstalling Core leaves a plain character. The cost
        /// is that vanilla then drops whatever sat in the granted rows - on the ground, not into
        /// nothing, which is the honest outcome for rows nothing is providing.
        /// </summary>
        [HarmonyPostfix]
        private static void Restore(Player __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!ReferenceEquals(__instance, Player.m_localPlayer)) return;

                var vanilla = InventoryRows.Base;
                if (vanilla < 0) return;

                __instance.AddUniqueKeyValue(VanillaRows.RowsKey, vanilla.ToString());
            }
            catch (System.Exception e)
            {
                CorePlugin.Log.LogError("Core could not restore the vanilla row count in the "
                    + "character, so the saved value now includes Core's rows and will compound "
                    + "on the next login. " + e.Message);
            }
        }
    }

    /// <summary>Where vanilla throws out anything below the height it just set.</summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DropInvalidItems))]
    internal static class VanillaRowsDrop
    {
        /// <summary>
        /// Nothing standing in the grid gets thrown out, whatever the height says.
        ///
        /// The prefix in VanillaRowsSize already prevents the ordinary case, so this is the fence
        /// rather than the fix - and it is the half that must not be skipped. SetInventorySize
        /// clamps to nine, so claims past five granted rows would be truncated and everything
        /// below nine evicted; and DropInvalidItems has other callers, including the console's
        /// inventory clean-up.
        ///
        /// Widening to what the items actually occupy, rather than to base plus claims, is
        /// deliberate: the question here is only "is anything about to be thrown away", and the
        /// answer must not depend on what any mod is currently claiming.
        ///
        /// It does neuter a console command meant to clear a stuck inventory. That is the right
        /// trade against dropping someone's things on the floor unasked.
        /// </summary>
        [HarmonyPrefix]
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
