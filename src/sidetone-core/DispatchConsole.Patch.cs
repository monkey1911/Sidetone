// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Shared core of both front ends.
*
* Patching: channels put in the same patch hear each other. When a call comes in on one member,
* the console sends it out on the others, and ends it there when it ends. The console's own
* transmissions on a member go to the whole patch as well.
*
* The Windows console decodes the received voice to audio and encodes it again for each target.
* Here the IMBE codewords themselves are passed across: the receiver has already decrypted them,
* and each target's transmitter encrypts them again for its own talkgroup if it is secure. The
* voice goes through the vocoder once, at the radio that spoke, and not again in the console.
*
* Patches are named by the codeplug's patch groups when it has any (as the Windows console's
* are), otherwise Patch 1 to 3. Who is in which patch is chosen while the console runs and is
* not remembered: like the channel marker, a patch never starts transmitting by itself at launch.
*/

using dvmconsole;
using fnecore.P25;
using Chan = Sidetone.Core.ConsoleChannel;

namespace Sidetone.Core
{
    public partial class DispatchConsole
    {
        public const int MaxPatches = 6;
        /// <summary>A source that has sent nothing for this long has ended, even if its end was missed.</summary>
        private static readonly TimeSpan PatchStale = TimeSpan.FromSeconds(2);

        /// <summary>One patch while the console runs: who is talking into it, and where that is being sent.</summary>
        private sealed class PatchRun
        {
            public Chan Source;
            public uint StreamId, SourceId;
            public DateTime Started, Last;
            public readonly List<(Chan Chan, P25Transmitter Tx)> Targets = new List<(Chan, P25Transmitter)>();
        }

        private readonly object patchLock = new object();
        private readonly List<string> patchNames = new List<string>();
        private PatchRun[] patchRuns = Array.Empty<PatchRun>();
        private readonly Dictionary<uint, DateTime> patchStreams = new Dictionary<uint, DateTime>();   // our own, and until when to ignore them
        private readonly List<(Chan Chan, P25Transmitter Tx)> txPatch = new List<(Chan, P25Transmitter)>(); // the console's own PTT, on the rest of its patch
        private int consolePatch;       // the patch the console itself is transmitting into, or 0
        private bool patchPassthrough;

        /// <summary>Stands in for the FNE connection in the self-test.</summary>
        internal IP25TrafficSink sinkForTest;
        private IP25TrafficSink TrafficSink => peer != null ? peer : sinkForTest;
        private bool CanSend => peer != null ? peer.Connected : sinkForTest != null;

        /// <summary>The patches' names: the codeplug's patch groups, or Patch 1 to 3.</summary>
        public IReadOnlyList<string> PatchNames => patchNames;

        private void SetUpPatches(Codeplug codeplug)
        {
            IEnumerable<Codeplug.Group> groups = (codeplug.Groups ?? new List<Codeplug.Group>()).Concat(codeplug.LegacyPatchGroups ?? new List<Codeplug.Group>());
            foreach (Codeplug.Group g in groups)
            {
                string name = g?.Name?.Trim();
                if (g == null || !g.IsPatchGroup() || string.IsNullOrEmpty(name) || patchNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (patchNames.Count < MaxPatches)
                    patchNames.Add(name);
            }
            if (patchNames.Count == 0)
                patchNames.AddRange(new[] { "Patch 1", "Patch 2", "Patch 3" });
            patchRuns = patchNames.Select(_ => new PatchRun()).ToArray();
            patchPassthrough = codeplug.PatchSourceIdPassthrough;
        }

        /// <summary>The name of the patch a channel is in, or null.</summary>
        public string PatchName(ConsoleChannel c)
        {
            int p = c.Patch;
            return p >= 1 && p <= patchNames.Count ? patchNames[p - 1] : null;
        }

        /// <summary>The channels in a patch (1 and up).</summary>
        public List<ConsoleChannel> PatchMembers(int patch) => chans.Where(c => c.Patch == patch).ToList();

        /// <summary>Whether a channel can be patched at all.</summary>
        public static bool CanPatch(ConsoleChannel c) => c.IsP25;

        /// <summary>
        /// Puts a channel in a patch (1 and up), or takes it out of its patch (0). Joining starts
        /// listening on the channel, because a patch only passes on what the console receives.
        /// </summary>
        public void SetPatch(ConsoleChannel c, int patch)
        {
            if (patch < 0 || patch > patchNames.Count)
                return;
            int was = c.Patch;
            if (was == patch)
                return;
            if (patch != 0 && !c.IsP25)
            {
                AddLog($"{c.Cfg.Name}: {c.Cfg.Mode} channels can't be patched yet");
                return;
            }
            if (Transmitting && txChan != null && (txChan.Patch == was && was != 0 || txChan.Patch == patch && patch != 0))
            {
                AddLog("can't change a patch while transmitting into it");
                return;
            }

            // whatever either patch is passing on right now ends; the next call starts with the new members
            lock (patchLock)
            {
                if (was != 0)
                    EndRun(patchRuns[was - 1], "patch changed");
                if (patch != 0)
                    EndRun(patchRuns[patch - 1], "patch changed");
                c.Patch = patch;
            }

            if (patch != 0 && !c.Selected)
                Select(c);
            if (patch != 0 && !c.Selected)
            {
                c.Patch = 0;        // couldn't start listening
                return;
            }

            if (was != 0)
                DescribePatch(was);
            if (patch != 0)
                DescribePatch(patch);
        }

        /// <summary>Moves a channel to the next patch: none, the first, the second, and so on, then none again.</summary>
        public void CyclePatch(ConsoleChannel c) => SetPatch(c, c.Patch >= patchNames.Count ? 0 : c.Patch + 1);

        /// <summary>Takes every channel out of its patch.</summary>
        public void ClearPatches(bool quiet = false)
        {
            bool any = false;
            lock (patchLock)
            {
                foreach (PatchRun run in patchRuns)
                    EndRun(run, quiet ? null : "patches cleared");
                foreach (Chan c in chans)
                {
                    any |= c.Patch != 0;
                    c.Patch = 0;
                }
            }
            if (any && !quiet)
                AddLog("patches cleared");
        }

        private void DescribePatch(int patch)
        {
            List<Chan> members = PatchMembers(patch);
            string name = patchNames[patch - 1];
            if (members.Count == 0)
                AddLog($"{name}: empty");
            else if (members.Count == 1)
                AddLog($"{name}: {members[0].Cfg.Name} (add another channel to patch it to)");
            else
            {
                AddLog($"{name}: {string.Join(" + ", members.Select(m => m.Cfg.Name))}");
                List<Chan> clear = members.Where(m => !m.TxSecure && !m.Cfg.RxOnly).ToList();
                if (clear.Count > 0 && members.Any(m => m.Encrypted))
                    AddLog($"warning: {name} joins secure and clear channels; secure traffic will go out in the clear on {string.Join(", ", clear.Select(m => m.Cfg.Name))}");
            }
        }

        /*
        ** Passing received calls on (network thread)
        */

        /// <summary>One decrypted voice codeword arrived on a listening channel.</summary>
        private void PatchCodeword(Chan c, P25VoiceReceiver rx, byte[] imbe)
        {
            int p = c.Patch;
            if (p == 0)
                return;
            uint stream = rx.CurrentStreamId;
            DateTime now = DateTime.UtcNow;

            lock (patchLock)
            {
                if (p > patchRuns.Length || patchStreams.ContainsKey(stream) || consolePatch == p)
                    return;     // our own traffic coming back, or the console has the patch
                PatchRun run = patchRuns[p - 1];

                if (run.Source != c || run.StreamId != stream)
                {
                    if (run.Source != null && now - run.Last <= PatchStale)
                        return;     // another member got there first; one talker at a time
                    EndRun(run, "source went quiet");
                    BeginRun(run, p, c, stream, rx.CurrentSource, now);
                }

                run.Last = now;
                foreach ((Chan _, P25Transmitter tx) in run.Targets)
                    tx.PushImbe(imbe);
            }
        }

        /// <summary>A call ended on a listening channel.</summary>
        private void PatchCallEnded(Chan c)
        {
            lock (patchLock)
            {
                foreach (PatchRun run in patchRuns)
                {
                    if (run.Source == c)
                        EndRun(run, null);
                }
            }
        }

        /// <summary>Starts sending a call out on the rest of its patch (caller holds the lock).</summary>
        private void BeginRun(PatchRun run, int patch, Chan source, uint stream, uint sourceId, DateTime now)
        {
            run.Source = source;
            run.StreamId = stream;
            run.SourceId = sourceId;
            run.Started = run.Last = now;

            string name = patchNames[patch - 1];
            if (!CanSend)
                return;     // nothing to send on; the run still marks who is talking
            uint from = patchPassthrough && sourceId != 0 ? sourceId : ConsoleRid;
            List<string> skipped = new List<string>();
            foreach (Chan m in chans)
            {
                if (m == source || m.Patch != patch || !m.Selected || !m.IsP25)
                    continue;
                if (m.Cfg.RxOnly)
                    continue;       // can be heard into the patch, never sent to
                if (m.Rx?.InCall == true)
                {
                    skipped.Add($"{m.Cfg.Name} (busy)");
                    continue;
                }

                P25Transmitter t = new P25Transmitter(TrafficSink, keys, from, m.Tg, m.ActiveTxAlg, m.ActiveKeyId);
                if (!t.Start(out string error))
                {
                    skipped.Add($"{m.Cfg.Name} ({error})");
                    if (m.TxSecure && KeyStore.IsSupported(m.KeyRequestAlg))
                        peer?.RequestKey(m.KeyRequestAlg, m.KeyId);
                    continue;
                }
                patchStreams[t.StreamId] = DateTime.MaxValue;   // while it lasts
                run.Targets.Add((m, t));
            }

            if (run.Targets.Count > 0)
                AddLog($"{name}: {source.Cfg.Name} → {string.Join(", ", run.Targets.Select(t => t.Chan.Cfg.Name))} ({aliases.Describe(sourceId)})");
            foreach (string s in skipped)
                AddLog($"{name}: not sent to {s}");
        }

        /// <summary>Ends what a patch is passing on (caller holds the lock). A reason is logged when given and something was being sent.</summary>
        private void EndRun(PatchRun run, string reason)
        {
            if (run.Source == null)
                return;
            DateTime now = DateTime.UtcNow;
            foreach ((Chan _, P25Transmitter tx) in run.Targets)
            {
                uint id = tx.StreamId;
                tx.Stop();
                if (id != 0)
                    patchStreams[id] = now + PatchStale;    // ignore it if the FNE sends it back late
            }
            if (run.Targets.Count > 0)
            {
                string name = patchNames[Array.IndexOf(patchRuns, run)];
                double secs = (now - run.Started).TotalSeconds;
                AddLog($"{name}: end {run.Source.Cfg.Name}, {secs:F1} s{(reason != null ? $" ({reason})" : string.Empty)}");
                AddRecent(name, aliases.Label(run.SourceId), $"{secs:F0} s, {run.Source.Cfg.Name} → {string.Join(", ", run.Targets.Select(t => t.Chan.Cfg.Name))}", tx: true);
            }
            run.Targets.Clear();
            run.Source = null;
            run.StreamId = 0;
        }

        /// <summary>Ends patches whose source stopped without saying so, and forgets old stream IDs.</summary>
        private void PatchHousekeeping()
        {
            DateTime now = DateTime.UtcNow;
            lock (patchLock)
            {
                foreach (PatchRun run in patchRuns)
                {
                    if (run.Source != null && now - run.Last > PatchStale)
                        EndRun(run, "source went quiet");
                }
                if (patchStreams.Count > 0)
                {
                    foreach (uint id in patchStreams.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
                        patchStreams.Remove(id);
                }
            }
        }

        /// <summary>What to show on a channel a patch (or the console, through its patch) is transmitting on, or null.</summary>
        private string PatchTransmitText(Chan c)
        {
            if (c.Patch == 0)
                return null;
            lock (patchLock)
            {
                if (txPatch.Any(t => t.Chan == c))
                    return $"TX patch ← {txChan?.Cfg.Name}";
                foreach (PatchRun run in patchRuns)
                {
                    if (run.Source != null && run.Targets.Any(t => t.Chan == c))
                        return $"TX patch ← {run.Source.Cfg.Name}";
                }
            }
            return null;
        }

        /*
        ** The console's own transmissions (UI thread)
        */

        /// <summary>The console keyed up on a patched channel: key the rest of the patch with the same voice.</summary>
        internal void StartPatchTx(Chan c, P25Transmitter main)
        {
            int p = c.Patch;
            if (p == 0)
                return;

            lock (patchLock)
            {
                EndRun(patchRuns[p - 1], "console transmitting");
                consolePatch = p;
                foreach (Chan m in chans)
                {
                    if (m == c || m.Patch != p || !m.Selected || !m.IsP25 || m.Cfg.RxOnly)
                        continue;
                    P25Transmitter t = new P25Transmitter(TrafficSink, keys, main.SourceId, m.Tg, m.ActiveTxAlg, m.ActiveKeyId);
                    if (!t.Start(out string error))
                    {
                        AddLog($"{patchNames[p - 1]}: not sent to {m.Cfg.Name} ({error})");
                        continue;
                    }
                    patchStreams[t.StreamId] = DateTime.MaxValue;
                    txPatch.Add((m, t));
                }
                if (txPatch.Count > 0)
                    AddLog($"{patchNames[p - 1]}: also transmitting on {string.Join(", ", txPatch.Select(t => t.Chan.Cfg.Name))}");
            }

            // the main transmitter's encoder runs once; the others get its codewords
            main.ClearFrame += imbe =>
            {
                lock (patchLock)
                {
                    foreach ((Chan _, P25Transmitter t) in txPatch)
                        t.PushImbe(imbe);
                }
            };
        }

        internal void StopPatchTx()
        {
            lock (patchLock)
            {
                DateTime now = DateTime.UtcNow;
                foreach ((Chan _, P25Transmitter t) in txPatch)
                {
                    uint id = t.StreamId;
                    t.Stop();
                    if (id != 0)
                        patchStreams[id] = now + PatchStale;
                }
                txPatch.Clear();
                consolePatch = 0;
            }
        }
    } // public partial class DispatchConsole
}
