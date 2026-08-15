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

            var total = Total;
            if (total == _applied) return;

            if (_height == null) _height = AccessTools.Field(typeof(Inventory), "m_height");
            if (_height == null)
            {
                CorePlugin.Log.LogError("Inventory.m_height not found - extra rows cannot work.");
                _applied = total;
                return;
            }

            _applied = total;
            _height.SetValue(inventory, _base + total);

            CorePlugin.Log.LogInfo("Inventory rows: " + _base + " + " + total + " claimed by " +
                                   Claims.Count + " mod(s).");

            Backdrop.Invalidate();
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

                for (var i = 0; i < Panels.Count; i++)
                {
                    if (Panels[i] == null) continue;

                    Panels[i].SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                                                        Heights[i] + rows * grid.m_elementSpace);
                }
            }
        }
    }
}
