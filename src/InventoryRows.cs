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
        /// The grid height as it stood immediately before a load widened it, or -1.
        ///
        /// This is NOT the baseline and must never be treated as one - LearnBase owns that.
        /// It exists because the fallback below used to read GetHeight() after the widening
        /// had already happened, which measures the working space rather than the inventory.
        /// </summary>
        private static int _preWiden = -1;

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
        /// Vanilla's own row count for this character, as it was before Core touched anything.
        ///
        /// -1 until it is known. See <see cref="LearnBase"/> for why it is learned rather than
        /// measured.
        /// </summary>
        internal static int Base
        {
            get { return _base; }
        }

        /// <summary>
        /// Take vanilla's row count from the one place that states it plainly.
        ///
        /// This used to be measured - _base = inventory.GetHeight() on first sight of the player
        /// - and on Valheim 1.0 that is wrong in a way that compounds. 1.0 asserts the height
        /// from the character's own "invrows" key inside SpawnPlayer, so by the time Update runs
        /// the height already includes whatever Core wrote last time. Measuring it would read
        /// six, add the claims again, and grow the grid on every login.
        ///
        /// VanillaRows calls this from a prefix on Player.SetInventorySize with the argument
        /// vanilla passed, which is its number and nobody else's.
        ///
        /// It also stands in for the first-sight branch in Tick: setting _player here is what
        /// stops that branch measuring the height and overwriting this.
        /// </summary>
        /// <summary>
        /// Said when vanilla's own row count leaves no room for everything claimed.
        ///
        /// Once per distinct shortfall, because SetInventorySize runs on every spawn and this
        /// would otherwise be one line per login forever. It is a warning rather than info: a mod
        /// asked for rows and did not get them, so anything reasoning about the total is now
        /// wrong, and the player is one trader purchase away from losing the rest.
        /// </summary>
        private static readonly HashSet<string> _truncated = new HashSet<string>();

        internal static void SayTruncated(int vanillaRows, int claimed, int granted)
        {
            var key = vanillaRows + ":" + claimed + ":" + granted;
            if (!_truncated.Add(key)) return;

            CorePlugin.Log.LogWarning("Inventory rows: vanilla is already at " + vanillaRows
                + " and it caps the grid at " + VanillaRows.Ceiling + ", so of " + claimed
                + " row(s) claimed only " + granted + " fit. The rows you bought are kept and "
                + "nothing is dropped - the mod's rows are what give way.");
        }

        internal static void LearnBase(int vanillaRows)
        {
            var player = Player.m_localPlayer;

            if (_base == vanillaRows && ReferenceEquals(player, _player)) return;

            _player = player;
            _base = vanillaRows;
            _applied = -1;
            _effective = -1;

            CorePlugin.Log.LogInfo("Inventory rows: vanilla says " + vanillaRows + ".");
        }

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
                _applied = -1;
                _effective = -1;

                // Measured only as a fallback, and it is the wrong number whenever
                // Player.SetInventorySize has run - which on Valheim 1.0 is every spawn, before
                // this ever ticks. LearnBase sets _player as well as _base, so reaching here
                // with a base already known means the prefix did not fire: the patch failed, or
                // this build of the game does not call SetInventorySize at all.
                //
                // Reading the height then is the best guess available, and it is announced as a
                // guess, because if the prefix is not running the eviction fence probably is not
                // either and rows five and six are not safe.
                if (_base < 0)
                {
                    // The pre-widening height when a load has just happened, because
                    // GetHeight() at this moment is the working space and not the inventory.
                    _base = _widened && _preWiden >= 0 ? _preWiden : inventory.GetHeight();

                    CorePlugin.Log.LogWarning("Inventory rows: vanilla height measured as "
                        + _base + " because Player.SetInventorySize never told us. On Valheim "
                        + "1.0 that patch is what keeps granted rows from being emptied onto "
                        + "the ground - treat rows above " + _base + " as unsafe until the "
                        + "errors above are fixed.");
                }
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
            // Not while nothing has claimed yet, and this is the whole reason that guard is
            // worth its lines. A load forces one tick through here before any mod's Update
            // has run, so Claims is momentarily empty and `honest` is the bare vanilla
            // height. Writing that height was harmless - the next tick corrected it. Moving
            // items to fit it is not: it packs the player's things out of rows that are
            // about to be claimed back, and it would do it on every single login. Observed
            // exactly once, as "4 + 0 claimed by 0 mod(s), 6 item(s) moved up" one frame
            // before "4 + 1 claimed by 1 mod(s)".
            //
            // Otherwise only reached when the claim total changed, because of the early
            // return above. That still covers the case this exists for - a grid arriving
            // from disk already held open - since the first real claim lands a frame later.
            // It does mean emptying a stranded row mid-session waits for the next login to
            // shrink the grid; re-measuring every frame would walk the item list every frame
            // for a state that resolves itself anyway.
            var rescued = Claims.Count > 0 && Occupied(inventory) > honest
                ? Compact(inventory, honest)
                : 0;

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

            // The baseline is NOT captured here any more, and that is the point. This used to
            // set _player and _base = inventory.GetHeight(), which quietly made it the winner of
            // a race it should not have been in: it runs before Player.OnSpawned, so LearnBase
            // then found _base and _player already matching and early-returned, and the line
            // that says which number vanilla gave us never printed. The mechanism worked and was
            // unobservable, which is how two earlier failures in this same feature went unnoticed.
            //
            // Measuring was also the fragile half. GetHeight() is whatever the grid happens to be
            // right now; SetInventorySize is handed vanilla's own count before anything touches
            // it. LearnBase owns the baseline now and this only opens working space for the load.
            _applied = -1;
            _widened = true;

            // Deliberately not the slack height. The widening is a working space for the
            // load and never a height anything should be drawn against; the tick that
            // follows writes the real one, and until it does the honest answer is that
            // nothing has grown yet.
            _effective = -1;

            // From a height that actually exists. This read _base + LoadSlack, and _base is
            // -1 until LearnBase runs - which on a character with no "invrows" key never
            // happens at all, because Valheim 1.0's Player.OnSpawned only calls
            // SetInventorySize when that key is already present and otherwise just writes it.
            // Every character made before 1.0 is in exactly that state on its first login, so
            // the grid was being set to -1 + 16 and the fallback below then adopted 15 as the
            // vanilla height. Reported from the live server on 2026-09-10 as "I logged in and
            // I have a 15 row inventory", which is LoadSlack - 1 and not a coincidence.
            _preWiden = inventory.GetHeight();
            var from = _base >= 0 ? _base : _preWiden;

            _height.SetValue(inventory, from + LoadSlack);
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

                var player = Player.m_localPlayer;
                if (player == null) return;

                if (!ReferenceEquals(gui, _seen))
                {
                    _seen = gui;
                    _shown = -1;
                    Capture(gui);
                }

                // Measured off the grid on screen, not off Extra, and that distinction is the
                // bug a player reported on 2026-09-11: a row bought from Haldor drew its slots
                // over open water with no wood behind them.
                //
                // Extra counts only what mods claimed above the vanilla baseline, and that
                // baseline is learned from the `invrows` key - so buying a row moves the
                // baseline up with it and Extra stays 0. The panel was then set to
                // captured + 0 and never grew for a row that was really there.
                //
                // Vanilla does not size the panel itself. Confirmed by buying a row and
                // relogging: the slots are still outside the wood, so this is not a stale
                // capture that a fresh window would correct. Which also means the panel height
                // captured from a fresh window is always the authored one, whatever the
                // character has bought - and that is what makes an absolute baseline safe here.
                //
                // Asking the inventory how tall it is covers every source at once: rows a mod
                // claimed, rows the player bought, and the tick's own clamp up to whatever the
                // items occupy. All three are reasons the wood has to move, and only one of
                // them was being counted.
                var rows = Grown(gui, player);
                if (rows == _shown) return;

                _shown = rows;
                Resize(gui, rows);
            }

            /// <summary>
            /// How many rows taller than the captured panel the grid currently is.
            ///
            /// Measured, not configured, and the measurement is the whole correction. A
            /// constant was tried first - "the art fits four rows" - and it was wrong: the
            /// panel captured on a character who had bought a row came back 358px against a
            /// 70px row, which is five. Vanilla does size its own panel when the window is
            /// built; it just never resizes one that is already open.
            ///
            /// So the baseline is whatever the capture actually covers, and it cannot drift:
            /// Capture only runs on a window Core has not written to yet.
            ///
            /// That makes both cases fall out of one rule. A window built after the purchase
            /// is captured at five rows and needs nothing. A window built before it is
            /// captured at four, and the row bought mid-session puts the grid one past the
            /// capture - which is exactly the reported bug, where the panel stayed four rows
            /// for the rest of the session because Extra never moved off zero.
            /// </summary>
            private static int Grown(InventoryGui gui, Player player)
            {
                var inventory = player.GetInventory();
                if (inventory == null || Heights.Count == 0) return 0;

                var grid = gui.m_player.GetComponentInChildren<InventoryGrid>(true);
                if (grid == null || grid.m_elementSpace <= 0f) return 0;

                // Rounded rather than floored, because the art carries a little padding past
                // its last row - 358px of panel over 70px rows is five rows and change, not
                // five and a spare row's worth.
                var fits = Mathf.RoundToInt(Heights[0] / grid.m_elementSpace);

                return Mathf.Max(0, inventory.GetHeight() - fits);
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

                    // The container window is a CHILD of the player window - the real path is
                    // Inventory_screen/root/Player/Container - so walking the player's children
                    // reaches the chest panel's own Bkg and selected_frame, which wear the same
                    // woodpanel sprite at very nearly the same width. Both filters wave them
                    // through, and growing them at pivot 0.5 put a row of spare wood above and
                    // below every chest you opened.
                    //
                    // Skipped rather than filtered harder, because there is nothing about the
                    // sprite or the size that separates them - only where they sit. The
                    // container is moved by this class, never resized by it.
                    if (_container != null && image.transform.IsChildOf(_container)) continue;

                    Remember(image.rectTransform);
                }
            }

            private static void Remember(RectTransform rect)
            {
                if (rect == null || Panels.Contains(rect)) return;

                Panels.Add(rect);
                Heights.Add(rect.rect.height);

                // Named, with its path, because "found by the sprite it draws, then filtered by
                // width" is a heuristic and the only way to know what it actually caught is to
                // read the list. It has been wrong once already in the other direction, catching
                // the armour and weight tabs cut from the same woodpanel art.
                CorePlugin.Log.LogInfo("Inventory panel: grabbed " + Path(rect)
                    + "  " + rect.rect.width.ToString("F0") + "x"
                    + rect.rect.height.ToString("F0")
                    + "  pivot y " + rect.pivot.y.ToString("F2"));
            }

            private static string Path(Transform t)
            {
                var name = t.name;

                for (var p = t.parent; p != null; p = p.parent)
                {
                    name = p.name + "/" + name;
                    if (p.name == "InventoryGui" || p.parent == null) break;
                }

                return name;
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

                // Says what it measured, not just what it did. This line is what caught the
                // constant that came before it: 358px of captured panel over 70px rows is five
                // rows, not the four the code was asserting.
                if (Panels.Count > 0)
                    CorePlugin.Log.LogInfo("Inventory panel: captured " + Heights[0].ToString("F0")
                        + "px, one row is " + grid.m_elementSpace.ToString("F0")
                        + "px, so the captured art fits about "
                        + Mathf.RoundToInt(Heights[0] / grid.m_elementSpace)
                        + " rows; growing by " + rows + " row(s) to " + (Heights[0] + added).ToString("F0")
                        + "px for a grid " + rows + " row(s) past what the capture covers.");

                // Pushed down by exactly what the inventory gained, from its own captured
                // baseline rather than by nudging it each time, so opening a chest twice does
                // not walk it off the screen.
                if (_container == null) return;

                _container.anchoredPosition = _containerBase + new Vector2(0f, -added);

                // And then clamped, because "exactly what the inventory gained" stops being
                // affordable. Vanilla stacks the container below the player window with a gap,
                // and preserving that gap is right for a row or two; with every row a character
                // can buy plus what a mod claims, the panel grows by enough to put the whole
                // chest window under the taskbar. Seen at eight rows on a 1080-tall screen,
                // where the two windows cannot both fit however the gap is spent.
                //
                // So overlap is chosen over disappearance. A container sitting over the last
                // rows of the inventory is awkward and completely usable; one below the screen
                // is neither. Nothing about this is reachable by pushing less - at that height
                // there is no offset that fits both.
                //
                // Measured off world corners rather than computed from anchors and pivots,
                // which is the same reason Rist's bar reads its own geometry: the arithmetic
                // has to be right about a parent chain nobody here authored, and the corners
                // are already the answer.
                var canvas = _container.GetComponentInParent<Canvas>();
                if (canvas == null) return;

                var canvasRect = canvas.transform as RectTransform;
                if (canvasRect == null || canvas.scaleFactor <= 0f) return;

                var box = new Vector3[4];
                var screen = new Vector3[4];
                _container.GetWorldCorners(box);
                canvasRect.GetWorldCorners(screen);

                // Corner 0 is bottom-left on both, so a positive difference is overhang.
                var below = screen[0].y - box[0].y;
                if (below <= 0f) return;

                _container.anchoredPosition += new Vector2(0f, below / canvas.scaleFactor);

                CorePlugin.Log.LogInfo("Inventory panel: the container window would have hung "
                    + below.ToString("F0") + "px below the screen, so it was lifted back on. "
                    + "It now overlaps the inventory - there is no room for both at this height.");
            }
        }
    }
}
