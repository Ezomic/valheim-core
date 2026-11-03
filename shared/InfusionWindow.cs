using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Ezomic.Shared
{
    /// <summary>
    /// The Infusion Table's own window (LHM-95): two item slots, the basin's liquid, a preview
    /// of what comes out, an Infuse button and a way to empty the basin. It replaces the
    /// workbench's recipe list for this table only, and for every mod that links this file.
    ///
    /// <b>This is the second half of <see cref="InfusionTable"/></b>, the same partial class in
    /// its own file so the window can be read without the table's model loader beside it. It is
    /// optional: a consumer that links only <c>InfusionTable.cs</c> still compiles, still gets
    /// the table and its liquid, and shows the vanilla crafting window. A consumer that links
    /// this file too needs UnityEngine.UI, UnityEngine.UIModule, UnityEngine.TextRenderingModule
    /// and Unity.TextMeshPro referenced, and <c>assembly_guiutils</c> for the tooltip.
    ///
    /// <b>It is vanilla's window, rearranged, not a new one.</b> The crafting panel inside
    /// InventoryGui keeps its frame, its title, its Craft button with the progress bar and the
    /// crafting effects. The recipe list, the tabs and the description are hidden behind a
    /// CanvasGroup (never switched off, because InventoryGui toggles some of them itself and a
    /// state restored from a guess would break the workbench), and the slots, the captions and
    /// the Empty basin button are clones of the pieces vanilla already draws: a requirement
    /// slot, the recipe's heading and body text, and a tab button. No art of its own.
    ///
    /// <b>The craft is still vanilla's.</b> The two slots pick a recipe, <c>SetRecipe</c> selects
    /// it, and the Craft button runs <c>OnCraftPressed</c> and <c>DoCrafting</c> unchanged, so
    /// the cost, the liquid gate and the emptying of the basin are the code the table already
    /// had. Nothing here crafts.
    ///
    /// <b>What is shared and what is not.</b> The slots are one player's own choice and live in
    /// this client only: they hold a prefab name, not an item, and are checked against the bag
    /// every frame. The only shared state is the liquid, on the table's ZDO, and the only thing
    /// this file writes to it is Empty basin, the same claim-then-set the fill and the craft
    /// use. Anyone in range can use the table at once, and the last write wins.
    ///
    /// <b>Hooks.</b> Four patches, applied once however many linked mods carry this file, by
    /// the same rule as the liquid's two: the first copy to tick patches and the rest see its
    /// patch on the method and leave it alone. They are the only code that touches the window,
    /// so that copy's statics are the window's state. A failure inside any of them switches the
    /// window off for the session and leaves vanilla's showing.
    /// </summary>
    public static partial class InfusionTable
    {
        private const string WindowRoot = "infusion_window";
        private const string BasinLabel = "Basin: ";
        private const string EmptyLabel = "Empty basin";
        private const string InfuseLabel = "Infuse";

        private static string _slotBase = "";
        private static string _slotInfusing = "";
        private static WindowView _view;
        private static bool _windowFailed;

        private static readonly List<Recipe> AtTable = new List<Recipe>();
        private static ObjectDB _atTableDb;
        private static int _atTableCount = -1;

        private static FieldInfo _selectedField, _availableField, _multiField;
        private static PropertyInfo _pairRecipe, _pairItem;
        private static MethodInfo _setRecipe;

        // ------------------------------------------------------------------ hooks

        static partial void InstallWindow(Harmony harmony)
        {
            try
            {
                PatchWindow(harmony, typeof(InventoryGui), "UpdateRecipe", nameof(SyncWindow),
                    nameof(PaintWindow));
                PatchWindow(harmony, typeof(InventoryGui), "OnCraftPressed", nameof(GuardCraft),
                    nameof(SingleCraft));
                PatchWindow(harmony, typeof(InventoryGui), "Hide", null, nameof(WindowHidden));
                PatchWindow(harmony, typeof(Humanoid), "UseItem", nameof(PlaceFromBag), null);
            }
            catch (Exception e)
            {
                Prefabs.Log.LogError("Patching the Infusion Table's window failed, so the table "
                    + "keeps the vanilla crafting window: " + e);
            }
        }

        private static void PatchWindow(Harmony harmony, Type type, string method, string prefix,
            string postfix)
        {
            MethodBase original = AccessTools.Method(type, method);
            if (original == null)
            {
                Prefabs.Log.LogError(type.Name + "." + method + " is missing, so the Infusion "
                    + "Table keeps the vanilla crafting window.");
                _windowFailed = true;
                return;
            }

            HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
            if (info != null)
            {
                foreach (HarmonyLib.Patch patch in info.Prefixes)
                    if (patch.owner == HarmonyId) return;
                foreach (HarmonyLib.Patch patch in info.Postfixes)
                    if (patch.owner == HarmonyId) return;
            }

            // First, so a right click at the table reaches the slots before another mod's own
            // use of the same item (Hausi wears a trophy on it).
            HarmonyMethod before = prefix == null ? null
                : new HarmonyMethod(typeof(InfusionTable), prefix) { priority = Priority.First };
            HarmonyMethod after = postfix == null ? null
                : new HarmonyMethod(typeof(InfusionTable), postfix);

            harmony.Patch(original, prefix: before, postfix: after);
        }

        /// <summary>The table the local player has open, or null. Also null once the window has failed.</summary>
        private static CraftingStation OpenTable()
        {
            if (_windowFailed) return null;

            Player player = Player.m_localPlayer;
            if (player == null || !InventoryGui.IsVisible()) return null;

            CraftingStation station = player.GetCurrentCraftingStation();
            if (station == null || station.m_name != DisplayName) return null;

            ZNetView view = station.GetComponentInParent<ZNetView>();
            return view != null && view.IsValid() ? station : null;
        }

        private static void SyncWindow(InventoryGui __instance)
        {
            try
            {
                CraftingStation station = OpenTable();
                if (station == null)
                {
                    if (_view != null) CloseWindow();
                    return;
                }

                if (_view == null || _view.Root == null || _view.Gui != __instance)
                {
                    if (_view != null) CloseWindow();
                    _view = BuildWindow(__instance);
                }

                OpenWindow(_view);
                DropMissing(Player.m_localPlayer);

                Recipe wanted = Chosen(Held(station));
                if (SelectedRecipe(__instance) != wanted) Select(__instance, wanted);
            }
            catch (Exception e)
            {
                FailWindow(e);
            }
        }

        private static void PaintWindow(InventoryGui __instance)
        {
            try
            {
                CraftingStation station = OpenTable();
                if (station == null || _view == null || !_view.Shown) return;

                Paint(__instance, _view, station, Player.m_localPlayer);
            }
            catch (Exception e)
            {
                FailWindow(e);
            }
        }

        private static bool GuardCraft(InventoryGui __instance)
        {
            try
            {
                CraftingStation station = OpenTable();
                if (station == null) return true;

                Recipe wanted = Chosen(Held(station));
                if (wanted == null)
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                        _slotBase == "" || _slotInfusing == ""
                            ? "Place an item in both slots first."
                            : "Those two do not infuse into anything.");
                    return false;
                }

                if (SelectedRecipe(__instance) != wanted) Select(__instance, wanted);
                return SelectedRecipe(__instance) == wanted;
            }
            catch (Exception e)
            {
                FailWindow(e);
                return true;
            }
        }

        /// <summary>Holding the multi-craft key would otherwise ask for five of everything.</summary>
        private static void SingleCraft(InventoryGui __instance)
        {
            if (OpenTable() == null) return;

            if (_multiField == null) _multiField = AccessTools.Field(typeof(InventoryGui), "m_multiCrafting");
            if (_multiField != null) _multiField.SetValue(__instance, false);
        }

        private static void WindowHidden()
        {
            if (_view != null) CloseWindow();
        }

        private static bool PlaceFromBag(Humanoid __instance, Inventory inventory,
            ItemDrop.ItemData item, bool fromInventoryGui)
        {
            try
            {
                if (!fromInventoryGui || item == null || __instance != Player.m_localPlayer
                    || OpenTable() == null) return true;

                Player player = Player.m_localPlayer;
                if (inventory != player.GetInventory()) return true;

                return !PlaceInSlot(item);
            }
            catch (Exception e)
            {
                FailWindow(e);
                return true;
            }
        }

        private static void FailWindow(Exception e)
        {
            if (_windowFailed) return;

            _windowFailed = true;
            Prefabs.Log.LogError("The Infusion Table's window failed and is switched off for this "
                + "session. The table shows the vanilla crafting window: " + e);

            try
            {
                CloseWindow();
            }
            catch (Exception)
            {
                // Restoring the vanilla panel is best effort, and the failure is already logged.
            }
        }

        // ------------------------------------------------------------------ what the slots mean

        private static string PrefabOf(ItemDrop.ItemData item)
        {
            return item != null && item.m_dropPrefab != null ? item.m_dropPrefab.name : null;
        }

        private static string Ingredient(Recipe recipe, int at)
        {
            Piece.Requirement requirement = recipe.m_resources[at];
            return requirement != null && requirement.m_resItem != null
                ? requirement.m_resItem.gameObject.name : "";
        }

        private static List<Recipe> RecipesAtTable()
        {
            ObjectDB db = ObjectDB.instance;
            if (db == null) return AtTable;
            if (db == _atTableDb && db.m_recipes.Count == _atTableCount) return AtTable;

            AtTable.Clear();
            foreach (Recipe recipe in db.m_recipes)
            {
                if (recipe == null || recipe.m_item == null || recipe.m_craftingStation == null
                    || recipe.m_craftingStation.m_name != DisplayName || NeededBy(recipe) == null
                    || recipe.m_resources == null || recipe.m_resources.Length < 2) continue;

                AtTable.Add(recipe);
            }

            _atTableDb = db;
            _atTableCount = db.m_recipes.Count;
            return AtTable;
        }

        private static bool IsIngredient(string name, int at)
        {
            foreach (Recipe recipe in RecipesAtTable())
                if (Ingredient(recipe, at) == name) return true;

            return false;
        }

        /// <summary>
        /// The recipe the two slots make, or null. Two recipes may share both inputs and differ
        /// in the liquid they ask for, so the one that asks for what the basin holds wins, then
        /// one that takes any liquid, then the first, which the status line then explains.
        /// </summary>
        private static Recipe Chosen(string held)
        {
            if (_slotBase == "" || _slotInfusing == "") return null;

            Recipe any = null, first = null;
            foreach (Recipe recipe in RecipesAtTable())
            {
                if (Ingredient(recipe, 0) != _slotBase || Ingredient(recipe, 1) != _slotInfusing)
                    continue;

                if (first == null) first = recipe;

                InfusionLiquid? needed = NeededBy(recipe);
                if (held != "" && needed.ToString() == held) return recipe;
                if (needed == InfusionLiquid.Any && any == null) any = recipe;
            }

            return any != null ? any : first;
        }

        /// <summary>Puts an item into the slot it belongs in. False when it belongs in neither.</summary>
        private static bool PlaceInSlot(ItemDrop.ItemData item)
        {
            string name = PrefabOf(item);
            if (name == null) return false;

            bool asBase = IsIngredient(name, 0), asInfusing = IsIngredient(name, 1);
            if (!asBase && !asInfusing) return false;

            if (asBase && (_slotBase == "" || !asInfusing)) _slotBase = name;
            else _slotInfusing = name;

            return true;
        }

        private static int Carried(Player player, string name, out ItemDrop.ItemData first)
        {
            first = null;
            int count = 0;
            if (name == "") return 0;

            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (PrefabOf(item) != name) continue;

                if (first == null) first = item;
                count += item.m_stack;
            }

            return count;
        }

        private static void DropMissing(Player player)
        {
            ItemDrop.ItemData unused;
            if (Carried(player, _slotBase, out unused) == 0) _slotBase = "";
            if (Carried(player, _slotInfusing, out unused) == 0) _slotInfusing = "";
        }

        private static bool EmptyBasin(Player player, CraftingStation station)
        {
            ZNetView view = station != null ? station.GetComponentInParent<ZNetView>() : null;
            if (view == null || !view.IsValid() || !station.InUseDistance(player)) return false;

            if (Held(station) == "")
            {
                player.Message(MessageHud.MessageType.Center, "The basin is already empty.");
                return false;
            }

            view.ClaimOwnership();
            view.GetZDO().Set(LiquidKey, "");
            player.Message(MessageHud.MessageType.Center, "You tip the basin out.");
            return true;
        }

        private static string Spoken(string held)
        {
            return held == "" ? "empty" : held == "GreydwarfEye" ? "Greydwarf Eye" : held;
        }

        /// <summary>
        /// The window in the words a scenario can assert on: whether it is up, what is in each
        /// slot, what the slots make, what the basin holds and whether the Craft button would
        /// work. Read from the same state the window draws.
        /// </summary>
        public static string WindowState()
        {
            CraftingStation station = OpenTable();
            bool open = station != null && _view != null && _view.Shown;

            string held = station != null ? Held(station) : "";
            Recipe wanted = open ? Chosen(held) : null;
            Player player = Player.m_localPlayer;

            bool craft = wanted != null && player != null && player.HaveRequirements(wanted, false, 1, 1)
                && SelectedRecipe(InventoryGui.instance) == wanted;

            return "window=" + (open ? "open" : "closed")
                + " base=" + (open && _slotBase != "" ? _slotBase : "none")
                + " infusing=" + (open && _slotInfusing != "" ? _slotInfusing : "none")
                + " result=" + (wanted != null ? wanted.m_item.gameObject.name : "none")
                + " liquid=" + (held != "" ? held : "none")
                + " craft=" + (craft ? "yes" : "no");
        }

        // ------------------------------------------------------------------ vanilla's selection

        private static Recipe SelectedRecipe(InventoryGui gui)
        {
            if (_selectedField == null)
                _selectedField = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");
            if (_selectedField == null || gui == null) return null;

            return RecipeOfPair(_selectedField.GetValue(gui));
        }

        private static Recipe RecipeOfPair(object pair)
        {
            if (pair == null) return null;

            if (_pairRecipe == null) _pairRecipe = pair.GetType().GetProperty("Recipe");
            return _pairRecipe != null ? _pairRecipe.GetValue(pair, null) as Recipe : null;
        }

        /// <summary>
        /// Choose a recipe in vanilla's list, or none. A recipe the player does not know yet is
        /// not in the list, and then nothing is selected and the Craft button stays off.
        /// </summary>
        private static void Select(InventoryGui gui, Recipe recipe)
        {
            if (_availableField == null)
                _availableField = AccessTools.Field(typeof(InventoryGui), "m_availableRecipes");
            if (_setRecipe == null) _setRecipe = AccessTools.Method(typeof(InventoryGui), "SetRecipe");
            if (_availableField == null || _setRecipe == null)
                throw new InvalidOperationException("InventoryGui's recipe list has moved.");

            int index = -1;
            IList list = _availableField.GetValue(gui) as IList;
            if (recipe != null && list != null)
            {
                if (_pairItem == null && list.Count > 0)
                    _pairItem = list[0].GetType().GetProperty("ItemData");

                for (int i = 0; i < list.Count; i++)
                {
                    object item = _pairItem != null ? _pairItem.GetValue(list[i], null) : null;
                    if (RecipeOfPair(list[i]) == recipe && item == null) { index = i; break; }
                }
            }

            _setRecipe.Invoke(gui, new object[] { index, false });
        }

        // ------------------------------------------------------------------ the view

        private sealed class SlotView
        {
            internal GameObject Root;
            internal Image Icon;
            internal TMP_Text Name, Amount;
            internal UITooltip Tooltip;
        }

        private sealed class Concealed
        {
            internal CanvasGroup Group;
        }

        private sealed class WindowView
        {
            internal InventoryGui Gui;
            internal RectTransform Root, Frame;
            internal SlotView Base, Infusing, Result;
            internal TMP_Text Name, Body, Basin, Status;
            internal Image Swatch;
            internal Button Empty;
            internal readonly List<Concealed> Hidden = new List<Concealed>();
            internal Vector2 FramePosition, FrameSize;
            internal bool Shown;
            internal Recipe Described;
        }

        private static WindowView BuildWindow(InventoryGui gui)
        {
            RectTransform frame = gui.m_recipeDecription.transform.parent as RectTransform;
            if (frame == null || gui.m_recipeRequirementList.Length == 0)
                throw new InvalidOperationException("InventoryGui's crafting panel has moved.");

            WindowView view = new WindowView { Gui = gui, Frame = frame };

            GameObject root = new GameObject(WindowRoot, typeof(RectTransform));
            root.layer = frame.gameObject.layer;
            view.Root = (RectTransform)root.transform;
            view.Root.SetParent(frame, false);
            view.Root.anchorMin = Vector2.zero;
            view.Root.anchorMax = Vector2.one;
            view.Root.offsetMin = view.Root.offsetMax = Vector2.zero;

            Caption(view, gui, "Base", -112f);
            Caption(view, gui, "Infusing", 0f);
            Caption(view, gui, "Result", 112f);
            Sign(view, gui, "+", -56f);
            Sign(view, gui, "=", 56f);

            view.Base = Slot(view, gui, "infusion_base", -112f, true);
            view.Infusing = Slot(view, gui, "infusion_infusing", 0f, true);
            view.Result = Slot(view, gui, "infusion_result", 112f, false);

            view.Name = Text(view, gui.m_recipeName, "infusion_name", 30f, TextAlignmentOptions.Center);
            Place(view.Name.rectTransform, new Vector2(0f, -165f), new Vector2(500f, 40f));

            view.Body = Text(view, gui.m_recipeDecription, "infusion_body", 18f, TextAlignmentOptions.TopLeft);
            Place(view.Body.rectTransform, new Vector2(0f, -190f), new Vector2(500f, 150f), 1f);

            view.Swatch = Swatch(view, gui);
            Place(view.Swatch.rectTransform, new Vector2(-205f, -372f), new Vector2(36f, 36f));

            view.Basin = Text(view, gui.m_recipeDecription, "infusion_basin", 20f, TextAlignmentOptions.Left);
            Place(view.Basin.rectTransform, new Vector2(-45f, -372f), new Vector2(260f, 36f));

            view.Empty = EmptyButton(view, gui);

            view.Status = Text(view, gui.m_recipeDecription, "infusion_status", 17f, TextAlignmentOptions.Top);
            Place(view.Status.rectTransform, new Vector2(0f, -405f), new Vector2(500f, 56f), 1f);

            return view;
        }

        private static void Place(RectTransform rect, Vector2 position, Vector2 size, float pivotY = 0.5f)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, pivotY);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static TMP_Text Text(WindowView view, TMP_Text source, string name, float size,
            TextAlignmentOptions alignment)
        {
            GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, view.Root, false);
            clone.name = name;
            clone.SetActive(true);

            TMP_Text text = clone.GetComponent<TMP_Text>();
            text.enabled = true;
            text.enableAutoSizing = false;
            text.fontSize = size;
            text.alignment = alignment;
            text.text = "";
            return text;
        }

        private static void Caption(WindowView view, InventoryGui gui, string label, float x)
        {
            TMP_Text text = Text(view, gui.m_recipeName, "infusion_caption_" + label, 20f,
                TextAlignmentOptions.Center);
            text.text = label;
            Place(text.rectTransform, new Vector2(x, -30f), new Vector2(120f, 28f));
        }

        private static void Sign(WindowView view, InventoryGui gui, string mark, float x)
        {
            TMP_Text text = Text(view, gui.m_recipeName, "infusion_sign", 30f, TextAlignmentOptions.Center);
            text.text = mark;
            Place(text.rectTransform, new Vector2(x, -90f), new Vector2(40f, 40f));
        }

        private static SlotView Slot(WindowView view, InventoryGui gui, string name, float x, bool clickable)
        {
            GameObject clone = UnityEngine.Object.Instantiate(gui.m_recipeRequirementList[0], view.Root, false);
            clone.name = name;
            clone.SetActive(true);
            Place((RectTransform)clone.transform, new Vector2(x, -90f), new Vector2(64f, 64f));

            SlotView slot = new SlotView
            {
                Root = clone,
                Icon = clone.transform.Find("res_icon").GetComponent<Image>(),
                Name = clone.transform.Find("res_name").GetComponent<TMP_Text>(),
                Amount = clone.transform.Find("res_amount").GetComponent<TMP_Text>(),
                Tooltip = clone.GetComponent<UITooltip>()
            };

            if (clickable)
            {
                Button button = clone.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.targetGraphic = clone.GetComponent<Image>();
                bool isBase = name == "infusion_base";
                button.onClick.AddListener(() =>
                {
                    if (isBase) _slotBase = ""; else _slotInfusing = "";
                });
            }

            return slot;
        }

        private static Image Swatch(WindowView view, InventoryGui gui)
        {
            GameObject clone = UnityEngine.Object.Instantiate(gui.m_recipeRequirementList[0], view.Root, false);
            clone.name = "infusion_swatch";
            clone.SetActive(true);

            foreach (Transform child in clone.transform) child.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(clone.GetComponent<UITooltip>());
            return clone.GetComponent<Image>();
        }

        /// <summary>A tab button, cloned: it is the one vanilla button that reads as a small action.</summary>
        private static Button EmptyButton(WindowView view, InventoryGui gui)
        {
            GameObject clone = UnityEngine.Object.Instantiate(gui.m_tabCraft.gameObject, view.Root, false);
            clone.name = "infusion_empty";
            clone.SetActive(true);

            // Nothing of the gamepad's: the clone must not register as a stop in vanilla's focus.
            foreach (MonoBehaviour behaviour in clone.GetComponents<MonoBehaviour>())
                if (behaviour.GetType().Name == "UIGamePad") UnityEngine.Object.Destroy(behaviour);

            foreach (Transform child in clone.transform)
                if (child.name != "Text") child.gameObject.SetActive(false);

            clone.GetComponentInChildren<TMP_Text>().text = EmptyLabel;
            Place((RectTransform)clone.transform, new Vector2(190f, -372f), new Vector2(150f, 32f));

            Button button = clone.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => EmptyBasin(Player.m_localPlayer, OpenTable()));
            return button;
        }

        // ------------------------------------------------------------------ showing it

        private static void Conceal(WindowView view, Transform part)
        {
            if (part == null) return;

            CanvasGroup group = part.GetComponent<CanvasGroup>();
            if (group == null) group = part.gameObject.AddComponent<CanvasGroup>();

            view.Hidden.Add(new Concealed { Group = group });
        }

        private static void OpenWindow(WindowView view)
        {
            if (view.Shown) return;

            InventoryGui gui = view.Gui;
            Transform crafting = view.Frame.parent;

            Conceal(view, gui.m_recipeIcon.transform);
            Conceal(view, gui.m_recipeName.transform);
            Conceal(view, gui.m_recipeDecription.transform);
            Conceal(view, gui.m_variantButton != null ? gui.m_variantButton.transform : null);
            Conceal(view, gui.m_itemCraftType != null ? gui.m_itemCraftType.transform : null);
            Conceal(view, gui.m_qualityPanel);
            Conceal(view, gui.m_recipeRequirementList[0].transform.parent);
            Conceal(view, view.Frame.Find("UpgradePanel"));
            Conceal(view, crafting.Find("RecipeList"));
            Conceal(view, crafting.Find("TabsButtons"));
            Conceal(view, crafting.Find("Level"));
            Conceal(view, crafting.Find("RepairButton"));
            Conceal(view, gui.m_repairPanel);

            foreach (Concealed part in view.Hidden)
            {
                part.Group.alpha = 0f;
                part.Group.interactable = false;
                part.Group.blocksRaycasts = false;
            }

            // The recipe list is gone, so the description takes the whole width.
            view.FramePosition = view.Frame.anchoredPosition;
            view.FrameSize = view.Frame.sizeDelta;
            view.Frame.sizeDelta = new Vector2(view.FrameSize.x + 196f, view.FrameSize.y);

            view.Root.gameObject.SetActive(true);
            view.Shown = true;
        }

        /// <summary>Gives vanilla's panel back as it was found, and forgets the slots.</summary>
        private static void CloseWindow()
        {
            _slotBase = _slotInfusing = "";

            WindowView view = _view;
            if (view == null) return;

            _view = null;

            foreach (Concealed part in view.Hidden)
            {
                if (part.Group == null) continue;

                part.Group.alpha = 1f;
                part.Group.interactable = true;
                part.Group.blocksRaycasts = true;
            }

            if (view.Shown && view.Frame != null)
            {
                view.Frame.anchoredPosition = view.FramePosition;
                view.Frame.sizeDelta = view.FrameSize;
            }

            if (view.Root != null) UnityEngine.Object.Destroy(view.Root.gameObject);
        }

        private static void Paint(InventoryGui gui, WindowView view, CraftingStation station, Player player)
        {
            string held = Held(station);
            Recipe wanted = Chosen(held);

            DrawSlot(view.Base, player, _slotBase);
            DrawSlot(view.Infusing, player, _slotInfusing);
            DrawResult(view, wanted);

            InfusionLiquid liquid;
            bool known = Enum.TryParse(held, out liquid);
            view.Basin.text = BasinLabel + Spoken(held);
            view.Swatch.color = held == "" ? DryColour : known ? ColourOf(liquid) : LiquidColour;
            view.Empty.interactable = held != "";

            view.Status.text = StatusLine(wanted, held);

            TMP_Text craft = gui.m_craftButton.GetComponentInChildren<TMP_Text>();
            if (craft != null) craft.text = InfuseLabel;
        }

        private static void DrawSlot(SlotView slot, Player player, string name)
        {
            ItemDrop.ItemData item;
            int count = Carried(player, name, out item);
            bool filled = item != null;

            slot.Icon.gameObject.SetActive(filled);
            slot.Name.gameObject.SetActive(filled);
            slot.Amount.gameObject.SetActive(filled);

            if (!filled)
            {
                slot.Tooltip.m_text = "Right-click an item in your inventory to place it here.";
                return;
            }

            string label = Localization.instance.Localize(item.m_shared.m_name);
            slot.Icon.sprite = item.GetIcon();
            slot.Icon.color = Color.white;
            slot.Name.text = label;
            slot.Amount.text = count.ToString();
            slot.Amount.color = Color.white;
            slot.Tooltip.m_text = label + "\nClick to take it out.";
        }

        private static void DrawResult(WindowView view, Recipe wanted)
        {
            SlotView slot = view.Result;
            bool filled = wanted != null;

            slot.Icon.gameObject.SetActive(filled);
            slot.Name.gameObject.SetActive(false);
            slot.Amount.gameObject.SetActive(filled);

            if (!filled)
            {
                slot.Tooltip.m_text = "What the two slots make.";
                view.Name.text = "";
                view.Body.text = "";
                view.Described = null;
                return;
            }

            ItemDrop.ItemData result = wanted.m_item.m_itemData;
            string label = Localization.instance.Localize(result.m_shared.m_name);

            slot.Icon.sprite = result.GetIcon();
            slot.Icon.color = Color.white;
            slot.Amount.text = wanted.m_amount.ToString();
            slot.Amount.color = Color.white;
            slot.Tooltip.m_text = label;

            view.Name.text = label;
            if (view.Described == wanted) return;

            view.Described = wanted;
            view.Body.text = Localization.instance.Localize(ItemDrop.ItemData.GetTooltip(result, 1,
                true, Game.m_worldLevel, wanted.m_amount));
        }

        private static string StatusLine(Recipe wanted, string held)
        {
            if (_slotBase == "" && _slotInfusing == "")
                return "Right-click items in your inventory to place them.";

            if (_slotBase == "" || _slotInfusing == "")
                return "Place an item in the other slot too.";

            if (wanted == null) return "<color=orange>Those two do not infuse into anything.</color>";

            InfusionLiquid? needed = NeededBy(wanted);
            if (held == "")
                return "<color=orange>The basin is empty. Use Resin, a Greydwarf Eye or Guck on the table to fill it.</color>";

            if (needed != InfusionLiquid.Any && needed.ToString() != held)
                return "<color=orange>This needs " + Spoken(needed.ToString()) + ", and the basin holds "
                    + Spoken(held) + ". Empty it first.</color>";

            return "";
        }
    }
}
