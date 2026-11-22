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

        public bool Connected => Peer?.Connected == true;
        /// <summary>The console's radio ID on this system (its codeplug rid).</summary>
        public uint ConsoleRid => Peer?.ConsoleRid ?? RidForTest;
        /// <summary>Where this system's traffic is sent: the peer, or the self-test's stand-in.</summary>
        internal IP25TrafficSink Sink => Peer != null ? Peer : SinkForTest;
        /// <summary>Traffic can be sent now.</summary>
        internal bool CanSend => Peer != null ? Peer.Connected : SinkForTest != null;

        public override string ToString() => Name;
    } // public sealed class FneLink
}
