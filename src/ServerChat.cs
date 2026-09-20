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

        /// <summary>
        /// The same thing with the voice attached, as a second name rather than a third
        /// argument on the first.
        ///
        /// A name is the unit ZRpc dispatches on, and its handler is registered with a fixed
        /// parameter list, so adding an argument to <see cref="Rpc"/> would not extend it -
        /// it would redefine it, and every sender still using the two-argument form would
        /// have its lines land on a handler expecting three. Crier on the live server is
        /// exactly that sender, and it ships on its own schedule, so the site chat would
        /// have gone quiet between the two releases. Two names cost one registration and
        /// nothing else, and an old sender keeps working for as long as it exists.
        /// </summary>
        internal const string RpcSay = "Ezomic_Core_ChatSay";

        private static AccessTools.FieldRef<Chat, float> _hideTimer;
        private static bool _bound;

        /// <summary>Registered on every connection in <see cref="NetworkPatches"/>.</summary>
        internal static void Receive(ZRpc rpc, string title, string text)
        {
            Show(title, text, Talker.Type.Normal);
        }

        /// <summary>
        /// The voice as an int, because that is what the wire carries and what vanilla's own
        /// chat RPC carries. Anything unrecognised becomes Normal rather than being dropped:
        /// a line nobody can read is worse than a line in the wrong colour, and Ping is a
        /// map marker rather than a voice, so it would draw nothing at all.
        /// </summary>
        internal static void ReceiveSay(ZRpc rpc, string title, string text, int type)
        {
            Show(title, text, type == (int)Talker.Type.Shout ? Talker.Type.Shout
                : type == (int)Talker.Type.Whisper ? Talker.Type.Whisper
                : Talker.Type.Normal);
        }

        /// <summary>
        /// Shout is drawn yellow and uppercased by the game, whisper dimmed and lowercased,
        /// and that formatting is vanilla's, applied inside AddString - so this passes the
        /// type along rather than doing anything with it. Which matters: the alternative
        /// would be rich-text markup in the text, and <see cref="Clean"/> exists precisely
        /// to make sure a line arriving from the network cannot carry any.
        /// </summary>
        private static void Show(string title, string text, Talker.Type type)
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

                chat.AddString(title.Length > 0 ? title : "Server", text, type);
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
