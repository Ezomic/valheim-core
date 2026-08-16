using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Ezomic.Core
{
    /// <summary>
    /// The one owner of the player inventory's height.
    ///
    /// Two mods that both want extra rows cannot simply each write Inventory.m_height: it is
    /// one private int, the last writer wins, and neither knows the other exists. Worse, a
    /// mod that writes only when its own state changes loses silently to one that writes every
    /// frame - its rows are there until something else touches the field, and then they are
    /// not. So each mod states a number and this adds them up and writes once.
    ///
    /// <code>
    /// InventoryRows.Claim(PluginGuid, 3);   // three rows, mine
    /// InventoryRows.Claim(PluginGuid, 0);   // give them back
    /// </code>
    ///
    /// Patching Inventory.GetHeight() instead would have been tidier and is wrong. The UI
    /// reads the accessor, but ValidPos, FindEmptySlot, HaveEmptySlot, NrOfFreeStacks,
    /// AddItem's bounds check and Load all read the field directly - so a postfix on the
    /// accessor draws rows the inventory itself does not believe in, and items cannot be put
    /// in them. The field is the only thing both halves agree on.
    ///
    /// The vanilla height is captured per player rather than added to, because anything that
    /// compounds grows a row every time the value is re-applied.
    /// </summary>
    public static class InventoryRows
    {
        private static readonly Dictionary<string, int> Claims = new Dictionary<string, int>();

        private static FieldInfo _height;
        private static Player _player;
        private static int _base = -1;
        private static int _applied = -1;
        private static bool _widened;

        /// <summary>
        /// How many rows the grid is opened to while a character is being read off disk.
        ///
        /// Generous on purpose and temporary by design: nothing is drawn during a load, and
        /// the tick that follows trims it straight back to base + claims, never below what
        /// the items themselves occupy. Sixteen is far more than any mod will claim and
        /// costs one int for the length of a load.
        /// </summary>
        private const int LoadSlack = 16;

        /// <summary>
        /// Ask for <paramref name="rows"/> extra rows, replacing whatever this mod asked for
        /// before. Zero gives them back. Cheap to call every frame.
        /// </summary>
        public static void Claim(string owner, int rows)
        {
            if (string.IsNullOrEmpty(owner)) return;

            rows = Mathf.Max(0, rows);

            if (Claims.TryGetValue(owner, out var had) && had == rows) return;

            Claims[owner] = rows;
            _applied = -1;   // force the next tick to write
        }

        /// <summary>Rows claimed by everyone, which is what the inventory grows by.</summary>
        public static int Total
        {
            get
            {
                var total = 0;
                foreach (var kv in Claims) total += kv.Value;
                return total;
            }
        }

        /// <summary>Driven from Core's own Update, so no mod has to own the timing.</summary>
        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                // A respawn builds a new Player with a fresh Inventory, and the old baseline
                // means nothing against it.
                _player = null;
                _base = -1;
                _applied = -1;
                return;
            }

            var inventory = player.GetInventory();
            if (inventory == null) return;

            if (!ReferenceEquals(player, _player))
            {
                _player = player;
                _base = inventory.GetHeight();
                _applied = -1;

                CorePlugin.Log.LogInfo("Inventory rows: vanilla height is " + _base + ".");
            }

            // Nothing has asked yet. Core writing 4 + 0 on the first frame, before any mod's
            // Update has run, briefly told an inventory holding items in row 7 that it was
            // four rows tall - harmless in practice and not a state worth passing through.
            // A mod that later claims 0 still gets written, because it has an entry by then.
            //
            // Unless a load has just widened the grid, in which case this must run even with
            // nothing claimed: the widening is temporary and something has to take it back
            // down, or an inventory stays sixteen rows tall for the session.
            if (Claims.Count == 0 && !_widened) return;

            var total = Total;
            if (total == _applied) return;

            if (_height == null) _height = AccessTools.Field(typeof(Inventory), "m_height");
            if (_height == null)
            {
                CorePlugin.Log.LogError("Inventory.m_height not found - extra rows cannot work.");
                _applied = total;
                return;
            }

            // Never below what is actually in the grid. Releasing rows is a real operation -
            // strip your armour and a mod that claimed rows for it gives them back - and the
            // items standing in those rows must not be sealed off behind the new edge.
            var wanted = Mathf.Max(_base + total, Occupied(inventory));

            _applied = total;
            _widened = false;
            _height.SetValue(inventory, wanted);

            CorePlugin.Log.LogInfo("Inventory rows: " + _base + " + " + total + " claimed by " +
                                   Claims.Count + " mod(s)" +
                                   (wanted > _base + total ? ", held at " + wanted + " by items in the grid" : "") + ".");

            Backdrop.Invalidate();
        }

        /// <summary>
        /// One past the lowest row anything is standing in, so the grid is never cut above
        /// its own contents. Rows given back while occupied stay until they are emptied.
        /// </summary>
        /// <summary>
        /// Opens the grid up before a character's items are read into it.
        ///
        /// Without this, every item in a claimed row is destroyed by loading the game, and
        /// nothing says so. Player.Load calls m_inventory.Load, which calls AddItem per
        /// stack, which begins:
        ///
        ///     if (x &lt; 0 || y &lt; 0 || x &gt;= m_width || y &gt;= m_height) return false;
        ///
        /// A saved position outside the current grid is dropped silently - not logged, not
        /// an error, and then written back out on the next save. Rows are applied from
        /// Core's Update, which cannot run until Player.m_localPlayer exists, and that is
        /// after the load. So the grid was always four rows tall at exactly the moment it
        /// mattered, and the bottom row was eaten on every relog.
        ///
        /// Found the long way round: a heartwood kept vanishing from a saved inventory, and
        /// the first theory was that the mod registering it lost a race with the load and
        /// left ObjectDB unable to resolve the name. That would have logged "Failed to find
        /// item prefab" and never did. The tell was a stack of wood in a middle row
        /// surviving the same relog that ate the bottom one - position, not identity.
        ///
        /// Widening rather than computing the right height, because the right height is not
        /// knowable here. Claims can be dynamic - a mod granting rows for armour has not
        /// been told about that armour yet at load - so any number derived from Claims is a
        /// guess. The tick that follows already refuses to shrink below Occupied(), so the
        /// items themselves decide what the grid ends up as, which is the correct authority.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), nameof(Player.Load))]
        private static void WidenBeforeLoad(Player __instance)
        {
            if (__instance == null) return;

            var inventory = __instance.GetInventory();
            if (inventory == null) return;

            if (_height == null) _height = AccessTools.Field(typeof(Inventory), "m_height");
            if (_height == null) return;

            // The baseline is captured here, before the widening, or the next tick would
            // read the widened value as vanilla and add every claim on top of it - and then
            // do it again on the following load. That compounds, which is the exact failure
            // the per-player capture at the top of Tick exists to avoid.
            _player = __instance;
            _base = inventory.GetHeight();
            _applied = -1;
            _widened = true;

            _height.SetValue(inventory, _base + LoadSlack);
        }

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

        /// <summary>
        /// The window behind the slots, grown to cover the extra rows.
        ///
        /// It lives here rather than in whichever mod claimed the rows, for the same reason
        /// the height does: two mods both stretching the same panel stretch it twice.
        /// </summary>
        internal static class Backdrop
        {
            private static InventoryGui _seen;
            private static int _shown = -1;

            private static readonly List<RectTransform> Panels = new List<RectTransform>();
            private static readonly List<float> Heights = new List<float>();

            // The container window sits under the player's, placed for a four row inventory.
            // Growing the one above it leaves the two overlapping - the bottom rows of the
            // inventory end up behind the chest panel, which is how this was noticed.
            private static RectTransform _container;
            private static Vector2 _containerBase;

            internal static void Invalidate()
            {
                _shown = -1;
            }

            internal static void Tick()
            {
                var gui = InventoryGui.instance;
                if (gui == null || gui.m_player == null)
                {
                    _seen = null;
                    return;
                }

                if (!ReferenceEquals(gui, _seen))
                {
                    _seen = gui;
                    _shown = -1;
                    Capture(gui);
                }

                var rows = Total;
                if (rows == _shown) return;

                _shown = rows;
                Resize(gui, rows);
            }

            private static void Capture(InventoryGui gui)
            {
                Panels.Clear();
                Heights.Clear();

                Remember(gui.m_player);

                _container = gui.m_container;
                if (_container != null) _containerBase = _container.anchoredPosition;

                // Found by the sprite it draws, then filtered by width. The sprite alone is
                // not enough: the armour and weight readouts down the right are cut from the
                // same woodpanel art, and growing those turned two small tabs into tall bars
                // beside a correctly sized panel.
                var full = gui.m_player.rect.width;

                foreach (var image in gui.m_player.GetComponentsInChildren<Image>(true))
                {
                    if (image == null || image.sprite == null) continue;
                    if (image.sprite.name.IndexOf("woodpanel", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (image.rectTransform.rect.width < full * 0.6f) continue;

                    Remember(image.rectTransform);
                }
            }

            private static void Remember(RectTransform rect)
            {
                if (rect == null || Panels.Contains(rect)) return;

                Panels.Add(rect);
                Heights.Add(rect.rect.height);
            }

            private static void Resize(InventoryGui gui, int rows)
            {
                // Not a guess: InventoryGrid lays its elements out at i * -m_elementSpace, so
                // one row is exactly that tall.
                var grid = gui.m_player.GetComponentInChildren<InventoryGrid>(true);
                if (grid == null || grid.m_elementSpace <= 0f) return;

                var added = rows * grid.m_elementSpace;

                for (var i = 0; i < Panels.Count; i++)
                {
                    if (Panels[i] == null) continue;

                    Panels[i].SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Heights[i] + added);
                }

                // Pushed down by exactly what the inventory gained, from its own captured
                // baseline rather than by nudging it each time, so opening a chest twice does
                // not walk it off the screen.
                if (_container != null)
                    _container.anchoredPosition = _containerBase + new Vector2(0f, -added);
            }
        }
    }
}
