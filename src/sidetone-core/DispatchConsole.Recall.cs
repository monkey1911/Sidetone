// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Shared core of both front ends.
*
* Instant recall and talkgroup recording. Every call the console hears on a listening channel,
* and every call it transmits, is kept as its IMBE codewords (already decrypted): about 0.5 kB a
* second. The last ten per channel can be played again, and on a channel set to record, each call
* is also written to a WAV file when it ends. Codewords are decoded only when a call is played or
* saved, so keeping them costs almost nothing while calls come in.
*
* Calls are held in memory only, and are gone when the console closes. Recordings are files and
* hold secure calls decrypted, so their folder is readable by the user only.
*/

using System.Diagnostics;
using System.Globalization;
using dvmconsole;
using Chan = Sidetone.Core.ConsoleChannel;

namespace Sidetone.Core
{
    /// <summary>One call kept for instant recall.</summary>
    public sealed class RecalledCall
    {
        public string Channel { get; init; }
        /// <summary>The system it was on (its recordings go in that system's folder).</summary>
        internal string SystemName { get; init; }
        public uint Talkgroup { get; init; }
        public uint Source { get; init; }
        /// <summary>Who spoke, as the aliases put it.</summary>
        public string Who { get; init; }
        public DateTime Started { get; init; }
        /// <summary>The console transmitted it.</summary>
        public bool Tx { get; init; }
        /// <summary>"clear", or the algorithm.</summary>
        public string Crypt { get; init; }
        /// <summary>The clear IMBE codewords, 20 ms each.</summary>
        internal readonly List<byte[]> Frames = new List<byte[]>();
        internal uint StreamId;
        internal DateTime Last;
        /// <summary>Some of the call went past the length kept.</summary>
        public bool Cut { get; internal set; }

        /// <summary>The call as the owner reads it: SYSTEM:CHANNEL:RADIOID/ALIAS.</summary>
        public string Label => $"{SystemName ?? "?"}:{Channel}:{Caller(Source, Who)}";

        /// <summary>RADIOID/ALIAS, or just the radio ID when it has no alias.</summary>
        public static string Caller(uint rid, string who) =>
            rid == 0 ? who ?? "?" : string.IsNullOrEmpty(who) || who == rid.ToString() ? rid.ToString() : $"{rid}/{who}";

        public TimeSpan Length => TimeSpan.FromMilliseconds(Frames.Count * 20);
        public string Describe() => $"{Started:HH:mm:ss} {(Tx ? "TX" : "RX")} {Who}, {Length.TotalSeconds:F0} s, {Crypt}";
    } // public sealed class RecalledCall

    public partial class DispatchConsole
    {
        /// <summary>Calls kept per channel.</summary>
        public const int RecallPerChannel = 10;
        /// <summary>The longest part of one call that is kept (30 minutes, about 1 MB).</summary>
        private const int MaxRecallFrames = 30 * 60 * 50;
        /// <summary>A call that has sent nothing for this long has ended, even if its end was missed.</summary>
        private static readonly TimeSpan RecallStale = TimeSpan.FromSeconds(3);
        /// <summary>How far ahead of the listener replay audio is written.</summary>
        private const int ReplayLeadMs = 240;

        private readonly object recallLock = new object();
        private readonly Dictionary<Chan, LinkedList<RecalledCall>> recalled = new Dictionary<Chan, LinkedList<RecalledCall>>();
        private readonly Dictionary<Chan, RecalledCall> openRx = new Dictionary<Chan, RecalledCall>(), openTx = new Dictionary<Chan, RecalledCall>();

        /// <summary>
        /// Folder recordings go into (each system and day gets its own folder underneath): the one set
        /// here (a command line option), else the saved setting, else the default.
        /// </summary>
        public string RecordingsFolder
        {
            get
            {
                string chosen = recordingsFolder ?? Settings?.RecordingsFolder;
                return string.IsNullOrWhiteSpace(chosen) ? DefaultRecordingsFolder : chosen;
            }
            set => recordingsFolder = value;
        }
        private string recordingsFolder;

        public static string DefaultRecordingsFolder
        {
            get
            {
                string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrWhiteSpace(data))
                    data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                return Path.Combine(data, "sidetone", "recordings");
            }
        }

        /// <summary>The calls kept for a channel, newest first.</summary>
        public List<RecalledCall> RecalledCalls(ConsoleChannel c)
        {
            lock (recallLock)
                return recalled.TryGetValue(c, out LinkedList<RecalledCall> list) ? list.ToList() : new List<RecalledCall>();
        }

        /*
        ** Collecting calls (network thread for receive, UI thread for transmit)
        */

        private void RecallCodeword(Chan c, P25VoiceReceiver rx, byte[] imbe)
        {
            lock (recallLock)
            {
                uint stream = rx.CurrentStreamId;
                if (!openRx.TryGetValue(c, out RecalledCall call) || call.StreamId != stream)
                {
                    if (call != null)
                        FinishCall(c, call, openRx);
                    byte alg = rx.CurrentAlgorithm;
                    call = new RecalledCall
                    {
                        Channel = c.Cfg.Name,
                        SystemName = c.Link.Name,
                        Talkgroup = c.Tg,
                        Source = rx.CurrentSource,
                        Who = c.Link.Aliases.Label(rx.CurrentSource),
                        Started = DateTime.Now,
                        Crypt = alg == 0 || alg == fnecore.P25.P25Defines.P25_ALGO_UNENCRYPT ? "clear" : KeyStore.AlgorithmName(alg),
                        StreamId = stream
                    };
                    openRx[c] = call;
                }
                Append(call, imbe);
            }
        }

        private void RecallCallEnded(Chan c)
        {
            lock (recallLock)
            {
                if (openRx.TryGetValue(c, out RecalledCall call))
                    FinishCall(c, call, openRx);
            }
        }

        /// <summary>The console keyed up: keep what it sends, as it went out.</summary>
        private void RecallTxStarted(Chan c, P25Transmitter t)
        {
            RecalledCall call = new RecalledCall
            {
                Channel = c.Cfg.Name,
                SystemName = c.Link.Name,
                Talkgroup = c.Tg,
                Source = t.SourceId,
                Who = c.Link.Aliases.Label(t.SourceId),
                Started = DateTime.Now,
                Tx = true,
                Crypt = t.Encrypted ? KeyStore.AlgorithmName(t.AlgorithmId) : "clear",
                StreamId = t.StreamId
            };
            lock (recallLock)
                openTx[c] = call;
            t.ClearFrame += imbe =>
            {
                lock (recallLock)
                    Append(call, imbe);
            };
        }

        internal void RecallTxStartedForTest(Chan c, P25Transmitter t) => RecallTxStarted(c, t);
        internal void RecallTxStoppedForTest(Chan c) => RecallTxStopped(c);

        private void RecallTxStopped(Chan c)
        {
            if (c == null)
                return;
            lock (recallLock)
            {
                if (openTx.TryGetValue(c, out RecalledCall call))
                    FinishCall(c, call, openTx);
            }
        }

        private static void Append(RecalledCall call, byte[] imbe)
        {
            call.Last = DateTime.UtcNow;
            if (call.Frames.Count < MaxRecallFrames)
                call.Frames.Add(imbe);
            else
                call.Cut = true;
        }

        /// <summary>Files a finished call: into the channel's recall list, and to disk if the channel records (caller holds the lock).</summary>
        private void FinishCall(Chan c, RecalledCall call, Dictionary<Chan, RecalledCall> open)
        {
            open.Remove(c);
            if (call.Frames.Count == 0)
                return;     // nothing could be decoded (no key): nothing to play

            if (!recalled.TryGetValue(c, out LinkedList<RecalledCall> list))
                recalled[c] = list = new LinkedList<RecalledCall>();
            list.AddFirst(call);
            while (list.Count > RecallPerChannel)
                list.RemoveLast();
            recallVersion++;
            HistoryChanged();

            if (c.Record)
                SaveRecording(call);
        }

        /// <summary>Changes whenever a call is added to a recall list (for redrawing).</summary>
        public int RecallVersion => recallVersion;
        private volatile int recallVersion;

        private void RecallHousekeeping()
        {
            DateTime now = DateTime.UtcNow;
            lock (recallLock)
            {
                foreach (var kv in openRx.Where(kv => now - kv.Value.Last > RecallStale).ToList())
                    FinishCall(kv.Key, kv.Value, openRx);
            }
        }

        /*
        ** Recording
        */

        /// <summary>Records a channel's calls to disk, or stops. Recording a channel starts listening to it.</summary>
        public void SetRecord(ConsoleChannel c, bool on)
        {
            if (c.Record == on)
                return;
            if (on && !c.IsP25)
            {
                AddLog($"{c.Cfg.Name}: {c.Cfg.Mode} channels can't be recorded yet");
                return;
            }
            if (on && !c.Selected)
            {
                Select(c);
                if (!c.Selected)
                    return;
            }
            c.Record = on;
            if (on)
                AddLog($"{c.Cfg.Name}: recording calls to {RecordingsFolder}" +
                       (c.Encrypted ? " (secure calls are saved decrypted)" : string.Empty));
            else
                AddLog($"{c.Cfg.Name}: recording off");
            MarkDirty();
        }

        public void ToggleRecord(ConsoleChannel c) => SetRecord(c, !c.Record);

        /// <summary>Recordings written, and the last one's path (for the self-test and the log).</summary>
        public int RecordingsWritten => recordingsWritten;
        private int recordingsWritten;
        internal string lastRecording;
        /// <summary>Recordings are written straight away instead of in the background (self-test).</summary>
        internal bool recordInline;

        /// <summary>Recording formats: Opus (the default) or WAV.</summary>
        public const string FormatOpus = "opus", FormatWav = "wav";

        /// <summary>The choices for deleting old recordings, in days (Settings offers these, and "keep").</summary>
        public static readonly int[] KeepDayChoices = { 7, 14, 30, 60, 90, 180, 365 };

        /// <summary>The format new recordings are written in: "opus" or "wav".</summary>
        public string RecordingFormat => string.Equals(Settings?.RecordingFormat, FormatWav, StringComparison.OrdinalIgnoreCase) ? FormatWav : FormatOpus;

        /// <summary>Recordings older than this many days are deleted (at start and every hour); null keeps them.</summary>
        public int? RecordingKeepDays => Settings?.RecordingKeepDays is int d && d > 0 ? d : null;

        private DateTime nextRecordingPrune;

        /// <summary>
        /// Deletes recordings older than the chosen number of days, in the background, and logs how
        /// many went. Nothing happens when they are kept. Called at start, every hour, and when the
        /// choice changes.
        /// </summary>
        public void PruneRecordingsSoon()
        {
            nextRecordingPrune = DateTime.Now + TimeSpan.FromHours(1);
            if (RecordingKeepDays is not int days)
                return;
            string folder = RecordingsFolder;
            Task.Run(() =>
            {
                int removed = Recordings.Prune(folder, DateTime.Now, days);
                if (removed > 0)
                    AddLog($"recordings: deleted {removed} older than {days} days from {folder}");
            });
        }

        private void RecordingHousekeeping()
        {
            if (DateTime.Now >= nextRecordingPrune && nextRecordingPrune != default)
                PruneRecordingsSoon();
        }

        private void SaveRecording(RecalledCall call)
        {
            string folder = RecordingsFolder;
            string systemName = call.SystemName ?? system.Name;
            string format = RecordingFormat;
            void Write()
            {
                try
                {
                    string path = RecordingPath(folder, systemName, call, format);
                    MakePrivateFolder(folder);
                    MakePrivateFolder(Path.GetDirectoryName(path));
                    string partial = path + ".part";
                    using (IAudioSink sink = format == FormatWav ? new WavSink(partial)
                           : new OpusFileSink(partial, $"{call.Channel} (TG {call.Talkgroup}) {(call.Tx ? "TX" : "RX")}", call.Who, call.Started))
                    {
                        if (!OperatingSystem.IsWindows())
                            File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        MBEDecoder decoder = new MBEDecoder(MBE_MODE.IMBE_88BIT);
                        foreach (byte[] imbe in call.Frames.ToList())
                        {
                            short[] samples = new short[P25VoiceReceiver.FrameSamples];
                            decoder.decode(imbe, samples);
                            sink.Write(samples);
                        }
                    }
                    File.Move(partial, path, overwrite: false);
                    Interlocked.Increment(ref recordingsWritten);
                    lastRecording = path;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    PostToUi(() => AddLog($"warning: {call.Channel}: the recording couldn't be saved ({ex.Message})"));
                }
            }

            if (recordInline)
                Write();
            else
                Task.Run(Write);
        }

        private static void MakePrivateFolder(string path)
        {
            if (Directory.Exists(path))
                return;
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(path);
            else
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        /// <summary>
        /// Where a call's recording goes: FOLDER/SYSTEM/YYYY-MM-DD/HHMMSS_SYSTEM_CHANNEL_RADIOID[_ALIAS].opus (or .wav).
        /// Inside each part anything that doesn't belong in a file name becomes '-', so '_' only separates the parts
        /// (Recordings.List reads them back). Before 0.36.2 it was HHMMSS_CHANNEL_TGnnn_RX_WHO; those still list.
        /// </summary>
        public static string RecordingPath(string folder, string systemName, RecalledCall call, string format = FormatOpus)
        {
            string ext = format == FormatWav ? ".wav" : ".opus";
            // the radio ID always (0 when there was none), so the list can tell the parts apart
            string caller = call.Source.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(call.Who) && call.Who != caller)
                caller += "_" + Safe(call.Who, '-');
            string name = $"{call.Started:HHmmss}_{Safe(systemName, '-')}_{Safe(call.Channel, '-')}_{caller}";
            // the folder keeps the old '_' form, so a system's recordings stay in one folder across versions
            string day = Path.Combine(folder, Safe(systemName), call.Started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            string path = Path.Combine(day, name + ext);
            for (int n = 2; File.Exists(path) || File.Exists(path + ".part"); n++)
                path = Path.Combine(day, $"{name}-{n}{ext}");
            return path;
        }

        private static string Safe(string text, char other = '_')
        {
            if (string.IsNullOrWhiteSpace(text))
                return "unknown";
            char[] chars = text.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '.' ? ch : other).ToArray();
            string s = new string(chars).Trim('.', '_', other);
            string twice = new string(other, 2);
            while (s.Contains(twice))
                s = s.Replace(twice, other.ToString());
            return s.Length == 0 ? "unknown" : s.Length > 48 ? s.Substring(0, 48) : s;
        }

        /*
        ** Desktop notifications
        */

        /// <summary>Shows a desktop notification when the console is paged: as set here (a command line option), else the saved setting, else yes.</summary>
        public bool Notifications
        {
            get => notifications ?? Settings?.Notifications ?? true;
            set => notifications = value;
        }
        private bool? notifications;

        /// <summary>Gets the notifications instead of the desktop (self-test).</summary>
        internal Action<string, string> notifyForTest;

        private readonly Dictionary<uint, DateTime> lastNotified = new Dictionary<uint, DateTime>();

        private void NotifyPaged(FneLink link, uint src)
        {
            if (!Notifications)
                return;
            DateTime now = DateTime.UtcNow;
            lock (lastNotified)
            {
                if (lastNotified.TryGetValue(src, out DateTime when) && now - when < TimeSpan.FromSeconds(10))
                    return;     // radios often repeat a call alert; one notification is enough
                lastNotified[src] = now;
            }
            string title = $"Paged by {link.Aliases.Label(src)}";
            string body = $"{link.Aliases.Describe(src)} sent a call alert to this console ({link.Name}, {DateTime.Now:HH:mm})";
            if (notifyForTest != null)
            {
                notifyForTest(title, body);
                return;
            }
            Task.Run(() =>
            {
                if (!DesktopNotify.Send(title, body, urgent: true))
                    PostToUi(() => AddLog("warning: no desktop notification shown (needs notify-send or gdbus, and a notification service)"));
            });
        }

        /*
        ** Replay
        */

        private sealed class ReplayJob
        {
            public Chan Chan;
            public RecalledCall Call;
            public CancellationTokenSource Stop = new CancellationTokenSource();
            public Stopwatch Clock = new Stopwatch();
        }

        private volatile ReplayJob replay;

        /// <summary>Plays to this instead of the speakers (self-test).</summary>
        internal Func<IAudioSink> replaySinkForTest;

        /// <summary>The call being played again, or null.</summary>
        public RecalledCall Replaying => replay?.Call;
        /// <summary>The channel whose call is being played again, or null.</summary>
        public ConsoleChannel ReplayChannel => replay?.Chan;
        /// <summary>How far into the call the replay is.</summary>
        public TimeSpan ReplayPosition
        {
            get
            {
                ReplayJob job = replay;
                if (job == null)
                    return TimeSpan.Zero;
                TimeSpan t = job.Clock.Elapsed;
                return t > job.Call.Length ? job.Call.Length : t;
            }
        }

        /// <summary>Plays a channel's latest call again, or stops the replay if one is playing.</summary>
        public void ReplayLast(ConsoleChannel c)
        {
            if (replay != null)
            {
                StopReplay();
                return;
            }
            RecalledCall last = RecalledCalls(c).FirstOrDefault();
            if (last == null)
            {
                AddLog(c.Selected ? $"{c.Cfg.Name}: no calls to replay yet" : $"{c.Cfg.Name}: no calls to replay (only calls on listening channels are kept)");
                return;
            }
            Replay(c, last);
        }

        /// <summary>Plays the call before the one being played (or the latest), stepping back through the channel's calls.</summary>
        public void ReplayOlder(ConsoleChannel c)
        {
            List<RecalledCall> calls = RecalledCalls(c);
            if (calls.Count == 0)
            {
                ReplayLast(c);
                return;
            }
            ReplayJob job = replay;
            int index = job != null && job.Chan == c ? calls.IndexOf(job.Call) + 1 : 0;
            if (index >= calls.Count)
            {
                StopReplay();
                AddLog($"{c.Cfg.Name}: that was the oldest call kept");
                return;
            }
            Replay(c, calls[index]);
        }

        /// <summary>Plays a kept call on the speakers, at the channel's volume (even if the channel is muted).</summary>
        public void Replay(ConsoleChannel c, RecalledCall call)
        {
            StopReplay();
            IAudioSink sink = null;
            try
            {
                if (replaySinkForTest != null)
                    sink = replaySinkForTest();
                else if (!NoAudio)
                    sink = AudioBackend.Current.OpenOutput(OutputFor(c));
            }
            catch (Exception ex)
            {
                AddLog($"{c.Cfg.Name}: can't play the replay ({ex.Message})");
                return;
            }

            ReplayJob job = new ReplayJob { Chan = c, Call = call };
            replay = job;
            AddLog($"{c.Cfg.Name}: replaying {call.Describe()}");
            bool fast = replaySinkForTest != null;
            double gain = c.Gain;
            VoiceProcessor level = RxLeveling ? new VoiceProcessor(VoiceProcessing.Receive) : null;
            List<byte[]> frames = call.Frames.ToList();
            job.Clock.Start();

            void Play()
            {
                try
                {
                    MBEDecoder decoder = new MBEDecoder(MBE_MODE.IMBE_88BIT);
                    for (int i = 0; i < frames.Count && !job.Stop.IsCancellationRequested; i++)
                    {
                        if (!fast)
                        {
                            // keep a little ahead of the listener, no more, so Stop is prompt
                            int wait = i * 20 - ReplayLeadMs - (int)job.Clock.ElapsedMilliseconds;
                            if (wait > 0 && job.Stop.Token.WaitHandle.WaitOne(wait))
                                break;
                        }
                        short[] samples = new short[P25VoiceReceiver.FrameSamples];
                        decoder.decode(frames[i], samples);
                        level?.Process(samples);
                        AudioMath.ApplyGain(samples, gain);
                        sink?.Write(samples);
                    }
                    if (!fast && !job.Stop.IsCancellationRequested)
                    {
                        int tail = frames.Count * 20 + 150 - (int)job.Clock.ElapsedMilliseconds;   // let the last of it play out
                        if (tail > 0)
                            job.Stop.Token.WaitHandle.WaitOne(tail);
                    }
                }
                catch (Exception) { /* the player went away; nothing to do */ }
                finally
                {
                    try { sink?.Dispose(); } catch (Exception) { }
                    if (replay == job)
                        replay = null;
                    job.Clock.Stop();
                    Wake();
                }
            }

            if (fast)
                Play();
            else
                Task.Run(Play);
        }

        /// <summary>Adds a made-up call to a channel's recall list (silent), for the demo system.</summary>
        public void AddDemoCall(ConsoleChannel c, uint source, string who, int seconds, bool tx, string crypt, DateTime started)
        {
            RecalledCall call = new RecalledCall { Channel = c.Cfg.Name, SystemName = c.Link.Name, Talkgroup = c.Tg, Source = source, Who = who, Started = started, Tx = tx, Crypt = crypt };
            byte[] silence = new byte[P25VoiceReceiver.ImbeLength];
            for (int i = 0; i < seconds * 50; i++)
                call.Frames.Add(silence);
            lock (recallLock)
            {
                if (!recalled.TryGetValue(c, out LinkedList<RecalledCall> list))
                    recalled[c] = list = new LinkedList<RecalledCall>();
                list.AddFirst(call);
                recallVersion++;
            }
        }

        public void StopReplay()
        {
            ReplayJob job = replay;
            if (job == null)
                return;
            replay = null;
            job.Stop.Cancel();
        }
    } // public partial class DispatchConsole

    /// <summary>
    /// Desktop notifications, through the freedesktop notification service (what GNOME, KDE, mako,
    /// dunst, swaync and the rest provide): notify-send when it's installed, otherwise gdbus.
    /// </summary>
    public static class DesktopNotify
    {
        /// <summary>The platform's own notifications (Android's), used instead of notify-send and gdbus when set: (title, body, urgent) → shown.</summary>
        public static Func<string, string, bool, bool> Platform { get; set; }

        /// <summary>Shows a notification. Returns false when neither tool is there or it failed.</summary>
        public static bool Send(string title, string body, bool urgent)
        {
            if (Platform != null)
                return Platform(title, body, urgent);
            string notifySend = AudioDeviceList.FindOnPath("notify-send");
            if (notifySend != null)
                return Run(notifySend, "--app-name=Sidetone", "--icon=sidetone", $"--urgency={(urgent ? "critical" : "normal")}", title, body);

            string gdbus = AudioDeviceList.FindOnPath("gdbus");
            if (gdbus != null)
                return Run(gdbus, "call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path", "/org/freedesktop/Notifications",
                           "--method", "org.freedesktop.Notifications.Notify", "Sidetone", "0", "sidetone", title, body, "[]",
                           $"{{'urgency': <byte {(urgent ? 2 : 1)}>}}", "-1");
            return false;
        }

        private static bool Run(string program, params string[] args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string a in args)
                    psi.ArgumentList.Add(a);
                using Process p = Process.Start(psi);
                if (p == null)
                    return false;
                _ = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(3000))
                {
                    try { p.Kill(true); } catch (Exception) { }
                    return false;
                }
                return p.ExitCode == 0;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException)
            {
                return false;
            }
        }
    } // public static class DesktopNotify
}
