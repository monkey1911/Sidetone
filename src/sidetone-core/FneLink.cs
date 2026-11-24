// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* One FNE system the console is connected to (0.33: every system in the codeplug that has channels and
* isn't turned off in Settings, as the Windows console connects to all of them). Each has its own peer,
* keys (key IDs can mean different keys on different FNEs) and radio aliases; a channel uses its own
* system's. The console's first link is its "primary" one: codeplug-wide things (hidden tabs, tab outputs,
* the time announcement, call history) are kept under its name, so nothing moves for one-system codeplugs.
*/

using dvmconsole;

namespace Sidetone.Core
{
    public sealed class FneLink
    {
        internal FneLink(Codeplug.System cfg) => Cfg = cfg;

        /// <summary>The codeplug's entry for this system.</summary>
        public Codeplug.System Cfg { get; }
        public string Name => Cfg.Name;

        internal CliPeer Peer;
        internal readonly KeyStore Keys = new KeyStore();
        internal RadioAliasBook Aliases;

        /// <summary>Stands in for the FNE in the self-test (patches and tones are sent here).</summary>
        internal IP25TrafficSink SinkForTest;
        /// <summary>The console's radio ID in the self-test, when there's no peer.</summary>
        internal uint RidForTest;
        /// <summary>Stands in for the FNE's DMR traffic in the self-test: each packet sent, with its call and sequence.</summary>
        internal Action<fnecore.DMR.DMRCallData, byte[], ushort> DmrSendForTest;
        /// <summary>Builds DMR packets in the self-test (an unconnected peer; it only formats them).</summary>
        internal fnecore.FneSystemBase DmrFactoryForTest;

        public bool Connected => Peer?.Connected == true;
        /// <summary>The console's radio ID on this system (its codeplug rid).</summary>
        public uint ConsoleRid => Peer?.ConsoleRid ?? RidForTest;
        /// <summary>Where this system's traffic is sent: the peer, or the self-test's stand-in.</summary>
        internal IP25TrafficSink Sink => Peer != null ? Peer : SinkForTest;
        /// <summary>Traffic can be sent now.</summary>
        internal bool CanSend => Peer != null ? Peer.Connected : SinkForTest != null || DmrSendForTest != null;

        /// <summary>Makes DMR packets (the peer, or the self-test's).</summary>
        internal fnecore.FneSystemBase DmrFactory => (fnecore.FneSystemBase)Peer ?? DmrFactoryForTest;

        /// <summary>Sends one DMR packet (the peer, or the self-test's stand-in).</summary>
        internal void SendDmr(fnecore.DMR.DMRCallData call, byte[] packet, ushort sequence)
        {
            if (Peer != null)
            {
                if (!Peer.Connected)
                    throw new InvalidOperationException($"not connected to {Name}");
                Peer.SendDMRFrame(call, packet, sequence);
            }
            else
                DmrSendForTest?.Invoke(call, packet, sequence);
        }

        public override string ToString() => Name;
    } // public sealed class FneLink
}
