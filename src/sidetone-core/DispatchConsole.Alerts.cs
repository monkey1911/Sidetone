// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Things the Windows console does around keying up, heard or checked locally:
*  - the talkgroup check: transmit (and tones, announcements) only on a talkgroup the FNE has announced for
*    this peer, when it has announced any (PeerSystem.IsTalkgroupAvailable);
*  - the transmit time limit's countdown: a short beep each second for the last 10 seconds and a long one
*    when it cuts off, on the transmitting channel's speaker, never sent; then PTT has to be let go before a
*    held key, button or desk mic keys up again (MainWindow.TransmitTimeout, OperatorPttTimeout);
*  - connection chimes: a rising chime when a system connects, the same notes falling when it drops.
*/

using fnecore;
using Chan = Sidetone.Core.ConsoleChannel;

namespace Sidetone.Core
{
    public partial class DispatchConsole
    {
        /*
        ** The talkgroup check
        */

        /// <summary>
        /// Why the FNE won't carry a channel's talkgroup, or null when it will (or hasn't announced any yet, as the Windows
        /// console allows). DMR matches the timeslot too, when the FNE's entries carry one.
        /// </summary>
        public static string TalkgroupBlocker(IReadOnlyList<TalkgroupEntry> table, uint tg, bool dmr, byte slot, string systemName)
        {
            if (table == null || table.Count == 0)
                return null;
            List<TalkgroupEntry> matching = table.Where(e => e != null && e.ID == tg).ToList();
            if (matching.Count == 0)
                return $"talkgroup {tg} isn't available on {systemName} (not in its talkgroup list)";
            if (!dmr || !matching.Any(e => e.Slot is 1 or 2))
                return matching.Any(e => !e.Invalid) ? null : $"talkgroup {tg} is turned off on {systemName}";
            return matching.Any(e => !e.Invalid && e.Slot == slot) ? null : $"talkgroup {tg} isn't available on TS{slot} on {systemName}";
        }

        /// <summary>Why a channel's talkgroup can't be keyed up on its FNE right now, or null.</summary>
        internal string TalkgroupBlocker(Chan c) => TalkgroupBlocker(c.Link.Talkgroups, c.Tg, c.IsDmr, c.Slot, c.Link.Name);

        /*
        ** Local sounds
        */

        /// <summary>Gets each local sound's name instead of playing it (self-test): "connect", "disconnect", "warning", "timeout".</summary>
        internal Action<string> localSoundForTest;

        /// <summary>Connection chimes are played (Settings; on unless turned off).</summary>
        public bool ConnectionChimes => Settings?.ConnectionChimes != false;

        /// <summary>The connection chime: G5, B5, D6 (a G major chord) rising to connect, the same notes falling for a drop.</summary>
        public static short[] ChimePcm(bool connected)
        {
            double[] notes = connected ? new[] { 784.0, 988.0, 1175.0 } : new[] { 1175.0, 988.0, 784.0 };
            const int rate = 8000, tone = 800, gap = 240, fade = 48;     // 100 ms notes, 30 ms apart, 6 ms fades
            short[] pcm = new short[notes.Length * tone + (notes.Length - 1) * gap];
            for (int n = 0; n < notes.Length; n++)
            {
                int at = n * (tone + gap);
                for (int i = 0; i < tone; i++)
                {
                    double envelope = Math.Min(1.0, Math.Min(i, tone - 1 - i) / (double)fade);
                    pcm[at + i] = (short)Math.Round(Math.Sin(2 * Math.PI * notes[n] * i / rate) * 0.2 * envelope * short.MaxValue);
                }
            }
            return pcm;
        }

        /// <summary>A 1 kHz beep for the time limit's countdown (0.1 s) and cut-off (1 s), as the Windows console's.</summary>
        public static short[] LimitBeepPcm(bool cutOff)
        {
            const int rate = 8000, fade = 48;
            int length = cutOff ? rate : rate / 10;
            short[] pcm = new short[length];
            for (int i = 0; i < length; i++)
            {
                double envelope = Math.Min(1.0, Math.Min(i, length - 1 - i) / (double)fade);
                pcm[i] = (short)Math.Round(Math.Sin(2 * Math.PI * 1000 * i / rate) * 0.2 * envelope * short.MaxValue);
            }
            return pcm;
        }

        /// <summary>Plays a short sound on this computer only (never sent): on a channel's speaker, or the default output.</summary>
        private void PlayLocal(string name, short[] pcm, Chan on = null)
        {
            if (localSoundForTest != null)
            {
                localSoundForTest(name);
                return;
            }
            if (NoAudio)
                return;
            IAudioOutput open = on?.Sink;
            if (open != null)
            {
                open.Write(pcm);
                return;
            }
            string device = on != null ? OutputFor(on) : Audio?.OutputTarget;
            Task.Run(() =>
            {
                IAudioOutput output = null;
                try
                {
                    output = AudioBackend.Current.OpenOutput(device);
                    output.Write(pcm);
                    Thread.Sleep(pcm.Length / 8 + 300);     // let it play out before the output closes
                }
                catch (Exception ex)
                {
                    PostToUi(() => AddLog($"couldn't play the {name} sound ({ex.Message})"));
                }
                finally
                {
                    try { output?.Dispose(); } catch (Exception) { }
                }
            });
        }

        /*
        ** Connection chimes
        */

        private readonly Dictionary<FneLink, bool> linkWasConnected = new Dictionary<FneLink, bool>();

        /// <summary>Notices systems connecting and dropping: logs a drop, and plays a chime (one per pass, a drop first).</summary>
        private void ConnectionHousekeeping()
        {
            bool up = false, down = false;
            foreach (FneLink link in links)
            {
                bool now = link.Connected;
                linkWasConnected.TryGetValue(link, out bool was);
                if (now == was)
                    continue;
                linkWasConnected[link] = now;
                if (now)
                    up = true;
                else
                {
                    down = true;
                    AddLog(Tagged(link, "lost the connection to the FNE; reconnecting"));
                }
            }
            if (!ConnectionChimes)
                return;
            if (down)
                PlayLocal("disconnect", ChimePcm(connected: false));
            else if (up)
                PlayLocal("connect", ChimePcm(connected: true));
        }

        /*
        ** The time limit's countdown, and letting go after it
        */

        /// <summary>The countdown beeps start this long before the limit.</summary>
        public static readonly TimeSpan LimitWarning = TimeSpan.FromSeconds(10);

        private int lastLimitWarning = -1;

        /// <summary>The time limit ended a transmission; a held PTT has to be let go before it keys up again.</summary>
        internal bool limitLatched;

        /// <summary>The time limit, with its countdown (called from Housekeeping).</summary>
        private void TxLimitHousekeeping()
        {
            IVoiceTransmitter t = tx;
            if (t == null || !t.IsTransmitting)
                return;
            TimeSpan held = DateTime.UtcNow - t.StartedAt;
            if (held > txLimit)
            {
                Chan on = txChan;
                bool wasHeld = deviceHold || holdSession != null;
                StopTx($"{txLimit.TotalSeconds:F0} s transmit limit reached");
                limitLatched = wasHeld;
                PlayLocal("timeout", LimitBeepPcm(cutOff: true), on);
                lastLimitWarning = -1;
                return;
            }
            int left = (int)Math.Ceiling((txLimit - held).TotalSeconds);
            if (left >= 1 && left <= LimitWarning.TotalSeconds && left != lastLimitWarning)
            {
                lastLimitWarning = left;
                PlayLocal("warning", LimitBeepPcm(cutOff: false), txChan);
            }
        }

        /// <summary>A held PTT pressed again after the time limit: refused until it has been let go (logged once).</summary>
        private bool HeldPttRefused()
        {
            if (!limitLatched)
                return false;
            if (!limitLatchLogged)
            {
                limitLatchLogged = true;
                AddLog("let go of PTT first: the transmit limit ended the last transmission");
            }
            return true;
        }
        private bool limitLatchLogged;

        /// <summary>PTT let go: a held key may key up again.</summary>
        private void PttLetGo()
        {
            limitLatched = false;
            limitLatchLogged = false;
        }
    } // public partial class DispatchConsole
}
