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
        /// The height last written to the grid, which is not base + claims: it is that or
        /// what the items occupy, whichever is larger. Anything that has to match the grid
        /// reads this and nothing else.
        ///
        /// -1 until a height has actually been written, which is also the answer when the
        /// field could not be reflected at all - in both cases the grid is still vanilla.
        /// </summary>
        private static int _effective = -1;

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

        /// <summary>
        /// Rows claimed by everyone. What the mods asked for, which is a floor and not the
        /// height - see <see cref="Extra"/>.
        /// </summary>
        public static int Total
        {
            get
            {
                var total = 0;
                foreach (var kv in Claims) total += kv.Value;
                return total;
            }
        }

        /// <summary>
        /// Rows the grid is actually taller than vanilla by.
        ///
        /// Not <see cref="Total"/>, and the difference is the whole point: a load can leave
        /// items standing in rows nobody claimed, and the tick refuses to cut the grid above
        /// them. One claimed row over a four row inventory holding something in row 9 is a
        /// nine row grid, so Total says 1 and the truth is 5. Anything sized to Total in that
        /// state is sized to a grid that is not on screen.
        ///
        /// Zero until a height has been written, rather than falling back to Total: at that
        /// point nothing has grown the grid, so vanilla is the honest answer and Total would
        /// be a promise of rows that are not there yet.
        /// </summary>
        public static int Extra
        {
            get
            {
                if (_base < 0 || _effective < 0) return 0;

                return Mathf.Max(0, _effective - _base);
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
                _effective = -1;
                return;
            }

            var inventory = player.GetInventory();
            if (inventory == null) return;

            if (!ReferenceEquals(player, _player))
            {
                _player = player;
                _base = inventory.GetHeight();
                _applied = -1;
                _effective = -1;

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

            // The height the claims actually justify. What the grid ends up as may still be
            // taller, but only for items this could not rescue.
            var honest = _base + total;

            // Move stragglers up before measuring, or the grid can only ever shrink by luck.
            //
            // The rule below - never cut above an occupied row - is right and stays. On its
            // own it is also permanent: Occupied() reads the *lowest* occupied row, so one
            // item parked in row 9 holds all nine rows open however little you are carrying,
            // and nothing here ever moved it. Core waited for the player to resolve a
            // condition the player had no way of knowing about. Seen in a real session as
            // four vanilla rows plus one claimed row displaying as nine, cleared only by
            // emptying the pack and relogging.
            // Only reached when the claim total changed or a load just happened, because of
            // the early return above. That is the case that matters - the observed failure
            // was a grid arriving from disk already held open - but it does mean emptying a
            // stranded row mid-session does not shrink the grid until something else moves.
            // Re-measuring every frame to catch that would walk the item list every frame
            // for a state that resolves itself on the next login.
            var rescued = Occupied(inventory) > honest ? Compact(inventory, honest) : 0;

            // Never below what is actually in the grid. Releasing rows is a real operation -
            // strip your armour and a mod that claimed rows for it gives them back - and the
            // items standing in those rows must not be sealed off behind the new edge.
            // Anything Compact could not find room for still holds its row.
            var wanted = Mathf.Max(honest, Occupied(inventory));

            _applied = total;
            _widened = false;
            _effective = wanted;
            _height.SetValue(inventory, wanted);

            CorePlugin.Log.LogInfo("Inventory rows: " + _base + " + " + total + " claimed by " +
                                   Claims.Count + " mod(s)" +
                                   (rescued > 0 ? ", " + rescued + " item(s) moved up" : "") +
                                   (wanted > honest ? ", held at " + wanted + " by items in the grid" : "") + ".");

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
        /// guess. The tick that follows refuses to shrink below Occupied() and moves what it
        /// can up out of the way first, so the items themselves decide what the grid ends up
        /// as, which is the correct authority.
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

            // Deliberately not the slack height. The widening is a working space for the
            // load and never a height anything should be drawn against; the tick that
            // follows writes the real one, and until it does the honest answer is that
            // nothing has grown yet.
            _effective = -1;

            _height.SetValue(inventory, _base + LoadSlack);
        }

        /// <summary>
        /// Moves items standing at or below <paramref name="keep"/> into free slots above it,
        /// so the rows about to be released are empty when they are released. Returns how many
        /// were moved.
        ///
        /// Empty slots only, never a stack merge. Merging would have to reason about quality,
        /// variant and world level to be correct, and getting that wrong destroys part of a
        /// stack - which is the one failure something sitting this close to a player's
        /// inventory cannot have. A merge would only ever help a pack that is nearly full,
        /// and a pack that is nearly full keeps its rows, which is the old behaviour and is
        /// safe.
        ///
        /// The stranded items are collected before any of them move. Walking the live list
        /// while rewriting the very positions it is being filtered on skips half of them.
        /// </summary>
        private static int Compact(Inventory inventory, int keep)
        {
            if (keep < 1) return 0;

            var stranded = new List<ItemDrop.ItemData>();
            foreach (var item in inventory.GetAllItems())
                if (item != null && item.m_gridPos.y >= keep) stranded.Add(item);

            if (stranded.Count == 0) return 0;

            var width = inventory.GetWidth();
            var moved = 0;

            foreach (var item in stranded)
            {
                var slot = FreeSlot(inventory, width, keep);

                // Nothing above the line. Everything after this one is stranded too, so the
                // grid keeps its height exactly as it did before this method existed.
                if (slot.y < 0) break;

                item.m_gridPos = slot;
                moved++;
            }

            // Not Inventory.Changed(), which is private and also recomputes total weight.
            // Nothing here adds or removes an item, so the weight cannot have changed; what
            // has to happen is a redraw, and that is what m_onChanged is.
            if (moved > 0 && inventory.m_onChanged != null) inventory.m_onChanged();

            return moved;
        }

        /// <summary>
        /// The first empty slot strictly above <paramref name="keep"/>.
        ///
        /// Hand-rolled rather than Inventory.FindEmptySlot, which is private and scans to
        /// m_height - and m_height at this moment is still the old tall value, so it would
        /// happily hand back a slot in the very rows being released.
        /// </summary>
        private static Vector2i FreeSlot(Inventory inventory, int width, int keep)
        {
            for (var y = 0; y < keep; y++)
                for (var x = 0; x < width; x++)
                    if (inventory.GetItemAt(x, y) == null) return new Vector2i(x, y);

            return new Vector2i(-1, -1);
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

                // Extra, not Total. This asked Claims how tall the grid was and Claims does
                // not know: the tick clamps the height up to what the items occupy, so an
                // inventory holding something below the claimed rows drew nine rows of slots
                // over a panel grown by one. The lower rows had no wood behind them at all.
                //
                // It is also what the guard has to compare. On Total the panel only ever
                // redrew when a mod changed its claim, so a height that moved for any other
                // reason - a load finding items in a low row, rows given back while still
                // occupied - left the panel where it was and nothing ever corrected it.
                var rows = Extra;
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
