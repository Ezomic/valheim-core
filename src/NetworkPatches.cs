using System.Collections.Generic;
using System.Text;
using HarmonyLib;

namespace Ezomic.Core
{
    /// <summary>
    /// The handshake, and the gate it feeds.
    ///
    /// The ordering here is the whole trick. Both sides send their mod list the moment the
    /// connection object exists, in <c>OnNewConnection</c>, which happens before either side
    /// sends <c>PeerInfo</c>. ZRpc delivers in order on one connection, so by the time
    /// <c>RPC_PeerInfo</c> runs the list has already arrived and the gate has something to
    /// check. Sending it any later means gating on data that is not there yet, and the
    /// symptom is a gate that lets the first connection through and works ever after.
    /// </summary>
    internal static class NetworkPatches
    {
        private const string RpcManifest = "Ezomic_Core_Manifest";
        private const string RpcConfig = "Ezomic_Core_Config";

        /// <summary>
        /// What each connection told us it has. Keyed by ZRpc because that is the only
        /// identity available this early - the ZNetPeer is not fully set up until PeerInfo,
        /// which is precisely what is being gated.
        /// </summary>
        private static readonly Dictionary<ZRpc, Dictionary<string, string>> Received =
            new Dictionary<ZRpc, Dictionary<string, string>>();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static void RegisterHandshake(ZNet __instance, ZNetPeer peer)
        {
            peer.m_rpc.Register<ZPackage>(RpcManifest, ReceiveManifest);
            peer.m_rpc.Register<ZPackage>(RpcConfig, ConfigSync.ReceiveConfig);

            peer.m_rpc.Invoke(RpcManifest, BuildManifest());
        }

        /// <summary>guid, version and requirement for every registered mod.</summary>
        private static ZPackage BuildManifest()
        {
            ZPackage pkg = new ZPackage();
            pkg.Write(Suite.Mods.Count);

            foreach (KeyValuePair<string, ModEntry> pair in Suite.Mods)
            {
                pkg.Write(pair.Key);
                pkg.Write(pair.Value.Version ?? "");
                pkg.Write((int)pair.Value.Requirement);
            }

            return pkg;
        }

        private static void ReceiveManifest(ZRpc rpc, ZPackage pkg)
        {
            Dictionary<string, string> theirs = new Dictionary<string, string>();

            int count = pkg.ReadInt();
            for (int i = 0; i < count; i++)
            {
                string guid = pkg.ReadString();
                string version = pkg.ReadString();
                pkg.ReadInt(); // their view of the requirement; ours is what we enforce
                theirs[guid] = version;
            }

            Received[rpc] = theirs;
        }

        /// <summary>
        /// The gate. Runs on both ends, but only the server can actually refuse - a client
        /// that dislikes what it sees logs it and lets the server do the disconnecting, so
        /// there is exactly one place a connection dies.
        ///
        /// The client checking anyway is not redundant: it is the only side that can say
        /// *what* was wrong in a log the player can read. The server's rejection arrives as
        /// the game's stock "incompatible version" screen with no room for detail.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        private static bool GateOnPeerInfo(ZNet __instance, ZRpc rpc)
        {
            if (!CorePlugin.EnforceVersions.Value) return true;

            Dictionary<string, string> theirs;
            if (!Received.TryGetValue(rpc, out theirs))
            {
                // No handshake at all means the other end has no Core. That is a mismatch
                // only if something here insists on being on both sides.
                theirs = new Dictionary<string, string>();
            }

            string problem = Compare(theirs);
            if (problem == null) return true;

            if (__instance.IsServer())
            {
                CorePlugin.Log.LogWarning("Refused a connection:\n" + problem);
                rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                return false;
            }

            CorePlugin.Log.LogError(
                "This server does not match your mods:\n" + problem
                + "\nThe server will close the connection.");
            return true;
        }

        /// <summary>
        /// Null when the two ends agree. Otherwise every disagreement at once, because
        /// fixing them one reconnect at a time is how a five-mod mismatch becomes an evening.
        /// </summary>
        private static string Compare(Dictionary<string, string> theirs)
        {
            StringBuilder problems = null;

            foreach (KeyValuePair<string, ModEntry> pair in Suite.Mods)
            {
                ModEntry mine = pair.Value;

                string theirVersion;
                bool present = theirs.TryGetValue(pair.Key, out theirVersion);

                // A host-only mod is allowed to be absent on the far end. It is not allowed
                // to be present at the wrong version - a half-updated group is the case that
                // actually happens, and it fails in stranger ways than nobody having it.
                if (!present)
                {
                    if (mine.Requirement == Requirement.HostOnly) continue;

                    Append(ref problems, "  " + mine.Name + " " + mine.Version
                        + " is missing on the other end.");
                    continue;
                }

                if (theirVersion != mine.Version)
                {
                    Append(ref problems, "  " + mine.Name + ": they have " + theirVersion
                        + ", this end has " + mine.Version + ".");
                }
            }

            // Mods on their end that are not on ours. Same danger, opposite direction: their
            // prefabs would arrive as ZDOs this end cannot resolve.
            foreach (KeyValuePair<string, string> pair in theirs)
            {
                if (Suite.Mods.ContainsKey(pair.Key)) continue;

                Append(ref problems, "  " + pair.Key + " " + pair.Value
                    + " is on the other end but not this one.");
            }

            return problems == null ? null : problems.ToString();
        }

        private static void Append(ref StringBuilder builder, string line)
        {
            if (builder == null) builder = new StringBuilder();
            else builder.Append('\n');

            builder.Append(line);
        }

        /// <summary>
        /// Once the peer is accepted, the host's settings follow. Postfix rather than a
        /// separate hook so it cannot run for a connection the gate above rejected.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        private static void PushConfigOnPeerInfo(ZNet __instance, ZRpc rpc)
        {
            if (!__instance.IsServer()) return;
            if (!CorePlugin.EnforceConfig.Value) return;

            rpc.Invoke(RpcConfig, ConfigSync.BuildConfig());
        }

        /// <summary>A connection that is gone cannot be asked about again.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNet), "Disconnect")]
        private static void ForgetPeer(ZNetPeer peer)
        {
            if (peer != null && peer.m_rpc != null) Received.Remove(peer.m_rpc);
        }
    }
}
