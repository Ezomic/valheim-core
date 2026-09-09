using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Ezomic.Core
{
    /// <summary>
    /// Stops a saved inventory losing whatever sits below the grid it is being read into.
    ///
    /// <see cref="InventoryRows"/> already guards the player against this, by widening the
    /// grid before Player.Load. That fixed relogging and left the other half standing: a
    /// **grave**. It is the same bug through a different door, and it is the worse one,
    /// because the items it eats are the ones you died holding.
    ///
    /// How a death loses the bottom row, in vanilla's own code:
    ///
    ///   1. Player.CreateTombStone calls Inventory.MoveInventoryToGrave, which copies the
    ///      player's m_width and m_height onto the grave. So the grave is born as tall as
    ///      the player was and holds the extra rows correctly. **Nothing is lost here**,
    ///      which is why looting your own grave immediately looks fine.
    ///   2. The grave is a Container with a ZNetView, so its inventory round-trips through
    ///      the ZDO. Height is not serialised - Inventory.Save writes a version, a count and
    ///      the items - so the grid is rebuilt from the *tombstone prefab's* height, which
    ///      is vanilla.
    ///   3. Inventory.Load re-adds each item at its saved position, and the private
    ///      AddItem(string, ...) it calls for that ends:
    ///
    ///          AddItem(component.m_itemData, component.m_itemData.m_stack, pos.x, pos.y);
    ///          UnityEngine.Object.Destroy(gameObject);
    ///          return true;
    ///
    ///      The positional AddItem begins `if (x &lt; 0 || y &lt; 0 || x &gt;= m_width ||
    ///      y &gt;= m_height) return false;` - and **that result is discarded**. The item is
    ///      instantiated, refused by the bounds check, never added, and destroyed. The method
    ///      returns true regardless, so there is not even a false to notice. Then the grave
    ///      saves again without it.
    ///
    /// The loss is therefore silent *and* delayed: loot the grave before its zone unloads and
    /// everything is there, relog or walk away first and the bottom row is gone. That is what
    /// made it read as random.
    ///
    /// The fix is the same shape as the player one and deliberately so - open the grid up,
    /// let the load put things where they were, then let the contents decide the height. It
    /// is applied to **every** inventory rather than only graves, because the defect is not
    /// specific to graves: any container read into a grid shorter than the one that wrote it
    /// deletes the difference, and no caller of Load can be told apart at this level anyway.
    /// Widening can only ever keep an item that would otherwise have been destroyed, so the
    /// blast radius of being wrong here is a container that draws a row taller than its
    /// prefab until it is emptied.
    /// </summary>
    [HarmonyPatch]
    internal static class InventoryLoad
    {
        private static FieldInfo _height;

        /// <summary>
        /// Rows added for the duration of a load. Matches InventoryRows.LoadSlack in spirit
        /// and in size: far more than any mod claims, and paid back before the frame ends.
        ///
        /// It is a bound rather than a guarantee, and the bound is honest - an inventory
        /// saved by a grid more than this much taller still loses the excess. Nothing in this
        /// repo comes close, and the alternative is reading the package to find out, which
        /// means consuming and rewinding a stream mid-load to answer a question that has
        /// never had a different answer.
        /// </summary>
        private const int LoadSlack = 16;

        /// <summary>
        /// Height the grid had before this load, so the postfix can put it back rather than
        /// leave the slack in place. Harmony's __state, so it nests correctly: the player's
        /// inventory arrives here already widened by InventoryRows, and this restores what it
        /// found instead of overwriting it with a computed value.
        /// </summary>
        // Valheim 1.0 added a second Load overload - Load(ZPackage, bool) beside Load(ZPackage) -
        // so naming the method alone is now an ambiguous match and Harmony refuses it. The two
        // bodies are the same work and either can be the one a container or a player arrives
        // through, so both are patched rather than a guess being made about which matters.
        /// <summary>
        /// Both Load overloads, returned as a list rather than stacked as attributes.
        ///
        /// This is the correction to a fix made earlier the same day, and the failure is worth
        /// recording because it looked like it worked. Valheim 1.0 added Load(ZPackage, bool)
        /// beside Load(ZPackage), which made naming the method alone ambiguous, and the first
        /// attempt at naming both was two [HarmonyPatch] attributes stacked on each patch
        /// method. Harmony does not read that as two targets: stacked attributes are MERGED
        /// into one HarmonyMethod, so the later argument list simply replaced the earlier one
        /// and only Load(ZPackage, bool) was ever patched.
        ///
        /// Nothing said so. The patch applied cleanly to a real method, Core printed its
        /// ready line, and the player inventory - which goes through Player.Load and therefore
        /// through the single-argument overload - was left unprotected.
        ///
        /// A missing overload is reported rather than silently skipped, because an empty
        /// target list is the one outcome Harmony treats as success while doing nothing.
        /// </summary>
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            var found = new List<MethodBase>();

            var one = AccessTools.Method(typeof(Inventory), nameof(Inventory.Load),
                                         new[] { typeof(ZPackage) });
            var two = AccessTools.Method(typeof(Inventory), nameof(Inventory.Load),
                                         new[] { typeof(ZPackage), typeof(bool) });

            if (one != null) found.Add(one);
            if (two != null) found.Add(two);

            if (one == null)
                CorePlugin.Log.LogError("Inventory.Load(ZPackage) is gone - the player's own "
                    + "inventory is the one that loads through it, so its rows are unprotected.");

            if (found.Count == 0)
                CorePlugin.Log.LogError("No Inventory.Load overload could be found at all.");

            return found;
        }

        [HarmonyPrefix]
        private static void Widen(Inventory __instance, out int __state)
        {
            __state = -1;

            if (__instance == null) return;

            if (_height == null) _height = AccessTools.Field(typeof(Inventory), "m_height");
            if (_height == null) return;

            __state = __instance.GetHeight();
            _height.SetValue(__instance, __state + LoadSlack);
        }

        /// <summary>
        /// Back to what it was, or to what the items need - whichever is taller.
        ///
        /// Never below the contents, for exactly the reason the load needed widening in the
        /// first place: an item standing in a row the grid does not believe in is an item the
        /// next save will drop. Holding the row open is how it survives long enough to be
        /// taken out, and once it is, the following load returns the grid to its prefab
        /// height on its own.
        /// </summary>
        [HarmonyPostfix]
        private static void Trim(Inventory __instance, int __state)
        {
            if (__instance == null || __state < 0 || _height == null) return;

            var occupied = Occupied(__instance);
            var wanted = Mathf.Max(__state, occupied);

            _height.SetValue(__instance, wanted);

            // Only when it actually mattered. A rescue is rare and worth a line; every chest
            // in every zone loading normally is not.
            if (wanted > __state)
                CorePlugin.Log.LogInfo("Inventory load: held at " + wanted + " rows rather than " +
                                       __state + " - something was saved below the grid, and " +
                                       "cutting to fit would have destroyed it.");
        }

        /// <summary>One past the lowest row anything is standing in.</summary>
        private static int Occupied(Inventory inventory)
        {
            var lowest = 0;

            foreach (var item in inventory.GetAllItems())
            {
                if (item == null) continue;
                if (item.m_gridPos.y + 1 > lowest) lowest = item.m_gridPos.y + 1;
            }

            return lowest;
        }
    }
}
