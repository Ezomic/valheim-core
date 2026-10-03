using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Ezomic.Core
{
    /// <summary>
    /// The Settings page of the compendium (LHM-51): one list of the mods that registered a
    /// setting through <see cref="SettingsPanel.Add"/>, and one mod's display options and hotkeys beside it.
    ///
    /// <b>Robbin picked mockup B on 2026-10-02</b> ("mods on the left with a one-line summary, one
    /// mod's settings on the right"), and the mockup is the spec. The colours, sizes and gaps below
    /// are its CSS, in its order, so the two can be read side by side. What differs is named in the
    /// README and the changelog: the mockup draws its own title and a row of tabs, and here those are
    /// the compendium's own title and its list, with this page as the last entry in that list.
    ///
    /// <b>It draws inside the compendium's page, the way Utangard's panel does, and reuses what
    /// that panel learned the hard way.</b> The entry in the list is a plain text page (the
    /// fallback, see <see cref="SettingsModel.TextPage"/>); when it is the one showing, this covers
    /// the text area and blanks the text, so the list, selection, gamepad, close button and Escape
    /// stay vanilla's. Three lessons are carried over unchanged:
    ///
    /// <list type="bullet">
    /// <item>The panel sits in the nearest rect above the text that clips, not the text's own
    /// parent (see <see cref="Host"/>). The parent is sized by the text, so blanking the text
    /// shrinks it and the panel with it.</item>
    /// <item>The panel is as tall as its content with a floor, never stretched to a host (see
    /// <see cref="Fit"/>). A column squeezed below its content hands every TextMeshPro label its
    /// minimum height, which is 0, and every text lands on its neighbour.</item>
    /// <item>Everything is built once, with the panel, as a fixed pool of rows, and a click only
    /// switches rows on and off and rewrites their text. A panel rebuilt on a click is a fresh
    /// layout, which is the moment that squeeze happens.</item>
    /// </list>
    ///
    /// Edges are a whole number of screen pixels (see <see cref="Edge"/>), for the reason given
    /// there: a one unit line at 0.934 pixels to a unit is not drawn at all.
    ///
    /// <b>Cheap.</b> Redrawn when the page opens, on a click, and once a second while it shows.
    /// Every write is skipped when the value has not changed.
    /// </summary>
    internal static class SettingsScreen
    {
        // ------------------------------------------------------------ mockup B, as data ---

        private static readonly Color PanelFace = Rgb(0x090807);
        private static readonly Color PanelEdge = Rgb(0x2e2b24);
        private static readonly Color RailFace = Rgb(0x0d0b09);
        private static readonly Color BodyText = Rgb(0xe8dcc0);
        private static readonly Color Muted = Rgb(0xa89c84);
        private static readonly Color Dim = Rgb(0x968d7b);
        private static readonly Color Gold = Rgb(0xd4a94a);
        private static readonly Color GoldBright = Rgb(0xf0c75e);
        private static readonly Color Amber = Rgb(0xe0a24a);
        private static readonly Color Red = Rgb(0xd27e6e);
        private static readonly Color ControlEdge = Rgb(0x4a3d2e);
        private static readonly Color ControlFace = Rgb(0x1b1812);
        private static readonly Color CaptureFace = Rgb(0x392f1b);
        private static readonly Color ButtonFace = Rgb(0x211e19);
        private static readonly Color ButtonOffText = Rgb(0x6f6859);
        private static readonly Color ButtonOffEdge = Rgb(0x24211c);
        private static readonly Color ButtonOffFace = Rgb(0x141210);
        private static readonly Color RowSelected = Rgb(0x272011);
        private static readonly Color WarnEdge = Rgb(0x6b5230);
        private static readonly Color WarnFace = Rgb(0x1d1710);
        private static readonly Color Divider = Rgb(0x1d1a15);
        private static readonly Color Clear = new Color(0f, 0f, 0f, 0f);

        private const float TitleSize = 20f;
        private const float BodySize = 14f;
        private const float SmallSize = 13f;
        private const float CaptionSize = 12f;

        private const float RailWidth = 236f;
        private const float RowHeight = 34f;
        private const float LabelWidth = 140f;
        private const float CellWidth = 200f;
        private const float ControlHeight = 24f;
        private const float EntryHeight = 41f;

        /// <summary>The mockup's line height for a single line. See <see cref="Wrapped"/> for wrapped text.</summary>
        private const float LineHeight = 1.2f;

        private const float RefreshSeconds = 1f;

        /// <summary>
        /// The most of each thing the page has room built for. Past these the extras are not
        /// drawn, which on the first three is not a state anybody will meet: eleven mods list
        /// settings today and the limit is sixteen.
        /// </summary>
        private const int MostMods = 16;
        private const int MostRows = 10;
        private const int MostBoxes = 3;
        private const int MostUsers = 6;

        private const string Hint = "Esc cancels. Backspace unbinds. A key another mod already uses is "
            + "refused, and the panel says which.";

        // ------------------------------------------------------------------- state -------

        /// <summary>The Settings entry in the list the compendium was last filled with.</summary>
        internal static TextsDialog.TextInfo Page;

        private static TextsDialog _dialog;
        private static GameObject _root;
        private static RectTransform _host;
        private static LayoutElement _floor;
        private static LayoutElement _columnsFloor;
        private static float _fitScale = -1f;
        private static Vector2 _fitSize;
        private static float _fitPixels = -1f;
        private static float _pixelsPerUnit = 1f;
        private static readonly List<RectTransform> Rims = new List<RectTransform>();
        private static readonly List<RectTransform> Hairlines = new List<RectTransform>();
        private static RectTransform _panelFace;
        private static TextsDialog _failedFor;
        private static TMP_Text _proto;
        private static float _naturalPerEm;
        private static Sprite _dot;
        private static float _nextRefresh;

        private static string _selectedGuid;
        private static SettingRow _capturing;
        private static int _captureFrame;
        private static string _refusal;
        private static SettingRow _refusedRow;
        private static int _consumedFrame = -1;

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly List<Section> Sections = new List<Section>();
        private static readonly List<Conflict> Conflicts = new List<Conflict>();
        private static GameObject _footer;
        private static TMP_Text _title;
        private static TMP_Text _note;
        private static TMP_Text _empty;

        /// <summary>True while a key is being waited for, which is what holds the compendium open.</summary>
        internal static bool Capturing { get { return _capturing != null; } }

        /// <summary>
        /// Whether the keys the capture owns are still to be hidden from vanilla this frame:
        /// while it waits, and for the rest of the frame in which it ended. The ticker that
        /// ends a capture and InventoryGui.Update run in an order Unity does not promise, and
        /// when the ticker goes first the press that ended the capture would otherwise reach
        /// the window, which closes on Escape, Tab and E.
        /// </summary>
        internal static bool KeysHeld
        {
            get { return IsShowing && (_capturing != null || _consumedFrame == Time.frameCount); }
        }

        private class Frame
        {
            public GameObject Go;
            public Image Edge;
            public Image FaceImage;
        }

        private sealed class Control : Frame
        {
            public TMP_Text Label;
            public LayoutElement Size;
        }

        private sealed class Entry
        {
            public GameObject Go;
            public Image Background;
            public GameObject Bar;
            public TMP_Text Name;
            public GameObject DotHolder;
            public TMP_Text Count;
            public TMP_Text Summary;
            public string Guid;
        }

        private sealed class RowWidgets
        {
            public GameObject Go;
            public TMP_Text Label;
            public Control Minus;
            public Control Box;
            public Control Plus;
            public TMP_Text Default;
            public Control Reset;
            public TMP_Text Hint;
            public SettingRow Bound;
        }

        private sealed class Section
        {
            public GameObject Go;
            public TMP_Text Heading;
            public SettingsGroup Group;
            public readonly List<RowWidgets> Rows = new List<RowWidgets>();
        }

        private sealed class UserLine
        {
            public GameObject Go;
            public TMP_Text Mod;
            public TMP_Text Label;
            public TMP_Text When;
        }

        private sealed class Conflict
        {
            public GameObject Go;
            public TMP_Text Text;
            public readonly List<UserLine> Lines = new List<UserLine>();
        }

        // ---------------------------------------------------------------- the seams ------

        /// <summary>
        /// The Settings entry in the compendium's list, added last so it follows Utangard's and
        /// the vanilla pages. Wrapped, since this runs inside the game's own list build: a throw
        /// here would leave vanilla's list half made, which reads as "the compendium is broken".
        /// </summary>
        [HarmonyPatch(typeof(TextsDialog), "UpdateTextsList")]
        internal static class AddPage
        {
            private static AccessTools.FieldRef<TextsDialog, List<TextsDialog.TextInfo>> _textsOf;
            private static bool _bound;

            [HarmonyPostfix]
            private static void Postfix(TextsDialog __instance)
            {
                Page = null;

                try
                {
                    if (!_bound)
                    {
                        _bound = true;
                        _textsOf = AccessTools.FieldRefAccess<TextsDialog, List<TextsDialog.TextInfo>>("m_texts");
                    }

                    List<TextsDialog.TextInfo> texts = _textsOf == null ? null : _textsOf(__instance);
                    if (texts == null) return;

                    var page = new TextsDialog.TextInfo("Settings", SettingsModel.TextPage());
                    texts.Add(page);
                    Page = page;
                }
                catch (Exception e)
                {
                    CorePlugin.Log.LogError("Settings page failed to build: " + e);
                }
            }
        }

        /// <summary>
        /// TextsDialog.ShowText(TextInfo): every way a page gets shown ends here, so one postfix
        /// sees each switch to and away from this page. Private and overloaded with ShowText(int),
        /// so it is found by shape.
        /// </summary>
        [HarmonyPatch]
        internal static class Show
        {
            [HarmonyTargetMethod]
            private static MethodBase Target()
            {
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(TextsDialog)))
                {
                    if (method.Name != "ShowText") continue;

                    ParameterInfo[] args = method.GetParameters();
                    if (args.Length == 1 && args[0].ParameterType == typeof(TextsDialog.TextInfo))
                        return method;
                }

                CorePlugin.Log.LogError("TextsDialog has no ShowText(TextInfo) any more - the Settings "
                    + "page in the compendium stays the plain text version.");
                return null;
            }

            [HarmonyPostfix]
            private static void Postfix(TextsDialog __instance, TextsDialog.TextInfo __0)
            {
                Shown(__instance, __0);
            }
        }

        /// <summary>
        /// While a key is being waited for, the inventory window's own Update must not see the
        /// presses: Escape would close the compendium, and Tab or E would close the whole window,
        /// all of them keys somebody may want to bind. They are hidden from it for that one call
        /// and from nothing else, so the capture's own reads, which run from the panel's ticker,
        /// are unaffected. The flag is cleared by a finalizer, since a throw in Update must not
        /// leave every key in the game reading as not pressed.
        /// </summary>
        [HarmonyPatch(typeof(InventoryGui), "Update")]
        internal static class HideKeysFromWindow
        {
            internal static bool Active;

            [HarmonyPrefix]
            private static void Prefix()
            {
                // Only while the page is on screen. A capture left open by closing the window
                // must not go on hiding Tab from the window that opens it.
                Active = KeysHeld;
            }

            [HarmonyFinalizer]
            private static void Finalizer()
            {
                Active = false;
            }
        }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKeyDown), typeof(KeyCode), typeof(bool))]
        internal static class HideKeyDown
        {
            [HarmonyPostfix]
            private static void Postfix(ref bool __result)
            {
                if (HideKeysFromWindow.Active) __result = false;
            }
        }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown), typeof(string))]
        internal static class HideButtonDown
        {
            [HarmonyPostfix]
            private static void Postfix(string name, ref bool __result)
            {
                if (HideKeysFromWindow.Active) __result = false;

                // The console opens from Console.Update, outside the inventory window, on the
                // button of this name. It is a key somebody may want to bind, so it is hidden
                // for as long as the capture holds the keys, wherever it is read from.
                else if (name == "Console" && KeysHeld) __result = false;
            }
        }

        private static void Shown(TextsDialog dialog, TextsDialog.TextInfo text)
        {
            bool ours = Page != null && ReferenceEquals(text, Page);
            if (!ours)
            {
                Cancel();
                if (_root != null) _root.SetActive(false);
                return;
            }

            if (dialog == null || ReferenceEquals(dialog, _failedFor)) return;

            try
            {
                if (_root == null || !ReferenceEquals(_dialog, dialog)) Build(dialog);

                Cancel();
                _root.transform.SetAsLastSibling();
                _root.SetActive(true);
                Fit();
                Draw();

                // Only once the panel has drawn. Anything above that throws leaves the text
                // page showing, which is the fallback.
                dialog.m_textArea.text = "";
                _nextRefresh = Time.unscaledTime + RefreshSeconds;
            }
            catch (Exception e)
            {
                Abandon(dialog, e);
            }
        }

        /// <summary>The page, as a console probe sees it. See <see cref="SettingsProbe"/>.</summary>
        internal static bool IsShowing { get { return _root != null && _root.activeInHierarchy; } }

        internal static string SelectedGuid { get { return _selectedGuid; } }

        internal static void Select(string guid)
        {
            _selectedGuid = guid;
            Cancel();
            Refresh();
        }

        /// <summary>Called every frame by the ticker on the panel, so only while the page is on screen.</summary>
        internal static void Tick()
        {
            if (_root == null || !_root.activeInHierarchy)
            {
                Cancel();
                return;
            }

            try
            {
                if (_capturing != null && Time.frameCount != _captureFrame) Listen();

                if (Time.unscaledTime < _nextRefresh) return;
                _nextRefresh = Time.unscaledTime + RefreshSeconds;

                Fit();
                Draw();
            }
            catch (Exception e)
            {
                Abandon(_dialog, e);
            }
        }

        private static void Refresh()
        {
            if (_root == null || !_root.activeInHierarchy) return;

            try
            {
                Draw();
            }
            catch (Exception e)
            {
                Abandon(_dialog, e);
            }
        }

        private static void Abandon(TextsDialog dialog, Exception e)
        {
            _failedFor = dialog;
            Cancel();

            CorePlugin.Log.LogError("The Settings panel failed, so the page is back to plain text for "
                + "this world. Nothing else is affected. " + e);

            try
            {
                if (_root != null) Object.Destroy(_root);
                _root = null;

                if (dialog != null && dialog.m_textArea != null && Page != null)
                    dialog.m_textArea.text = Localization.instance != null
                        ? Localization.instance.Localize(Page.m_text)
                        : Page.m_text;
            }
            catch (Exception again)
            {
                CorePlugin.Log.LogError("Could not put the Settings text page back either: " + again);
            }
        }

        // ------------------------------------------------------------------ capture ------

        private static void Begin(SettingRow row)
        {
            _capturing = row;
            _captureFrame = Time.frameCount;
            _refusal = null;
            Refresh();
        }

        internal static void Cancel()
        {
            _refusedRow = null;
            if (_capturing == null) return;

            _consumedFrame = Time.frameCount;
            _capturing = null;
            _refusal = null;
        }

        private static KeyCode[] _bindable;

        /// <summary>
        /// Every key and mouse button a bind can be, which stops short of the gamepad: Joystick
        /// codes are not something a keyboard hotkey can hold. Mouse0 is the click that started
        /// the capture, so it is never an answer.
        /// </summary>
        private static KeyCode[] Bindable()
        {
            if (_bindable != null) return _bindable;

            var keys = new List<KeyCode>();
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            {
                if (key == KeyCode.None || key == KeyCode.Mouse0) continue;
                if (key >= KeyCode.JoystickButton0) continue;
                if (!keys.Contains(key)) keys.Add(key);
            }

            _bindable = keys.ToArray();
            return _bindable;
        }

        /// <summary>The first bindable key pressed this frame, or None. Read through ZInput.</summary>
        internal static KeyCode PressedNow()
        {
            foreach (KeyCode key in Bindable())
                if (ZInput.GetKeyDown(key, false)) return key;

            return KeyCode.None;
        }

        private static void Listen()
        {
            // B and Y are how a gamepad leaves a window, and a gamepad has no Escape to press.
            if (ZInput.GetButtonDown("JoyButtonB") || ZInput.GetButtonDown("JoyButtonY"))
            {
                Cancel();
                Refresh();
                return;
            }

            KeyCode key = PressedNow();
            if (key == KeyCode.None) return;

            if (key == KeyCode.Escape)
            {
                Cancel();
                Refresh();
                return;
            }

            Bind(key == KeyCode.Backspace ? KeyCode.None : key);
        }

        /// <summary>Bind the captured row to a key. A refusal keeps the capture open and says why.</summary>
        internal static string Bind(KeyCode key)
        {
            SettingRow row = _capturing;
            if (row == null) return "no capture is open";

            string refusal = SettingsModel.TryBind(SettingsModel.Pages(), row, key);
            if (refusal != null)
            {
                _refusal = refusal;
                Refresh();
                return refusal;
            }

            Cancel();
            Refresh();
            return null;
        }

        internal static void StartCapture(SettingRow row)
        {
            Begin(row);
        }

        internal static void EndCapture()
        {
            Cancel();
            Refresh();
        }

        // ------------------------------------------------------------------ clicks -------

        private static void OnEntry(Entry entry)
        {
            _selectedGuid = entry.Guid;
            Cancel();
            Refresh();
        }

        private static void OnBox(RowWidgets w)
        {
            SettingRow row = w.Bound;
            if (row == null) return;

            if (row.IsKey)
            {
                if (ReferenceEquals(_capturing, row)) Cancel();
                else Begin(row);

                Refresh();
                return;
            }

            Cancel();

            if (row.IsBool) SettingsModel.Toggle(row);
            else if (row.IsEnum) SettingsModel.Cycle(row, 1);

            Refresh();
        }

        private static void OnStep(RowWidgets w, int step)
        {
            if (w.Bound == null || !w.Bound.IsInt) return;

            Cancel();
            SettingsModel.Nudge(w.Bound, step);
            Refresh();
        }

        private static void OnReset(RowWidgets w)
        {
            SettingRow row = w.Bound;
            if (row == null || SettingsModel.IsDefault(row)) return;

            Cancel();
            _refusal = SettingsModel.Reset(SettingsModel.Pages(), row);
            _refusedRow = _refusal != null ? row : null;
            Refresh();
        }

        // ----------------------------------------------------------------- drawing -------

        private static void Draw()
        {
            List<ModPage> pages = SettingsModel.Pages();
            ModPage page = Chosen(pages);

            DrawRail(pages, page);

            SetText(_title, page == null ? "SETTINGS" : page.Name.ToUpperInvariant());
            SetActive(_note.gameObject, page != null);
            SetActive(_empty.gameObject, page == null);

            foreach (Section section in Sections)
                DrawSection(section, page);

            DrawConflicts(pages, page);
        }

        /// <summary>
        /// The page being shown: the chosen mod, else the first. A mod that has gone, which a
        /// host taking its rows over would do, hands the page to the first rather than leaving
        /// it on nothing.
        /// </summary>
        private static ModPage Chosen(List<ModPage> pages)
        {
            if (pages.Count == 0) return null;

            foreach (ModPage page in pages)
                if (page.Guid == _selectedGuid) return page;

            _selectedGuid = pages[0].Guid;
            return pages[0];
        }

        private static void DrawRail(List<ModPage> pages, ModPage chosen)
        {
            bool anyShared = false;

            for (int i = 0; i < Entries.Count; i++)
            {
                Entry entry = Entries[i];
                bool shown = i < pages.Count;
                SetActive(entry.Go, shown);
                if (!shown) continue;

                ModPage page = pages[i];
                bool selected = ReferenceEquals(page, chosen);
                anyShared |= page.SharesKey;
                entry.Guid = page.Guid;

                SetColor(entry.Background, selected ? RowSelected : Clear);
                SetActive(entry.Bar, selected);
                SetText(entry.Name, page.Name);
                SetColor(entry.Name, selected ? GoldBright : BodyText);
                SetActive(entry.DotHolder, page.SharesKey);
                SetText(entry.Count, page.Rows.Count.ToString(CultureInfo.InvariantCulture));
                SetText(entry.Summary, SettingsModel.SummaryOf(page));
            }

            SetActive(_footer, anyShared);
        }

        private static void DrawSection(Section section, ModPage page)
        {
            var rows = new List<SettingRow>();
            if (page != null)
                foreach (SettingRow row in page.Rows)
                    if (row.Group == section.Group) rows.Add(row);

            SetActive(section.Go, rows.Count > 0);
            if (rows.Count == 0) return;

            SetText(section.Heading, section.Group == SettingsGroup.Display ? "DISPLAY" : "HOTKEYS");

            for (int i = 0; i < section.Rows.Count; i++)
            {
                RowWidgets w = section.Rows[i];
                bool shown = i < rows.Count;
                SetActive(w.Go, shown);

                if (shown) DrawRow(w, rows[i]);
                else w.Bound = null;
            }
        }

        private static void DrawRow(RowWidgets w, SettingRow row)
        {
            w.Bound = row;
            bool capturing = ReferenceEquals(_capturing, row);

            SetText(w.Label, row.Label);

            string value = SettingsModel.ValueText(row);
            float width = row.IsBool ? 70f : row.IsInt ? 60f : 190f;

            if (capturing) value = "Press a key...";

            SetText(w.Box.Label, value);
            SetColor(w.Box.Label, capturing ? GoldBright : BodyText);
            SetColor(w.Box.Edge, capturing ? Gold : ControlEdge);
            SetColor(w.Box.FaceImage, capturing ? CaptureFace : ControlFace);
            SetWidth(w.Box, width);

            SetActive(w.Minus.Go, row.IsInt);
            SetActive(w.Plus.Go, row.IsInt);
            if (row.IsInt)
            {
                Dress(w.Minus, SettingsModel.CanNudge(row, -1));
                Dress(w.Plus, SettingsModel.CanNudge(row, 1));
            }

            SetText(w.Default, "Default: " + SettingsModel.DefaultText(row));
            Dress(w.Reset, !SettingsModel.IsDefault(row));

            bool refusedReset = !capturing && ReferenceEquals(_refusedRow, row) && _refusal != null;
            SetActive(w.Hint.gameObject, capturing || refusedReset);
            if (capturing)
            {
                SetText(w.Hint, Wrapped(_refusal != null ? _refusal + " Press another key, or Esc to cancel." : Hint, 1.23f));
                SetColor(w.Hint, _refusal != null ? Red : Muted);
            }
            else if (refusedReset)
            {
                SetText(w.Hint, Wrapped(_refusal + " Reset refused.", 1.23f));
                SetColor(w.Hint, Red);
            }
        }

        /// <summary>A small button in its live look, or its greyed one when it would do nothing.</summary>
        private static void Dress(Control button, bool live)
        {
            SetColor(button.Label, live ? BodyText : ButtonOffText);
            SetColor(button.Edge, live ? PanelEdge : ButtonOffEdge);
            SetColor(button.FaceImage, live ? ButtonFace : ButtonOffFace);
        }

        private static void DrawConflicts(List<ModPage> pages, ModPage page)
        {
            var keys = new List<KeyCode>();
            if (page != null)
            {
                foreach (SettingRow row in page.Rows)
                {
                    if (!row.IsKey) continue;

                    KeyCode key = SettingsModel.KeyOf(row.Entry);
                    if (key != KeyCode.None && !keys.Contains(key) && SettingsModel.UsersOf(pages, row).Count > 1)
                        keys.Add(key);
                }
            }

            for (int i = 0; i < Conflicts.Count; i++)
            {
                Conflict box = Conflicts[i];
                bool shown = i < keys.Count;
                SetActive(box.Go, shown);
                if (!shown) continue;

                DrawConflict(box, pages, page, keys[i]);
            }
        }

        private static void DrawConflict(Conflict box, List<ModPage> pages, ModPage page, KeyCode key)
        {
            var users = new List<SettingsModel.User>();
            foreach (ModPage other in pages)
                foreach (SettingRow row in other.Rows)
                    if (row.IsKey && SettingsModel.KeyOf(row.Entry) == key)
                        users.Add(new SettingsModel.User { Page = other, Row = row });

            var mods = new List<ModPage>();
            bool allSay = true;
            foreach (SettingsModel.User user in users)
            {
                if (!mods.Contains(user.Page)) mods.Add(user.Page);
                allSay &= user.Row.When != null;
            }

            string name = SettingsModel.KeyName(key);
            string title = mods.Count == users.Count
                ? name + " is shared by " + SettingsModel.Count(users.Count) + " mods."
                : name + " is used by " + SettingsModel.Count(users.Count) + " features.";

            string body = allSay
                ? "They act in different situations, so none of them clash today. Rebind two of them "
                  + "to keys you hold together and both would fire at once."
                : "At least one of them does not say when it acts, so they may fire together. Rebind "
                  + "one of them to a key nothing else uses.";

            SetText(box.Text, Wrapped("<size=14><b><color=#e0a24a>" + title + "</color></b></size> " + body, 1.31f, true));

            for (int i = 0; i < box.Lines.Count; i++)
            {
                UserLine line = box.Lines[i];
                bool shown = i < users.Count;
                SetActive(line.Go, shown);
                if (!shown) continue;

                SettingsModel.User user = users[i];
                SetText(line.Mod, Plain(user.Page.Name));
                SetColor(line.Mod, ReferenceEquals(user.Page, page) ? GoldBright : BodyText);
                SetText(line.Label, Plain(user.Row.Label));
                SetText(line.When, Plain(user.Row.When ?? ""));
            }
        }

        private static string Plain(string text)
        {
            return "<noparse>" + text + "</noparse>";
        }

        /// <summary>
        /// A paragraph that may wrap, at the mockup's line height. Names and labels in it are not
        /// markup, so they go in a noparse unless the caller has built tags of its own.
        /// </summary>
        private static string Wrapped(string text, float lineHeight, bool markup = false)
        {
            return "<line-height=" + lineHeight.ToString("0.##", CultureInfo.InvariantCulture) + "em>"
                + (markup ? text : "<noparse>" + text + "</noparse>");
        }

        // Each of these writes only on a change: a TextMeshPro text or a RectTransform written
        // with its own value still dirties the layout, and this runs every second.

        private static void SetText(TMP_Text label, string text)
        {
            if (label.text != text) label.text = text;
        }

        private static void SetColor(Graphic graphic, Color color)
        {
            if (graphic.color != color) graphic.color = color;
        }

        private static void SetColor(TMP_Text label, Color color)
        {
            if (label.color != color) label.color = color;
        }

        private static void SetActive(GameObject go, bool active)
        {
            if (go.activeSelf != active) go.SetActive(active);
        }

        private static void SetWidth(Control control, float width)
        {
            if (Mathf.Approximately(control.Size.preferredWidth, width)) return;

            control.Size.minWidth = width;
            control.Size.preferredWidth = width;
        }

        private static float Edge(float width)
        {
            float pixels = Mathf.Max(1f, Mathf.Round(width * _pixelsPerUnit));
            return pixels / _pixelsPerUnit;
        }

        private static void Rim(RectTransform face)
        {
            Rims.Add(face);
            Inset(face, Edge(1f));
        }

        // ---------------------------------------------------------------- building -------

        /// <summary>
        /// Builds the panel over the text area of this compendium. Everything is made under an
        /// inactive root so nothing cloned runs its Awake until it has been stripped, and the labels
        /// are clones of the compendium's own text, which is how they wear the game's font without
        /// this mod naming it.
        /// </summary>
        private static void Build(TextsDialog dialog)
        {
            Forget();

            TMP_Text donor = dialog.m_textArea;
            if (donor == null) throw new InvalidOperationException("TextsDialog.m_textArea is not set.");

            string passed;
            RectTransform host = Host(dialog, donor, out passed);

            var root = new GameObject("Core_Settings", typeof(RectTransform));
            root.SetActive(false);

            var rootRect = (RectTransform)root.transform;
            rootRect.SetParent(host, false);

            _floor = root.AddComponent<LayoutElement>();
            _floor.ignoreLayout = true;

            _root = root;
            _dialog = dialog;
            _host = host;

            // The panel: a 1 px edge in #2e2b24 round a #090807 face. Raycast on, so a click on
            // the panel does not fall through to whatever is under it.
            Paint(rootRect, PanelEdge, true);
            RectTransform face = Child("Face", rootRect);
            face.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            _panelFace = face;
            Inset(face, Edge(1f));
            Paint(face, PanelFace, false);

            // As tall as what it holds and never squeezed: see Fit.
            VerticalLayoutGroup frame = root.AddComponent<VerticalLayoutGroup>();
            frame.padding = new RectOffset(1, 1, 1, 1);
            Stack(frame, false);
            root.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            GameObject proto = Object.Instantiate(donor.gameObject, rootRect);
            proto.name = "Core_Label";
            Strip(proto);
            proto.SetActive(false);

            _proto = proto.GetComponent<TMP_Text>();
            if (_proto == null) throw new InvalidOperationException("The compendium's text carried no TextMeshPro component.");
            Prime(_proto);

            // Measured with the root switched on, since an inactive label has not loaded its font.
            root.SetActive(true);
            _naturalPerEm = Measure(rootRect);

            root.AddComponent<SettingsScreenTicker>();

            RectTransform body = Child("Body", rootRect);
            body.gameObject.AddComponent<LayoutElement>().flexibleHeight = 0f;
            VerticalLayoutGroup column = body.gameObject.AddComponent<VerticalLayoutGroup>();
            column.padding = new RectOffset(14, 14, 14, 14);
            Stack(column, false);

            _fitScale = -1f;
            Fit();

            // The rail and the page, 12 apart, both as tall as the taller so their faces meet.
            RectTransform columns = Child("Columns", body);
            _columnsFloor = columns.gameObject.AddComponent<LayoutElement>();
            HorizontalLayoutGroup split = columns.gameObject.AddComponent<HorizontalLayoutGroup>();
            split.spacing = 12f;
            Row(split, true);

            int sounded = 0;
            BuildRail(columns, dialog, ref sounded);
            BuildPage(columns, dialog, ref sounded);

            _fitScale = -1f;
            Fit();

            var inv = CultureInfo.InvariantCulture;
            Rect area = host.rect;
            CorePlugin.Log.LogInfo("Settings panel built over '" + host.name + "'"
                + (passed.Length > 0 ? " (past " + passed + ", sized by what is in it)" : "") + ", "
                + Mathf.RoundToInt(area.width) + " x " + Mathf.RoundToInt(area.height)
                + " at scale " + _fitScale.ToString("0.###", inv)
                + ", " + _pixelsPerUnit.ToString("0.###", inv) + " screen pixels to a unit"
                + ", the compendium's own text at size " + donor.fontSize.ToString("0.#", inv)
                + ", one line of text at " + _naturalPerEm.ToString("0.00", inv) + " em, "
                + sounded + " buttons with vanilla's click sound.");
        }

        private static void BuildRail(RectTransform columns, TextsDialog dialog, ref int sounded)
        {
            Frame rail = MakeFrame("Rail", columns, PanelEdge, RailFace, false);
            LayoutElement size = rail.Go.AddComponent<LayoutElement>();
            size.minWidth = RailWidth;
            size.preferredWidth = RailWidth;
            size.flexibleWidth = 0f;

            VerticalLayoutGroup stack = rail.Go.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(1, 1, 7, 7);
            Stack(stack, false);

            RectTransform rail0 = (RectTransform)rail.Go.transform;

            // MODS, 12 bold with a pixel of tracking, 6 / 12 / 3 of padding.
            RectTransform headWrap = Child("Heading", rail0);
            VerticalLayoutGroup headPad = headWrap.gameObject.AddComponent<VerticalLayoutGroup>();
            headPad.padding = new RectOffset(12, 12, 6, 3);
            Stack(headPad, false);
            TMP_Text heading = Label(headWrap, CaptionSize, Dim, TextAlignmentOptions.TopLeft);
            heading.text = "MODS";
            heading.fontStyle = FontStyles.Bold;
            heading.characterSpacing = 8f;

            for (int i = 0; i < MostMods; i++)
            {
                Entry entry = MakeEntry(rail0);
                if (Sound(entry.Go, dialog)) sounded++;
                Entries.Add(entry);
            }

            RectTransform foot = Child("Legend", rail0);
            VerticalLayoutGroup footPad = foot.gameObject.AddComponent<VerticalLayoutGroup>();
            footPad.padding = new RectOffset(12, 12, 6, 6);
            Stack(footPad, false);
            _footer = foot.gameObject;

            RectTransform key = Child("Key", foot);
            HorizontalLayoutGroup line = key.gameObject.AddComponent<HorizontalLayoutGroup>();
            line.spacing = 6f;
            Row(line, false);
            line.childAlignment = TextAnchor.MiddleLeft;
            Dot(key, Amber);
            Label(key, CaptionSize, Dim, TextAlignmentOptions.TopLeft).text = "Shares a key with another mod";
        }

        /// <summary>
        /// A mod in the list, 41 high: a 3 wide bar that is gold when selected, the name in bold with
        /// an amber dot after it when one of its keys is shared and the count at the right, and
        /// the one-line summary under that.
        /// </summary>
        private static Entry MakeEntry(RectTransform rail)
        {
            RectTransform rect = Child("Mod", rail);
            var entry = new Entry { Go = rect.gameObject };
            entry.Background = Paint(rect, Clear, true);

            LayoutElement size = rect.gameObject.AddComponent<LayoutElement>();
            size.minHeight = EntryHeight;
            size.preferredHeight = EntryHeight;

            VerticalLayoutGroup stack = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(13, 12, 3, 3);
            Stack(stack, false);

            RectTransform bar = Child("Bar", rect);
            bar.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            bar.anchorMin = new Vector2(0f, 0f);
            bar.anchorMax = new Vector2(0f, 1f);
            bar.pivot = new Vector2(0f, 0.5f);
            bar.sizeDelta = new Vector2(3f, 0f);
            bar.anchoredPosition = Vector2.zero;
            Paint(bar, Gold, false);
            entry.Bar = bar.gameObject;

            RectTransform line = Child("Line", rect);
            HorizontalLayoutGroup who = line.gameObject.AddComponent<HorizontalLayoutGroup>();
            who.spacing = 6f;
            Row(who, false);
            who.childAlignment = TextAnchor.MiddleLeft;

            entry.Name = Label(line, BodySize, BodyText, TextAlignmentOptions.TopLeft);
            entry.Name.fontStyle = FontStyles.Bold;
            entry.Name.textWrappingMode = TextWrappingModes.NoWrap;

            RectTransform dot = Dot(line, Amber);
            entry.DotHolder = dot.gameObject;

            RectTransform fill = Child("Spacer", line);
            LayoutElement grow = fill.gameObject.AddComponent<LayoutElement>();
            grow.minWidth = 0f;
            grow.flexibleWidth = 1f;

            entry.Count = Label(line, CaptionSize, Dim, TextAlignmentOptions.TopRight);
            entry.Count.textWrappingMode = TextWrappingModes.NoWrap;

            entry.Summary = Label(rect, CaptionSize, Dim, TextAlignmentOptions.TopLeft);
            entry.Summary.textWrappingMode = TextWrappingModes.NoWrap;
            entry.Summary.overflowMode = TextOverflowModes.Ellipsis;
            LayoutElement clip = entry.Summary.gameObject.AddComponent<LayoutElement>();
            clip.minWidth = 0f;
            clip.preferredWidth = 0f;
            clip.flexibleWidth = 1f;

            Button button = rect.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = entry.Background;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() => OnEntry(entry));

            return entry;
        }

        private static void BuildPage(RectTransform columns, TextsDialog dialog, ref int sounded)
        {
            Frame main = MakeFrame("Page", columns, PanelEdge, PanelFace, false);
            LayoutElement size = main.Go.AddComponent<LayoutElement>();
            size.minWidth = 0f;
            size.preferredWidth = 0f;
            size.flexibleWidth = 1f;

            VerticalLayoutGroup stack = main.Go.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(15, 15, 11, 11);
            Stack(stack, false);
            RectTransform page = (RectTransform)main.Go.transform;

            // The mod's name, with the one promise the page makes at its right on the same baseline.
            RectTransform head = Child("Head", page);
            HorizontalLayoutGroup who = head.gameObject.AddComponent<HorizontalLayoutGroup>();
            who.spacing = 8f;
            Row(who, false);
            who.childAlignment = TextAnchor.LowerLeft;

            _title = Label(head, TitleSize, Gold, TextAlignmentOptions.TopLeft);
            _title.fontStyle = FontStyles.Bold;
            _title.characterSpacing = 4f;
            _title.textWrappingMode = TextWrappingModes.NoWrap;
            _title.overflowMode = TextOverflowModes.Ellipsis;
            LayoutElement nameGrow = _title.gameObject.AddComponent<LayoutElement>();
            nameGrow.minWidth = 0f;
            nameGrow.flexibleWidth = 1f;

            _note = Label(head, SmallSize, Muted, TextAlignmentOptions.TopRight);
            _note.text = "Applied at once. Yours alone.";
            _note.textWrappingMode = TextWrappingModes.NoWrap;

            _empty = Label(page, BodySize, Muted, TextAlignmentOptions.TopLeft);
            _empty.text = Wrapped("No mod has listed a setting here. Each mod that has one adds it when it is installed.", 1.3f);

            foreach (SettingsGroup group in new[] { SettingsGroup.Display, SettingsGroup.Hotkeys })
            {
                Section section = MakeSection(page, group, dialog, ref sounded);
                Sections.Add(section);
            }

            for (int i = 0; i < MostBoxes; i++)
                Conflicts.Add(MakeConflict(page));
        }

        /// <summary>
        /// A group heading and its rows. The heading is 13 bold in gold with a pixel of tracking, 14
        /// above it and 2 over a hairline, and 4 under that, as the mockup's .sec.
        /// </summary>
        private static Section MakeSection(RectTransform page, SettingsGroup group, TextsDialog dialog, ref int sounded)
        {
            RectTransform rect = Child(group.ToString(), page);
            Stack(rect.gameObject.AddComponent<VerticalLayoutGroup>(), false);

            var section = new Section { Go = rect.gameObject, Group = group };

            Gap(rect, 14f);
            section.Heading = Label(rect, SmallSize, Gold, TextAlignmentOptions.TopLeft);
            section.Heading.fontStyle = FontStyles.Bold;
            section.Heading.characterSpacing = 8f;
            Gap(rect, 2f);
            Hairline(rect, PanelEdge);
            Gap(rect, 4f);

            for (int i = 0; i < MostRows; i++)
            {
                RowWidgets row = MakeRow(rect, dialog, ref sounded);
                section.Rows.Add(row);
            }

            return section;
        }

        /// <summary>
        /// A setting: 34 high, the name in 140, the control in 200, the default beside it and a
        /// Reset at the right. Made once for every kind of setting and shown as the one it is: a
        /// switch is a 70 wide box, a choice or a key a 190 wide one, and a number the same box
        /// between a minus and a plus.
        /// </summary>
        private static RowWidgets MakeRow(RectTransform section, TextsDialog dialog, ref int sounded)
        {
            RectTransform rect = Child("Row", section);
            Stack(rect.gameObject.AddComponent<VerticalLayoutGroup>(), false);

            var w = new RowWidgets { Go = rect.gameObject };

            RectTransform line = Child("Line", rect);
            LayoutElement height = line.gameObject.AddComponent<LayoutElement>();
            height.minHeight = RowHeight;
            height.preferredHeight = RowHeight;
            HorizontalLayoutGroup across = line.gameObject.AddComponent<HorizontalLayoutGroup>();
            Row(across, false);
            across.childAlignment = TextAnchor.MiddleLeft;

            w.Label = Label(line, BodySize, BodyText, TextAlignmentOptions.TopLeft);
            w.Label.textWrappingMode = TextWrappingModes.NoWrap;
            w.Label.overflowMode = TextOverflowModes.Ellipsis;
            LayoutElement labelSize = w.Label.gameObject.AddComponent<LayoutElement>();
            labelSize.minWidth = LabelWidth;
            labelSize.preferredWidth = LabelWidth;
            labelSize.flexibleWidth = 0f;

            RectTransform cell = Child("Control", line);
            LayoutElement cellSize = cell.gameObject.AddComponent<LayoutElement>();
            cellSize.minWidth = CellWidth;
            cellSize.preferredWidth = CellWidth;
            cellSize.flexibleWidth = 0f;
            HorizontalLayoutGroup controls = cell.gameObject.AddComponent<HorizontalLayoutGroup>();
            controls.spacing = 4f;
            Row(controls, false);
            controls.childAlignment = TextAnchor.MiddleLeft;

            w.Minus = MakeButton(cell, "-", 24f, ControlEdge, ControlFace);
            w.Box = MakeButton(cell, "", 70f, ControlEdge, ControlFace);
            w.Box.Label.alignment = TextAlignmentOptions.MidlineLeft;
            w.Plus = MakeButton(cell, "+", 24f, ControlEdge, ControlFace);

            w.Default = Label(line, CaptionSize, Dim, TextAlignmentOptions.TopLeft);
            w.Default.textWrappingMode = TextWrappingModes.NoWrap;
            w.Default.overflowMode = TextOverflowModes.Ellipsis;
            w.Default.margin = new Vector4(10f, w.Default.margin.y, 0f, w.Default.margin.w);
            LayoutElement defaultSize = w.Default.gameObject.AddComponent<LayoutElement>();
            defaultSize.minWidth = 0f;
            defaultSize.preferredWidth = 0f;
            defaultSize.flexibleWidth = 1f;

            w.Reset = MakeButton(line, "Reset", 0f, PanelEdge, ButtonFace);

            // Under the row, from the label's left edge: what to press, or why that key was refused.
            w.Hint = Label(rect, SmallSize, Muted, TextAlignmentOptions.TopLeft);
            w.Hint.margin = new Vector4(LabelWidth, w.Hint.margin.y, 0f, w.Hint.margin.w);

            AddClick(w.Box, dialog, ref sounded, () => OnBox(w));
            AddClick(w.Minus, dialog, ref sounded, () => OnStep(w, -1));
            AddClick(w.Plus, dialog, ref sounded, () => OnStep(w, 1));
            AddClick(w.Reset, dialog, ref sounded, () => OnReset(w));

            return w;
        }

        private static void AddClick(Control control, TextsDialog dialog, ref int sounded, UnityEngine.Events.UnityAction action)
        {
            Button button = control.Go.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = control.Edge;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(action);

            if (Sound(control.Go, dialog)) sounded++;
        }

        /// <summary>
        /// A box with a one line label in it, 24 high: the mockup's .btn when the width is 0 and the
        /// label sets it (padding 9), .val when it is given. Its look is set by Dress and by the
        /// row that owns it.
        /// </summary>
        private static Control MakeButton(RectTransform parent, string text, float width, Color edge, Color face)
        {
            Frame frame = MakeFrame("Button", parent, edge, face, true);
            var control = new Control { Go = frame.Go, Edge = frame.Edge, FaceImage = frame.FaceImage };

            control.Size = frame.Go.AddComponent<LayoutElement>();
            control.Size.minHeight = ControlHeight;
            control.Size.preferredHeight = ControlHeight;
            if (width > 0f)
            {
                control.Size.minWidth = width;
                control.Size.preferredWidth = width;
            }

            VerticalLayoutGroup inner = frame.Go.AddComponent<VerticalLayoutGroup>();
            inner.padding = new RectOffset(1, 1, 1, 1);
            Stack(inner, true);

            control.Label = Label(frame.Go.transform, width > 0f && text.Length == 0 ? BodySize : SmallSize,
                BodyText, width > 0f && text.Length == 0 ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.Center);
            control.Label.fontStyle = FontStyles.Bold;
            control.Label.text = text;
            control.Label.textWrappingMode = TextWrappingModes.NoWrap;
            control.Label.overflowMode = TextOverflowModes.Ellipsis;
            float lead = control.Label.margin.y;
            float side = width > 0f && text.Length > 0 ? 2f : 9f;
            control.Label.margin = new Vector4(side, lead, side, lead);

            return control;
        }

        /// <summary>
        /// The box under a shared key: an amber edge, the heading and its explanation as one
        /// paragraph, and a table of who uses the key, when each acts.
        /// </summary>
        private static Conflict MakeConflict(RectTransform page)
        {
            RectTransform holder = Child("Shared", page);
            Stack(holder.gameObject.AddComponent<VerticalLayoutGroup>(), false);

            var conflict = new Conflict { Go = holder.gameObject };

            Gap(holder, 12f);

            Frame box = MakeFrame("Box", holder, WarnEdge, WarnFace, false);
            VerticalLayoutGroup stack = box.Go.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(11, 11, 9, 9);
            Stack(stack, false);
            RectTransform rect = (RectTransform)box.Go.transform;

            conflict.Text = Label(rect, SmallSize, BodyText, TextAlignmentOptions.TopLeft);

            Gap(rect, 6f);
            Hairline(rect, PanelEdge);

            for (int i = 0; i < MostUsers; i++)
                conflict.Lines.Add(MakeUserLine(rect));

            return conflict;
        }

        private static UserLine MakeUserLine(RectTransform box)
        {
            RectTransform rect = Child("User", box);
            LayoutElement height = rect.gameObject.AddComponent<LayoutElement>();
            height.minHeight = 25f;
            height.preferredHeight = 25f;
            HorizontalLayoutGroup across = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            Row(across, false);
            across.childAlignment = TextAnchor.MiddleLeft;

            var line = new UserLine { Go = rect.gameObject };

            line.Mod = Cell(rect, 90f, BodyText, true);
            line.Label = Cell(rect, 130f, BodyText, false);
            line.When = Cell(rect, 0f, Muted, false);

            // The rule under the row, a pixel of it drawn over the row's own bottom edge.
            RectTransform rule = Child("Rule", rect);
            rule.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            rule.anchorMin = new Vector2(0f, 0f);
            rule.anchorMax = new Vector2(1f, 0f);
            rule.pivot = new Vector2(0.5f, 0f);
            rule.anchoredPosition = Vector2.zero;
            rule.sizeDelta = new Vector2(0f, Edge(1f));
            Paint(rule, Divider, false);
            Hairlines.Add(rule);

            return line;
        }

        private static TMP_Text Cell(RectTransform row, float width, Color color, bool bold)
        {
            TMP_Text label = Label(row, SmallSize, color, TextAlignmentOptions.TopLeft);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            if (bold) label.fontStyle = FontStyles.Bold;

            LayoutElement size = label.gameObject.AddComponent<LayoutElement>();
            size.minWidth = 0f;
            size.preferredWidth = width;
            size.flexibleWidth = width > 0f ? 0f : 1f;
            if (width > 0f) size.minWidth = width;

            return label;
        }

        /// <summary>An edge colour with a face inset by it, children laid out over the face.</summary>
        private static Frame MakeFrame(string name, Transform parent, Color edge, Color face, bool raycast)
        {
            RectTransform rect = Child(name, parent);
            var frame = new Frame { Go = rect.gameObject };
            frame.Edge = Paint(rect, edge, raycast);

            RectTransform inner = Child("Face", rect);
            inner.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            Rim(inner);
            frame.FaceImage = Paint(inner, face, false);

            return frame;
        }

        /// <summary>A rule one screen pixel thick, which Fit redraws whenever the pixel size changes.</summary>
        private static void Hairline(RectTransform parent, Color color)
        {
            RectTransform rect = Child("Rule", parent);
            LayoutElement size = rect.gameObject.AddComponent<LayoutElement>();
            size.minHeight = Edge(1f);
            size.preferredHeight = Edge(1f);
            Paint(rect, color, false);
            Hairlines.Add(rect);
        }

        /// <summary>A round dot, 8 across, which tints to whatever colour it is given.</summary>
        private static RectTransform Dot(RectTransform parent, Color color)
        {
            RectTransform rect = Child("Dot", parent);
            Image image = Paint(rect, color, false);
            image.sprite = DotSprite();

            LayoutElement size = rect.gameObject.AddComponent<LayoutElement>();
            size.minWidth = 8f;
            size.preferredWidth = 8f;
            size.minHeight = 8f;
            size.preferredHeight = 8f;
            return rect;
        }

        private static Sprite DotSprite()
        {
            if (_dot != null) return _dot;

            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.hideFlags = HideFlags.HideAndDontSave;

            float radius = size / 2f;
            var middle = new Vector2(radius, radius);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), middle);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(radius - distance)));
                }
            }

            texture.Apply(false, false);

            _dot = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            _dot.hideFlags = HideFlags.HideAndDontSave;
            return _dot;
        }

        // ------------------------------------------------------- placement and fitting ---

        /// <summary>
        /// Where the panel goes: the nearest rect above the compendium's text whose height is its
        /// own rather than the text's. The text's own parent is not it: in 1.0 that is 'Content',
        /// sized by the text, so the moment the panel blanked the text it shrank and took the
        /// panel with it. This climbs past every rect that follows what is in it, and stops at the
        /// first that clips, since that is the area the text is seen in. The hierarchy is asset
        /// data no decompile shows, so the rects it passed are named on the build log line.
        /// </summary>
        private static RectTransform Host(TextsDialog dialog, TMP_Text donor, out string passed)
        {
            var host = donor.transform.parent as RectTransform;
            if (host == null) throw new InvalidOperationException("The compendium's text has no parent to draw over.");

            passed = "";
            while (!Clips(host) && FollowsContent(host))
            {
                var above = host.parent as RectTransform;
                if (above == null || above == dialog.transform || !above.IsChildOf(dialog.transform)) break;
                if (Brings(host, above, dialog.m_textAreaTopic) || Brings(host, above, dialog.m_rightScrollbar)
                    || Brings(host, above, dialog.m_listRoot)) break;

                passed += (passed.Length > 0 ? ", '" : "'") + host.name + "'";
                host = above;
            }

            return host;
        }

        private static bool FollowsContent(RectTransform rect)
        {
            foreach (ContentSizeFitter fitter in rect.GetComponents<ContentSizeFitter>())
                if (fitter.enabled && fitter.verticalFit != ContentSizeFitter.FitMode.Unconstrained) return true;

            foreach (ScrollRect scroll in rect.GetComponentsInParent<ScrollRect>(true))
                if (scroll.content == rect) return true;

            Transform parent = rect.parent;
            if (parent == null) return false;

            foreach (HorizontalOrVerticalLayoutGroup group in parent.GetComponents<HorizontalOrVerticalLayoutGroup>())
                if (group.enabled && group.childControlHeight) return true;

            return false;
        }

        private static bool Clips(RectTransform rect)
        {
            RectMask2D rectMask = rect.GetComponent<RectMask2D>();
            if (rectMask != null && rectMask.enabled) return true;

            Mask mask = rect.GetComponent<Mask>();
            return mask != null && mask.enabled;
        }

        private static bool Brings(Transform from, Transform to, Component part)
        {
            return part != null && part.transform.IsChildOf(to) && !part.transform.IsChildOf(from);
        }

        /// <summary>
        /// Keeps the panel over the area the compendium's text would fill: as wide as it, from its
        /// top, and at least as tall as it. The height is the panel's own: a ContentSizeFitter on
        /// the root makes it as tall as what it holds, and this only sets the floor under that,
        /// so the dark page still fills the text area when there is little to say and a long page
        /// is never squeezed. The columns carry the floor less the frame's padding so the rail and
        /// the page reach the bottom together.
        ///
        /// Below a host scale of 1 the compendium sits under a parent that shrinks it, and the
        /// panel is drawn at 1/scale with its width and floor shrunk by the same factor, so its
        /// text comes out at the mockup's pixel sizes. Above 1 it is left alone.
        ///
        /// It also measures how many screen pixels one unit covers and redraws every edge to
        /// whole pixels of it: see <see cref="Edge"/>.
        /// </summary>
        private static void Fit()
        {
            if (_root == null || _host == null || _floor == null) return;

            float scale = HostScale(_host);
            Vector2 size = _host.rect.size;
            float pixels = PixelsPerUnit(_host);
            if (Mathf.Approximately(scale, _fitScale) && size == _fitSize
                && Mathf.Approximately(pixels, _fitPixels)) return;

            _fitScale = scale;
            _fitSize = size;
            _fitPixels = pixels;

            float drawn = scale >= 0.99f ? 1f : scale;
            float grow = 1f / drawn;

            var rect = (RectTransform)_root.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.localScale = new Vector3(grow, grow, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, size.x * drawn);

            _floor.minHeight = Mathf.Max(0f, size.y * drawn);
            if (_columnsFloor != null) _columnsFloor.minHeight = Mathf.Max(0f, _floor.minHeight - 30f);

            _pixelsPerUnit = pixels * grow;
            foreach (RectTransform rim in Rims) Inset(rim, Edge(1f));
            if (_panelFace != null) Inset(_panelFace, Edge(1f));

            foreach (RectTransform line in Hairlines)
            {
                LayoutElement element = line.GetComponent<LayoutElement>();
                if (element != null && !element.ignoreLayout)
                {
                    element.minHeight = Edge(1f);
                    element.preferredHeight = Edge(1f);
                }
                else
                {
                    line.sizeDelta = new Vector2(line.sizeDelta.x, Edge(1f));
                }
            }
        }

        private static float PixelsPerUnit(RectTransform rect)
        {
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            if (canvas == null) return 1f;

            Canvas top = canvas.rootCanvas;
            float canvasScale = top.transform.lossyScale.x;
            if (canvasScale <= 0f || top.scaleFactor <= 0f) return 1f;

            float perUnit = top.scaleFactor * rect.lossyScale.x / canvasScale;
            return perUnit > 0.1f && perUnit < 20f ? perUnit : 1f;
        }

        private static float HostScale(RectTransform host)
        {
            Canvas canvas = host.GetComponentInParent<Canvas>();
            if (canvas == null) return 1f;

            float canvasScale = canvas.rootCanvas.transform.lossyScale.x;
            if (canvasScale <= 0f) return 1f;

            float scale = host.lossyScale.x / canvasScale;
            return scale > 0.2f && scale < 5f ? scale : 1f;
        }

        /// <summary>
        /// The click and hover sounds of the compendium's own list, copied off its row prefab so
        /// they are whatever vanilla plays there. A method of its own inside a try, since ButtonSfx
        /// lives in assembly_guiutils: a rename costs the sound and never the panel.
        /// </summary>
        private static bool Sound(GameObject button, TextsDialog dialog)
        {
            try
            {
                return CopySound(button, dialog);
            }
            catch (Exception e)
            {
                CorePlugin.Log.LogWarning("Settings panel: the buttons stay silent. " + e.Message);
                return false;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool CopySound(GameObject button, TextsDialog dialog)
        {
            if (dialog.m_elementPrefab == null) return false;

            ButtonSfx donor = dialog.m_elementPrefab.GetComponentInChildren<ButtonSfx>(true);
            if (donor == null) return false;

            // Added after the Button, because ButtonSfx looks for it once, in Awake.
            ButtonSfx sfx = button.GetComponent<ButtonSfx>();
            if (sfx == null) sfx = button.AddComponent<ButtonSfx>();
            sfx.m_sfxPrefab = donor.m_sfxPrefab;
            sfx.m_sfxPrefabVibrationOnly = donor.m_sfxPrefabVibrationOnly;
            sfx.m_selectSfxPrefab = donor.m_selectSfxPrefab;
            sfx.m_selectSfxPrefabVibrationOnly = donor.m_selectSfxPrefabVibrationOnly;
            sfx.m_enterSfxPrefab = donor.m_enterSfxPrefab;
            sfx.m_enterSfxPrefabVibrationOnly = donor.m_enterSfxPrefabVibrationOnly;
            return true;
        }

        // ------------------------------------------------------------------ helpers ------

        /// <summary>
        /// Everything off the clone except the text itself, so a copy of the compendium's text does
        /// not bring whatever sizes and localises the original. DestroyImmediate, since the clone is
        /// used in the same frame, and twice over because a component another requires cannot go first.
        /// </summary>
        private static void Strip(GameObject go)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (Component component in go.GetComponents<Component>())
                {
                    if (component == null) continue;
                    if (component is RectTransform || component is CanvasRenderer || component is TMP_Text) continue;

                    Object.DestroyImmediate(component);
                }
            }

            for (int i = go.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(go.transform.GetChild(i).gameObject);
        }

        private static void Prime(TMP_Text label)
        {
            label.text = "";
            label.enableAutoSizing = false;
            label.richText = true;
            label.raycastTarget = false;
            label.overrideColorTags = false;
            label.enableVertexGradient = false;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Overflow;
            label.margin = Vector4.zero;
            label.lineSpacing = 0f;
            label.paragraphSpacing = 0f;
            label.characterSpacing = 0f;
            label.fontStyle = FontStyles.Normal;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.color = BodyText;
        }

        /// <summary>One line of the compendium's font at size 1, from a throwaway label.</summary>
        private static float Measure(RectTransform parent)
        {
            GameObject probe = Object.Instantiate(_proto.gameObject, parent);
            try
            {
                probe.SetActive(true);
                TMP_Text text = probe.GetComponent<TMP_Text>();
                text.fontSize = 100f;
                text.textWrappingMode = TextWrappingModes.NoWrap;

                float perEm = text.GetPreferredValues("Hg").y / 100f;
                if (perEm > 0.6f && perEm < 2.5f) return perEm;

                CorePlugin.Log.LogWarning("Settings panel: measured a line of " + perEm
                    + " em, which cannot be right; spacing the text as if it were 1.2.");
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }

            return 1.2f;
        }

        /// <summary>Half the difference between the mockup's line and the font's own, at this size.</summary>
        private static float Lead(float size)
        {
            return Mathf.Max(0f, (LineHeight - _naturalPerEm) * size * 0.5f);
        }

        private static TMP_Text Label(Transform parent, float size, Color color, TextAlignmentOptions align)
        {
            GameObject go = Object.Instantiate(_proto.gameObject, parent);
            go.name = "Text";
            go.SetActive(true);

            TMP_Text label = go.GetComponent<TMP_Text>();
            label.fontSize = size;
            label.color = color;
            label.alignment = align;

            float lead = Lead(size);
            label.margin = new Vector4(0f, lead, 0f, lead);
            return label;
        }

        private static RectTransform Child(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static Image Paint(RectTransform rect, Color color, bool raycast)
        {
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = raycast;
            return image;
        }

        private static void Inset(RectTransform rect, float by)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(by, by);
            rect.offsetMax = new Vector2(-by, -by);
        }

        private static GameObject Gap(Transform parent, float height)
        {
            RectTransform rect = Child("Gap", parent);
            LayoutElement space = rect.gameObject.AddComponent<LayoutElement>();
            space.minHeight = height;
            space.preferredHeight = height;
            return rect.gameObject;
        }

        private static void Stack(VerticalLayoutGroup group, bool fillHeight)
        {
            group.spacing = 0f;
            group.childAlignment = TextAnchor.UpperLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = fillHeight;
        }

        private static void Row(HorizontalLayoutGroup group, bool even)
        {
            group.childAlignment = TextAnchor.UpperLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = even;
            group.childForceExpandHeight = even;
        }

        /// <summary>Drop every reference into a previous compendium, which a new world has destroyed.</summary>
        private static void Forget()
        {
            if (_root != null) Object.Destroy(_root);

            _root = null;
            _dialog = null;
            _host = null;
            _floor = null;
            _columnsFloor = null;
            _fitScale = -1f;
            _fitPixels = -1f;
            _panelFace = null;
            _proto = null;
            _footer = null;
            _title = null;
            _note = null;
            _empty = null;
            _capturing = null;
            Rims.Clear();
            Hairlines.Clear();
            Entries.Clear();
            Sections.Clear();
            Conflicts.Clear();
        }

        private static Color Rgb(int hex)
        {
            return new Color(((hex >> 16) & 0xff) / 255f, ((hex >> 8) & 0xff) / 255f, (hex & 0xff) / 255f, 1f);
        }
    }

    /// <summary>
    /// Keeps the page's numbers fresh and listens for a key while it is on screen. A component on
    /// the panel itself, so it runs exactly while the page is showing.
    /// </summary>
    internal sealed class SettingsScreenTicker : MonoBehaviour
    {
        private void Update()
        {
            SettingsScreen.Tick();
        }
    }
}
