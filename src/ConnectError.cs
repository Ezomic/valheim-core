using HarmonyLib;

namespace Ezomic.Core
{
    /// <summary>
    /// Puts the reason a connection was refused onto the screen that announces it.
    ///
    /// Valheim's refusal screen shows one localised line per ConnectionStatus - "Incompatible
    /// version", "Kicked from server" - and carries no room for detail. Everything specific
    /// therefore ended up in a log, which is fine for whoever runs the server and useless for
    /// the person actually turned away, who may not have access to it and has no reason to
    /// guess that a log is where the answer lives.
    ///
    /// Observed twice in one evening: a build mismatch that reported only "Incompatible
    /// version", and a character refused at the door that reported only "Kicked from server".
    /// Both had a precise, already-computed explanation sitting one machine away.
    ///
    /// So a mod that is about to cause a refusal leaves the reason here, and the next time the
    /// screen is drawn it says so. Consumed on display, because a stale reason attached to an
    /// unrelated later failure would be worse than no reason at all.
    /// </summary>
    internal static class ConnectError
    {
        private static string _pending;

        /// <summary>
        /// Set the explanation the next refusal screen should show. Safe to call from any mod
        /// on the client; the last one set before the screen appears wins.
        /// </summary>
        internal static void Expect(string reason)
        {
            if (!string.IsNullOrEmpty(reason)) _pending = reason;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FejdStartup), "ShowConnectError")]
        private static void Explain(FejdStartup __instance)
        {
            if (string.IsNullOrEmpty(_pending)) return;

            var reason = _pending;
            _pending = null;

            // The label is a TMP_Text, and reaching it by reflection avoids taking a reference
            // on Unity.TextMeshPro purely to assign one string on a screen nobody sees twice.
            // The same caution as the ImageConversionModule note in the repo's working file:
            // an extra assembly reference is a build-time risk paid forever for a one-line win.
            var field = AccessTools.Field(typeof(FejdStartup), "m_connectionFailedError");
            var label = field != null ? field.GetValue(__instance) : null;
            if (label == null) return;

            var text = AccessTools.Property(label.GetType(), "text");
            if (text == null) return;

            var existing = text.GetValue(label, null) as string;

            // Appended rather than replaced. The stock line is what the player recognises and
            // what any search will match; the detail belongs under it, not instead of it.
            text.SetValue(label, string.IsNullOrEmpty(existing) ? reason : existing + "\n\n" + reason, null);
        }
    }
}
