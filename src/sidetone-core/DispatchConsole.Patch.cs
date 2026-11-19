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
* A received call reaches the console with whatever unevenness the network gave it, and a radio
* site (a small hotspot especially) chops up audio that doesn't arrive at a steady rate. So a
* patched call is held in a short buffer and sent out on a steady 180 ms rhythm, one LDU at a
* time, as a radio would send it: sending starts once two LDUs are waiting, and the talkgroup is
* asked for (the grant demand) at once, so the system has that time to grant it. At the end, what
* is still buffered is sent before the call is ended.
*
* Patches are named by the codeplug's patch groups when it has any (as the Windows console's
* are), otherwise Patch 1 to 3. Who is in which patch is chosen while the console runs and is
* not remembered: like the channel marker, a patch never starts transmitting by itself at launch.
*/

using System.Diagnostics;
using dvmconsole;
using fnecore.P25;
using Chan = Sidetone.Core.ConsoleChannel;

namespace Sidetone.Core
{
    /// <summary>
    /// Which way a patch member's traffic goes. A one-way patch from A to B has A as a source:
    /// A's calls go to B, and B's calls aren't sent back (the Windows console's one-way patch).
    /// </summary>
    public enum PatchRole
    {
        /// <summary>Its calls go to the rest of the patch, and the patch's calls are sent on it.</summary>
        Both,
        /// <summary>Its calls go to the rest of the patch; nothing is sent on it.</summary>
        SourceOnly,
        /// <summary>The patch's calls are sent on it; its own calls aren't passed on.</summary>
        TargetOnly
    }

    public partial class DispatchConsole
    {
        public const int MaxPatches = 6;
        /// <summary>A source that has sent nothing for this long has ended, even if its end was missed.</summary>
        private static readonly TimeSpan PatchStale = TimeSpan.FromSeconds(2);

        /// <summary>Voice frames per LDU, and how long an LDU lasts on the air.</summary>
        internal const int PatchLduFrames = 9;
        internal static readonly TimeSpan PatchLduTime = TimeSpan.FromMilliseconds(180);
        /// <summary>LDUs buffered before a patched call starts going out (and again after the source falls behind).</summary>
        internal const int PatchPrebufferLdus = 2;
        /// <summary>At most this much is buffered; beyond it the oldest audio is dropped rather than falling further behind.</summary>
        private const int PatchMaxLdus = 10;

        /// <summary>One patch while the console runs: who is talking into it, and the call being sent for it.</summary>
        private sealed class PatchRun
        {
            public Chan Source;
            public uint StreamId, SourceId;
            public DateTime Started, Last;
            public PatchSend Send;
        }

        /// <summary>
        /// One call being sent out on a patch's other members: its transmitters, the voice waiting to
        /// go out, and how evenly it arrived. Outlives its run while what's buffered drains.
        /// </summary>
        private sealed class PatchSend
        {
            public int Patch;
            public Chan Source;
            public uint SourceId;
            public DateTime Started;
            public readonly List<(Chan Chan, P25Transmitter Tx)> Targets = new List<(Chan, P25Transmitter)>();
            public readonly Queue<byte[]> Pending = new Queue<byte[]>();
            public bool Flowing, Ending;
            public string EndReason;
            public TimeSpan NextDue, LastIn = TimeSpan.MinValue, LongestGap;
            public int FramesIn, FramesOut, RanDry, Dropped;
        }

        private readonly object patchLock = new object();
        private readonly List<string> patchNames = new List<string>();
        private PatchRun[] patchRuns = Array.Empty<PatchRun>();
        private readonly Dictionary<uint, DateTime> patchStreams = new Dictionary<uint, DateTime>();   // our own, and until when to ignore them
        private readonly List<(Chan Chan, P25Transmitter Tx)> txPatch = new List<(Chan, P25Transmitter)>(); // the console's own PTT, on the rest of its patch
        private int consolePatch;       // the patch the console itself is transmitting into, or 0
        private bool patchPassthrough;
        private readonly List<PatchSend> patchDrains = new List<PatchSend>();     // ended calls still sending what's buffered
        private readonly Stopwatch patchClock = Stopwatch.StartNew();
        private Timer patchTimer;

        /// <summary>The self-test drives the patch clock itself (<see cref="PatchPump"/>) instead of a timer.</summary>
        internal bool patchManualPump;

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
                if (patch == 0)
                    c.PatchRole = PatchRole.Both;      // a channel joins a patch both ways
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

        /// <summary>A channel's patch direction as a mark: ⇄ both ways, → source only, ← target only (null when not patched).</summary>
        public static string PatchMark(ConsoleChannel c) => c.Patch == 0 ? null
            : c.PatchRole == PatchRole.SourceOnly ? "→" : c.PatchRole == PatchRole.TargetOnly ? "←" : "⇄";

        /// <summary>A patch direction in words, for buttons and the log.</summary>
        public static string PatchRoleText(PatchRole role) => role switch
        {
            PatchRole.SourceOnly => "source only",
            PatchRole.TargetOnly => "target only",
            _ => "both ways"
        };

        /// <summary>
        /// Sets which way a patched channel's traffic goes. A call being passed on right now ends; the
        /// next one follows the new direction.
        /// </summary>
        public void SetPatchRole(ConsoleChannel c, PatchRole role)
        {
            if (c.Patch == 0)
            {
                AddLog($"{c.Cfg.Name}: not in a patch");
                return;
            }
            if (c.PatchRole == role)
                return;
            if (Transmitting && txChan != null && txChan.Patch == c.Patch)
            {
                AddLog("can't change a patch while transmitting into it");
                return;
            }
            lock (patchLock)
            {
                EndRun(patchRuns[c.Patch - 1], "patch changed");
                c.PatchRole = role;
            }
            AddLog($"{c.Cfg.Name}: {PatchRoleText(role)} in {patchNames[c.Patch - 1]}");
            DescribePatch(c.Patch);
        }

        /// <summary>Moves a patched channel to the next direction: both ways, source only, target only.</summary>
        public void CyclePatchRole(ConsoleChannel c) =>
            SetPatchRole(c, c.PatchRole == PatchRole.Both ? PatchRole.SourceOnly : c.PatchRole == PatchRole.SourceOnly ? PatchRole.TargetOnly : PatchRole.Both);

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
                foreach (PatchSend d in patchDrains)
                    FinishSend(d, quiet ? null : "patches cleared");
                patchDrains.Clear();
                foreach (Chan c in chans)
                {
                    any |= c.Patch != 0;
                    c.Patch = 0;
                    c.PatchRole = PatchRole.Both;
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
                List<Chan> sources = members.Where(m => m.PatchRole != PatchRole.TargetOnly).ToList();
                List<Chan> targets = members.Where(m => m.PatchRole != PatchRole.SourceOnly && !m.Cfg.RxOnly).ToList();
                if (members.All(m => m.PatchRole == PatchRole.Both))
                    AddLog($"{name}: {string.Join(" + ", members.Select(m => m.Cfg.Name))}");
                else
                {
                    // as people say it: "A → B". A both-ways member sides with the sources when the others
                    // are targets, and with the targets when the others are sources.
                    bool anySource = members.Any(m => m.PatchRole == PatchRole.SourceOnly), anyTarget = members.Any(m => m.PatchRole == PatchRole.TargetOnly);
                    IEnumerable<string> left = members.Where(m => m.PatchRole == PatchRole.SourceOnly || (m.PatchRole == PatchRole.Both && (anyTarget || !anySource))).Select(m => m.Cfg.Name);
                    IEnumerable<string> right = members.Where(m => !m.Cfg.RxOnly && (m.PatchRole == PatchRole.TargetOnly || (m.PatchRole == PatchRole.Both && (anySource || !anyTarget)))).Select(m => m.Cfg.Name);
                    List<Chan> both = members.Where(m => m.PatchRole == PatchRole.Both).ToList();
                    AddLog($"{name}: one-way, {string.Join(" + ", left)} → {string.Join(" + ", right)}" +
                           (both.Count >= 2 ? $" ({string.Join(" + ", both.Select(m => m.Cfg.Name))} both ways between them)" : string.Empty));
                }
                if (!sources.Any(s => targets.Any(t => t != s)))
                    AddLog($"warning: {name} passes nothing on: it needs a source and a different target");
                List<Chan> clear = targets.Where(m => !m.TxSecure).ToList();
                if (clear.Count > 0 && sources.Any(m => m.Encrypted))
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
            if (p == 0 || c.PatchRole == PatchRole.TargetOnly)
                return;     // not patched, or a one-way patch's target: its calls aren't passed on
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
                PatchSend send = run.Send;
                if (send == null)
                    return;     // nowhere to send it (not connected)

                // how evenly the source's voice arrives: the longest wait between frames
                TimeSpan at = patchClock.Elapsed;
                if (send.LastIn != TimeSpan.MinValue && at - send.LastIn > send.LongestGap)
                    send.LongestGap = at - send.LastIn;
                send.LastIn = at;

                send.Pending.Enqueue((byte[])imbe.Clone());
                send.FramesIn++;
                if (send.Pending.Count > PatchMaxLdus * PatchLduFrames)
                {
                    for (int i = 0; i < PatchLduFrames; i++)
                        send.Pending.Dequeue();
                    send.Dropped += PatchLduFrames;
                }
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
                        EndRun(run, null, drain: true);
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
            PatchSend send = new PatchSend { Patch = patch, Source = source, SourceId = sourceId, Started = now };

            // a previous call on this patch still draining would be a second transmitter on the same talkgroups
            foreach (PatchSend d in patchDrains.Where(d => d.Patch == patch).ToList())
            {
                FinishSend(d, "next call");
                patchDrains.Remove(d);
            }

            List<string> skipped = new List<string>();
            foreach (Chan m in chans)
            {
                if (m == source || m.Patch != patch || !m.Selected || !m.IsP25)
                    continue;
                if (m.Cfg.RxOnly || m.PatchRole == PatchRole.SourceOnly)
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
                send.Targets.Add((m, t));
            }

            if (send.Targets.Count > 0)
            {
                run.Send = send;
                StartPatchPump();
                AddLog($"{name}: {source.Cfg.Name} → {string.Join(", ", send.Targets.Select(t => t.Chan.Cfg.Name))} ({aliases.Describe(sourceId)})");
            }
            foreach (string s in skipped)
                AddLog($"{name}: not sent to {s}");
        }

        /// <summary>
        /// Ends what a patch is passing on (caller holds the lock). With <paramref name="drain"/>, what is
        /// still buffered goes out first (the call ended normally); otherwise it stops now.
        /// </summary>
        private void EndRun(PatchRun run, string reason, bool drain = false)
        {
            if (run.Source == null)
                return;
            PatchSend send = run.Send;
            run.Send = null;
            run.Source = null;
            run.StreamId = 0;
            if (send == null)
                return;
            if (drain)
            {
                send.Ending = true;
                send.EndReason = reason;
                patchDrains.Add(send);
                StartPatchPump();
            }
            else
                FinishSend(send, reason);
        }

        /// <summary>Ends a patched call on its targets and logs how it went (caller holds the lock).</summary>
        private void FinishSend(PatchSend send, string reason)
        {
            DateTime now = DateTime.UtcNow;
            foreach ((Chan _, P25Transmitter tx) in send.Targets)
            {
                uint id = tx.StreamId;
                tx.Stop();
                if (id != 0)
                    patchStreams[id] = now + PatchStale;    // ignore it if the FNE sends it back late
            }
            if (send.Targets.Count == 0)
                return;

            string name = patchNames[send.Patch - 1];
            double secs = (now - send.Started).TotalSeconds;
            List<string> notes = new List<string>();
            if (send.RanDry > 0)
                notes.Add($"the source fell behind {send.RanDry}×");
            if (send.Dropped > 0)
                notes.Add($"{send.Dropped} frames dropped to catch up");
            string timing = $"{send.FramesOut} of {send.FramesIn} frames sent, longest gap from the source {send.LongestGap.TotalMilliseconds:F0} ms" +
                            (notes.Count > 0 ? $", {string.Join(", ", notes)}" : string.Empty);
            AddLog($"{name}: end {send.Source.Cfg.Name}, {secs:F1} s ({timing}){(reason != null ? $" ({reason})" : string.Empty)}");
            AddRecent(name, aliases.Label(send.SourceId), $"{secs:F0} s, {send.Source.Cfg.Name} → {string.Join(", ", send.Targets.Select(t => t.Chan.Cfg.Name))}", tx: true);
            send.Targets.Clear();
        }

        /// <summary>Starts the timer that sends patched voice at a steady rate, if it isn't running.</summary>
        private void StartPatchPump()
        {
            if (patchManualPump || patchTimer != null)
                return;
            patchTimer = new Timer(_ =>
            {
                try { PatchPump(patchClock.Elapsed); }
                catch (Exception ex) { AddLog($"patch: {ex.Message}"); }
            }, null, 10, 10);
        }

        /// <summary>
        /// Sends what is due on every patched call: one LDU every 180 ms once enough is buffered, and
        /// ends the calls that have drained. Runs on a timer (or from the self-test with its own clock).
        /// </summary>
        internal void PatchPump(TimeSpan now)
        {
            lock (patchLock)
            {
                foreach (PatchRun run in patchRuns)
                {
                    if (run.Send != null)
                        PumpSend(run.Send, now);
                }
                foreach (PatchSend d in patchDrains.ToList())
                {
                    if (PumpSend(d, now))
                    {
                        FinishSend(d, d.EndReason);
                        patchDrains.Remove(d);
                    }
                }
                if (patchRuns.All(r => r.Send == null) && patchDrains.Count == 0)
                {
                    patchTimer?.Dispose();      // nothing to send; started again by the next call
                    patchTimer = null;
                }
            }
        }

        /// <summary>Sends what is due on one call; true when it has ended and everything has gone out.</summary>
        private static bool PumpSend(PatchSend s, TimeSpan now)
        {
            if (!s.Flowing)
            {
                if (s.Pending.Count >= PatchPrebufferLdus * PatchLduFrames || (s.Ending && s.Pending.Count >= PatchLduFrames))
                {
                    s.Flowing = true;
                    s.NextDue = now;
                }
                else
                    return s.Ending;    // an ended call with less than an LDU left: done (a part LDU is dropped, as a radio's would be)
            }

            for (int sent = 0; sent < 2 && now >= s.NextDue; sent++)
            {
                if (s.Pending.Count < PatchLduFrames)
                {
                    if (s.Ending)
                        return true;
                    s.Flowing = false;  // the source fell behind: wait until there's a buffer again
                    s.RanDry++;
                    return false;
                }
                for (int i = 0; i < PatchLduFrames; i++)
                {
                    byte[] imbe = s.Pending.Dequeue();
                    foreach ((Chan _, P25Transmitter tx) in s.Targets)
                        tx.PushImbe(imbe);
                    s.FramesOut++;
                }
                s.NextDue += PatchLduTime;
            }
            if (now - s.NextDue > PatchLduTime + PatchLduTime)
                s.NextDue = now;        // the timer was held up: carry on from now rather than bursting to catch up
            return s.Ending && s.Pending.Count < PatchLduFrames;
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
                        EndRun(run, "source went quiet", drain: true);
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
                foreach (PatchSend send in patchRuns.Select(r => r.Send).Where(x => x != null).Concat(patchDrains))
                {
                    if (send.Targets.Any(t => t.Chan == c))
                        return $"TX patch ← {send.Source.Cfg.Name}";
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
            if (p == 0 || c.PatchRole == PatchRole.TargetOnly)
                return;     // a one-way patch's target: the console's own transmission stays on it

            lock (patchLock)
            {
                EndRun(patchRuns[p - 1], "console transmitting");
                foreach (PatchSend d in patchDrains.Where(d => d.Patch == p).ToList())
                {
                    FinishSend(d, "console transmitting");
                    patchDrains.Remove(d);
                }
                consolePatch = p;
                foreach (Chan m in chans)
                {
                    if (m == c || m.Patch != p || !m.Selected || !m.IsP25 || m.Cfg.RxOnly || m.PatchRole == PatchRole.SourceOnly)
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
