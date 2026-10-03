using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Ezomic.Core
{
    /// <summary>
    /// `coresettings` in the console: the settings screen's state as lines a Devkit scenario can
    /// read, and the handful of things a click does as words it can say.
    ///
    /// Every verb goes through <see cref="SettingsModel"/> and <see cref="SettingsScreen"/>, the
    /// code a click on the panel reaches, so a scenario that passes has exercised the real
    /// capture, refusal and reset and not a copy of them. What it cannot do is look at the panel:
    /// that a row is drawn, or that a button is where the mockup puts it, is a person's to check.
    ///
    /// isCheat false. Nothing it writes is a game value: it edits the same config entries the
    /// panel does.
    /// </summary>
    internal static class SettingsProbe
    {
        private static bool _registered;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        internal static class Hook
        {
            private static void Postfix()
            {
                if (_registered) return;
                _registered = true;

                new Terminal.ConsoleCommand("coresettings",
                    "- the settings screen: list | page | pick <Mod> | show <Mod> <Label> | shared <Mod> "
                    + "| capture <Mod> <Label> | press <Key> | cancel | set <Mod> <Label> <Key> | reset <Mod> <Label>",
                    OnCommand, isCheat: false);
            }
        }

        private static void OnCommand(Terminal.ConsoleEventArgs args)
        {
            Terminal term = args.Context;
            if (term == null) return;

            string verb = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            List<ModPage> pages = SettingsModel.Pages();

            switch (verb)
            {
                case "list": List(term, pages); return;
                case "page": Page(term, pages); return;
                case "pick": Pick(term, pages, args); return;
                case "show": Show(term, pages, args); return;
                case "shared": Shared(term, pages, args); return;
                case "capture": Capture(term, pages, args); return;
                case "press": Press(term, args); return;
                case "cancel": Cancel(term); return;
                case "set": Set(term, pages, args); return;
                case "reset": Reset(term, pages, args); return;
            }

            term.AddString("coresettings: unknown verb " + verb);
        }

        private static void List(Terminal term, List<ModPage> pages)
        {
            int rows = 0;
            int shared = 0;
            foreach (ModPage page in pages)
            {
                rows += page.Rows.Count;
                if (page.SharesKey) shared++;
            }

            term.AddString("coresettings mods=" + pages.Count + " rows=" + rows + " sharing=" + shared);
            foreach (ModPage page in pages)
                term.AddString("coresettings mod=" + Word(page.Name) + " rows=" + page.Rows.Count
                    + " shares=" + (page.SharesKey ? "yes" : "no") + " summary=" + Word(SettingsModel.SummaryOf(page)));
        }

        private static void Page(Terminal term, List<ModPage> pages)
        {
            string selected = "none";
            foreach (ModPage page in pages)
                if (page.Guid == SettingsScreen.SelectedGuid) selected = Word(page.Name);

            term.AddString("coresettings showing=" + (SettingsScreen.IsShowing ? "yes" : "no")
                + " selected=" + selected + " capturing=" + (SettingsScreen.Capturing ? "yes" : "no"));
        }

        private static void Pick(Terminal term, List<ModPage> pages, Terminal.ConsoleEventArgs args)
        {
            ModPage page = PageNamed(pages, args.Length > 2 ? args[2] : "");
            if (page == null) { term.AddString("coresettings pick: no such mod"); return; }

            SettingsScreen.Select(page.Guid);
            term.AddString("coresettings picked=" + Word(page.Name));
        }

        private static void Show(Terminal term, List<ModPage> pages, Terminal.ConsoleEventArgs args)
        {
            SettingRow row = RowNamed(pages, args, 2, args.Length);
            if (row == null) { term.AddString("coresettings show: no such row"); return; }

            term.AddString("coresettings row=" + Word(row.Label)
                + " value=" + Word(SettingsModel.ValueText(row))
                + " default=" + Word(SettingsModel.DefaultText(row))
                + " isdefault=" + (SettingsModel.IsDefault(row) ? "yes" : "no")
                + " when=" + Word(row.When ?? "none"));
        }

        private static void Shared(Terminal term, List<ModPage> pages, Terminal.ConsoleEventArgs args)
        {
            ModPage page = PageNamed(pages, args.Length > 2 ? args[2] : "");
            if (page == null) { term.AddString("coresettings shared: no such mod"); return; }

            int boxes = 0;
            var done = new List<KeyCode>();
            foreach (SettingRow row in page.Rows)
            {
                KeyCode key = row.IsKey ? SettingsModel.KeyOf(row.Entry) : KeyCode.None;
                if (key == KeyCode.None || done.Contains(key)) continue;

                List<SettingsModel.User> users = SettingsModel.UsersOf(pages, row);
                if (users.Count < 2) continue;

                done.Add(key);
                boxes++;

                var who = new List<string>();
                foreach (SettingsModel.User user in users) who.Add(user.Page.Name);
                term.AddString("coresettings box key=" + Word(SettingsModel.KeyName(key))
                    + " users=" + users.Count + " mods=" + string.Join("+", who.ToArray()));
            }

            term.AddString("coresettings shared=" + Word(page.Name) + " boxes=" + boxes);
        }

        private static void Capture(Terminal term, List<ModPage> pages, Terminal.ConsoleEventArgs args)
        {
            SettingRow row = RowNamed(pages, args, 2, args.Length);
            if (row == null || !row.IsKey) { term.AddString("coresettings capture: no such key row"); return; }

            SettingsScreen.StartCapture(row);
            term.AddString("coresettings capturing=" + Word(row.Label));
        }

        /// <summary>The answer a captured row gets to a key, without anyone pressing one.</summary>
        private static void Press(Terminal term, Terminal.ConsoleEventArgs args)
        {
            KeyCode key;
            if (!TryKey(args.Length > 2 ? args[2] : "", out key)) { term.AddString("coresettings press: no such key"); return; }

            string refusal = SettingsScreen.Bind(key);
            term.AddString(refusal == null ? "coresettings bound=yes" : "coresettings bound=no refusal=" + Word(refusal));
        }

        private static void Cancel(Terminal term)
        {
            SettingsScreen.EndCapture();
            term.AddString("coresettings capturing=" + (SettingsScreen.Capturing ? "yes" : "no"));
        }

        private static void Set(Terminal term, List<ModPage> pages, Terminal.ConsoleEventArgs args)
        {
            KeyCode key;
            if (args.Length < 5 || !TryKey(args[args.Length - 1], out key))
            {
                term.AddString("coresettings set: set <Mod> <Label> <Key>");
                return;
            }

            SettingRow row = RowNamed(pages, args, 2, args.Length - 1);
            if (row == null || !row.IsKey) { term.AddString("coresettings set: no such key row"); return; }

            string refusal = SettingsModel.TryBind(pages, row, key);
            term.AddString(refusal == null
                ? "coresettings bound=yes value=" + Word(SettingsModel.ValueText(row))
                : "coresettings bound=no refusal=" + Word(refusal));
        }

        private static void Reset(Terminal term, List<ModPage> pages, Terminal.ConsoleEventArgs args)
        {
            SettingRow row = RowNamed(pages, args, 2, args.Length);
            if (row == null) { term.AddString("coresettings reset: no such row"); return; }

            SettingsModel.Reset(row);
            term.AddString("coresettings reset=" + Word(row.Label) + " value=" + Word(SettingsModel.ValueText(row)));
        }

        private static ModPage PageNamed(List<ModPage> pages, string name)
        {
            foreach (ModPage page in pages)
                if (string.Equals(page.Name, name, System.StringComparison.OrdinalIgnoreCase)) return page;

            return null;
        }

        /// <summary>The row whose mod is args[from] and whose label is the words after it up to end.</summary>
        private static SettingRow RowNamed(List<ModPage> pages, Terminal.ConsoleEventArgs args, int from, int end)
        {
            if (end - from < 2) return null;

            ModPage page = PageNamed(pages, args[from]);
            if (page == null) return null;

            var words = new List<string>();
            for (int i = from + 1; i < end; i++) words.Add(args[i]);
            string label = string.Join(" ", words.ToArray());

            foreach (SettingRow row in page.Rows)
                if (string.Equals(row.Label, label, System.StringComparison.OrdinalIgnoreCase)) return row;

            return null;
        }

        private static bool TryKey(string text, out KeyCode key)
        {
            key = KeyCode.None;
            if (string.IsNullOrEmpty(text)) return false;

            try
            {
                key = (KeyCode)System.Enum.Parse(typeof(KeyCode), text.Replace("_", ""), true);
                return true;
            }
            catch (System.ArgumentException)
            {
                return false;
            }
        }

        /// <summary>One token for a scenario to match: spaces become underscores.</summary>
        private static string Word(string text)
        {
            return string.IsNullOrEmpty(text) ? "none" : text.Replace(' ', '_');
        }
    }
}
