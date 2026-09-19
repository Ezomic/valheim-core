using System;
using HarmonyLib;

namespace Ezomic.Core
{
    /// <summary>
    /// A line for the chat window, sent by the server.
    ///
    /// A server cannot put a line in anyone's chat window with vanilla alone. The chat RPC
    /// carries a platform id, and the client draws the name of the CONNECTED player holding
    /// that id, which a server never is - so every line a server sends that way is dropped
    /// with an error on every client (Crier's Announce.cs has the decompiled detail). The
    /// window does have an overload that takes a plain title, and it has no network path at
    /// all; it has to be called on the client. Every player has Core, and a gate that keeps
    /// every player on the same Core, so Core is where the client half lives.
    ///
    /// Crier on the server is what sends it: chat written on the Longhouse site, which used to
    /// land in the top-left corner where the game puts notices, and belongs in the window
    /// with the rest of the conversation. A client with an older Core ignores the call - ZRpc
    /// drops a method it has no handler for, silently - so nothing breaks while versions mix.
    /// </summary>
    internal static class ServerChat
    {
        internal const string Rpc = "Ezomic_Core_ChatLine";

        private static AccessTools.FieldRef<Chat, float> _hideTimer;
        private static bool _bound;

        /// <summary>Registered on every connection in <see cref="NetworkPatches"/>.</summary>
        internal static void Receive(ZRpc rpc, string title, string text)
        {
            try
            {
                // Only a client shows it. On a server this could only have come from a client,
                // which has no business putting words in anybody's window.
                if (ZNet.instance == null || ZNet.instance.IsServer()) return;

                var chat = Chat.instance;
                if (chat == null) return;

                title = Clean(title);
                text = Clean(text);
                if (text.Length == 0) return;

                chat.AddString(title.Length > 0 ? title : "Server", text, Talker.Type.Normal);
                Open(chat);
            }
            catch (Exception e)
            {
                CorePlugin.Log.LogWarning("Could not show a chat line from the server: " + e.Message);
            }
        }

        /// <summary>
        /// '&lt;' and '&gt;' become spaces, which is exactly what vanilla does to every chat line
        /// it receives: the window is rich text, and a line must not be able to resize, recolour
        /// or hide the rest of it.
        /// </summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            return s.Replace('<', ' ').Replace('>', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        /// <summary>
        /// Open the window the way an incoming message does, which in vanilla is zeroing a
        /// private timer. Bound on first use inside a try, not in a static initializer: a field
        /// ref that throws at type-init poisons every patch its class carries. If the field is
        /// ever renamed, the line still lands in the window; it just does not pop it open.
        /// </summary>
        private static void Open(Chat chat)
        {
            if (!_bound)
            {
                _bound = true;
                try { _hideTimer = AccessTools.FieldRefAccess<Chat, float>("m_hideTimer"); }
                catch (Exception e) { CorePlugin.Log.LogWarning("Chat window timer not found, lines will not pop it open: " + e.Message); }
            }

            if (_hideTimer != null) _hideTimer(chat) = 0f;
        }
    }
}
