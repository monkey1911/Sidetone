// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Shared core of both front ends.
*
* The dispatch console itself, without any user interface: one system's channels, live receive
* on selected channels, push-to-talk on the focused channel, alert tones, pages and the channel
* marker, radio commands, microphone mute and metering, the PTT socket and PTT devices, aliases,
* remembered settings, the log and the recent-calls list. the terminal front end derives from it; the graphical one
* drives one through its public members.
*
* Threading: everything runs on the front end's UI thread (the one that called Start), except
* the network and audio callbacks, which only touch state that is locked or volatile. Work from
* other threads (the PTT socket, PTT devices) is queued and run by RunControlCalls / Tick.
*/

using System.Globalization;
using dvmconsole;
using fnecore;
using fnecore.P25;
using Chan = Sidetone.Core.ConsoleChannel;

namespace Sidetone.Core
{
    /// <summary>
    /// One channel card: its codeplug entry and live state.
    /// </summary>
    public sealed class ConsoleChannel
    {
        public Codeplug.Channel Cfg;
        /// <summary>The FNE system this channel is on.</summary>
        public FneLink Link;
        /// <summary>The codeplug zone's name, as written there (part of the remembered settings key).</summary>
        public string Zone;
        /// <summary>The zone (tab) this channel is on.</summary>
        public ConsoleZone InZone;
        /// <summary>The channel's zone is hidden: the channel is switched off.</summary>
        public bool Hidden => InZone != null && InZone.Hidden;
        public uint Tg;
        public bool IsP25;
        public bool Encrypted;
        public byte TxAlg;
        public byte KeyRequestAlg;
        public ushort KeyId;
        /// <summary>The codeplug lets the operator switch this channel's transmit between secure and clear.</summary>
        public bool Selectable;
        /// <summary>On a selectable channel: transmit in the clear (receive still decrypts).</summary>
        public volatile bool TxClear;

        /// <summary>Transmit on this channel is encrypted right now.</summary>
        public bool TxSecure => Encrypted && !(Selectable && TxClear);
        /// <summary>The algorithm transmit uses right now.</summary>
        public byte ActiveTxAlg => TxSecure ? TxAlg : P25Defines.P25_ALGO_UNENCRYPT;
        /// <summary>The key ID transmit uses right now.</summary>
        public ushort ActiveKeyId => TxSecure ? KeyId : (ushort)0;

        /// <summary>Evens out received levels on this channel, when that is on (network thread only).</summary>
        public volatile VoiceProcessor RxLevel;

        /// <summary>Calls on this channel are written to disk as they end.</summary>
        public volatile bool Record;

        /// <summary>The patch this channel is in (1 and up, an index into the console's patch names plus one), or 0 for none.</summary>
        public volatile int Patch;
        /// <summary>This channel's own audio output (a device name), or null to follow its tab.</summary>
        public string Output;
        /// <summary>Which way this channel's patch passes calls: both ways, from it only, or to it only.</summary>
        public volatile PatchRole PatchRole;

        public volatile bool Selected;
        public volatile bool Muted;
        public double Gain = 1.0;
        public P25VoiceReceiver Rx;
        public IAudioOutput Sink;

        // activity seen on channels that aren't selected
        public bool Active;
        public uint ActiveSrc;
        public uint ActiveStream;
        public DateTime ActiveLast;     // UTC, the last frame seen
        public string LastCall = string.Empty;
    } // public sealed class ConsoleChannel

    /// <summary>What a channel is doing.</summary>
    public enum ChannelActivity { Unsupported, Idle, Listening, Busy, Receiving, ReceivingNoKey, Transmitting, SendingTone }

    /// <summary>A channel's activity with a short description for its card.</summary>
    public readonly record struct ChannelStatus(ChannelActivity Activity, string Text, bool Muted);

    /// <summary>
    /// The dispatch console for one system.
    /// </summary>
    public partial class DispatchConsole
    {
        public const int MaxLogLines = 500;
        internal const int MaxRecentCalls = 100;

        /// <summary>Every FNE system connected; the first is the primary one (see FneLink).</summary>
        internal readonly List<FneLink> links = new List<FneLink>();
        /// <summary>The primary system (codeplug-wide settings and history are kept under its name).</summary>
        internal readonly Codeplug.System system;
        internal readonly TimeSpan txLimit;
        internal readonly string logFile;
        internal KeyStore keys => links[0].Keys;
        internal readonly List<Chan> chans = new List<Chan>();
        internal readonly object uiLock = new object();
        internal readonly LinkedList<string> log = new LinkedList<string>();
        internal readonly LinkedList<(DateTime When, string Chan, string Who, string Detail, bool Tx)> recent = new();

        internal CliPeer peer => links[0].Peer;
        internal int cursor;
        internal volatile bool globalMute;

        internal P25Transmitter tx;
        internal Chan txChan;
        internal IAudioInput capture;
        internal volatile string pendingTxStop;

        // microphone: one capture shared by transmit and the level meter
        internal readonly LevelMeter micLevel = new LevelMeter();
        internal volatile P25Transmitter txFeed;     // gets mic audio while transmitting
        internal bool micMonitor;                    // mic open just for the meter (v)
        internal volatile string pendingMicFail;

        // remembered settings
        internal readonly string settingsPath;
        internal readonly AppSettings settings;
        internal readonly string settingsError;
        internal bool settingsDirty;
        internal DateTime dirtyAt;
        internal bool talkPermit;

        // tones: alert 1-5 and the periodic channel marker, sent on every listening channel
        internal readonly TimeSpan markerInterval;
        internal volatile ToneJob toneJob;
        internal HashSet<Chan> toneChans = new HashSet<Chan>();
        internal bool markerOn;
        internal DateTime nextMarker;

        /// <summary>Self-test hook: don't start audio players when selecting channels.</summary>
        public bool NoAudio { get; set; }

        /// <summary>Output and input to use (from --output / --input or the defaults); null means the sound system's defaults, unreported.</summary>
        public AudioSelection Audio { get; set; }

        /// <summary>Index of the focused card (self-test hook).</summary>
        internal int Cursor => cursor;

        /// <summary>Whether channel <paramref name="i"/> is selected (self-test hook).</summary>
        internal bool IsSelected(int i) => chans[i].Selected;

        /// <summary>Global mute (self-test hook).</summary>
        public bool GlobalMute => globalMute;
        public bool MicMonitor => micMonitor;
        public RadioAliasBook Aliases => aliases;

        internal RadioAliasBook aliases => links[0].Aliases;
        public LevelMeter MicLevel => micLevel;

        /// <summary>Every FNE system connected, the primary one first.</summary>
        public IReadOnlyList<FneLink> Links => links;
        /// <summary>More than one system: names and log lines say which.</summary>
        public bool MultiSystem => links.Count > 1;
        /// <summary>How many systems are connected now.</summary>
        public int ConnectedCount => links.Count(l => l.Connected);

        /// <summary>
        /// Creates the console for one system of a codeplug.
        /// </summary>
        public DispatchConsole(Codeplug codeplug, Codeplug.System system, TimeSpan txLimit, string logFile, TimeSpan? markerInterval = null,
                   string settingsPath = null)
            : this(codeplug, new[] { system }, txLimit, logFile, markerInterval, settingsPath)
        {
        }

        /// <summary>
        /// Creates the console for several systems of a codeplug at once (the first is the primary one).
        /// </summary>
        public DispatchConsole(Codeplug codeplug, IReadOnlyList<Codeplug.System> systems, TimeSpan txLimit, string logFile, TimeSpan? markerInterval = null,
                   string settingsPath = null)
        {
            if (systems == null || systems.Count == 0)
                throw new ArgumentException("no systems to connect to");
            Codeplug.System system = systems[0];
            this.settingsPath = settingsPath;
            settings = AppSettings.Load(settingsPath, out settingsError);
            talkPermit = settings.TalkPermitTone;
            this.markerInterval = markerInterval ?? TimeSpan.FromSeconds(10);   // the Windows console's hold timer
            this.system = system;
            this.txLimit = txLimit;
            foreach (Codeplug.System sys in systems)
            {
                if (links.Any(l => string.Equals(l.Name, sys.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                FneLink link = new FneLink(sys) { Aliases = new RadioAliasBook(codeplug, sys, Path.GetDirectoryName(settingsPath ?? AppSettings.DefaultPath)) };
                link.Aliases.Status += m => AddLog(Tagged(link, m));
                links.Add(link);
            }
            this.logFile = logFile;
            SetUpPatches(codeplug);

            List<Codeplug.Zone> cpZones = codeplug.Zones ?? new List<Codeplug.Zone>();
            for (int zi = 0; zi < cpZones.Count; zi++)
            {
                Codeplug.Zone zone = cpZones[zi];
                if (zone == null)
                    continue;
                foreach (Codeplug.Channel c in zone.Channels ?? new List<Codeplug.Channel>())
                {
                    if (c == null)
                        continue;
                    // a channel without a system is on the primary one, as before
                    FneLink on = string.IsNullOrWhiteSpace(c.System) ? links[0]
                               : links.FirstOrDefault(l => string.Equals(l.Name, c.System.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (on == null)
                        continue;

                    bool p25 = c.GetChannelMode() == Codeplug.ChannelMode.P25;
                    bool enc = p25 && !string.IsNullOrWhiteSpace(c.Algo) && !c.Algo.Equals("none", StringComparison.OrdinalIgnoreCase);
                    uint.TryParse(c.Tgid, out uint tg);
                    ConsoleZone inZone = ZoneFor(zone, zi);
                    Chan made = new Chan
                    {
                        Cfg = c,
                        Link = on,
                        Zone = zone.Name,
                        InZone = inZone,
                        Tg = tg,
                        IsP25 = p25 && tg != 0,
                        Encrypted = enc,
                        Selectable = enc && c.SelectableEncryption,
                        TxAlg = enc ? c.GetAlgoId() : P25Defines.P25_ALGO_UNENCRYPT,
                        KeyRequestAlg = enc ? c.GetKeyRequestAlgoId() : (byte)0,
                        KeyId = enc ? c.GetKeyId() : (ushort)0
                    };
                    chans.Add(made);
                    inZone.chans.Add(made);
                }
            }

            if (chans.Count == 0)
                throw new ArgumentException(links.Count == 1 ? $"no channels for system '{system.Name}' in the codeplug"
                                                             : $"no channels for systems {string.Join(", ", links.Select(l => $"'{l.Name}'"))} in the codeplug");
            SetUpStreams(codeplug);
        }

        /*
        ** Lifetime
        */

        /// <summary>
        /// Connects to the FNE and starts everything the console needs: aliases, remembered settings,
        /// the PTT socket and the PTT device. Call it on the thread that will drive the console (the
        /// front end's UI thread); every other method except the audio and network callbacks expects that thread.
        /// </summary>
        public void Start()
        {
            if (LogFolder != null && logFile == null)
                LogFiles.Prune(LogFolder, DateTime.Now);
            AddLog($"Sidetone {Version} started{(LogFilePath != null ? $"; log file {LogFilePath}" : string.Empty)}");
            foreach (FneLink link in links)
            {
                try { link.Peer = new CliPeer(link.Cfg, link.Keys, verbose: false); }
                catch (FneAddressException ex)
                {
                    AddLog(Tagged(link, $"error: {ex.Message} (address \"{ex.Host}\")"));
                    if (links.Count == 1)
                        throw;
                    continue;   // the other systems still connect
                }
                FneLink l = link;
                link.Peer.Status += m => AddLog(Tagged(l, m));
                link.Peer.P25Frame += e => OnFrame(l, e);
                link.Peer.P25Control += (lco, src, dst, tsbk) => OnP25Control(l, lco, src, dst, tsbk);
                link.Peer.Aliases = link.Aliases;
            }

            if (Audio != null)
            {
                AddLog(Audio.OutputReport);
                AddLog(Audio.InputReport);
            }
            ReloadAudioProcessing(log: true);
            foreach (FneLink link in links)
                AddLog($"connecting to {link.Name} ({link.Cfg.Address}:{link.Cfg.Port}) as peer {link.Cfg.PeerId}");
            if (settingsError != null)
                AddLog(settingsError);
            foreach (FneLink link in links)
                link.Aliases.Load();
            RestoreSettings();
            LoadHistory();
            PruneRecordingsSoon();
            StartControl();
            StartPttDevice();
            foreach (FneLink link in links)
                link.Peer?.Start();
            ClaimUiThread();
        }

        /// <summary>
        /// One pass of regular work: runs queued control commands, then timers (transmit limit, the
        /// no-audio warning, saving settings, the channel marker). Call it a few times a second.
        /// </summary>
        public void Tick()
        {
            RunControlCalls();
            Housekeeping();
        }

        /// <summary>
        /// Stops everything: PTT hooks, tones, transmit, the microphone, listening channels and the FNE connection.
        /// </summary>
        public void Shutdown()
        {
            StopControl();
            StopPttDevice();
            RunControlCalls();  // answer anything still queued
            CancelTones(wait: true);
            SaveSettings();     // before the channels are closed below
            SaveHistory();
            StopTx("console closed");
            StopReplay();
            StopAllStreams();
            ClearPatches(quiet: true);
            foreach (FneLink link in links)
                link.Keys.SetLocal(null, 0);
            localKeys?.Dispose();   // wipes the key file's keys from memory
            localKeys = null;
            micMonitor = false;
            CloseMicIfUnused();
            foreach (Chan c in chans)
                Deselect(c, quiet: true);
            foreach (FneLink link in links)
                link.Peer?.Stop();
        }

        /*
        ** State for front ends
        */

        /// <summary>Sidetone's version, like 0.25.0.</summary>
        public static string Version
        {
            get
            {
                Version v = typeof(DispatchConsole).Assembly.GetName().Version;
                return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        /// <summary>The channel cards, in codeplug order.</summary>
        public IReadOnlyList<ConsoleChannel> Channels => chans;
        /// <summary>The system this console is on.</summary>
        public Codeplug.System SystemConfig => system;
        /// <summary>Longest transmission allowed.</summary>
        public TimeSpan TxLimit => txLimit;
        /// <summary>Logged in to every FNE system.</summary>
        public bool Connected => links.All(l => l.Connected);
        /// <summary>This console's radio ID on the primary system (0 until connected).</summary>
        public uint ConsoleRid => links[0].ConsoleRid;
        /// <summary>Encryption keys received so far.</summary>
        public KeyStore Keys => keys;
        /// <summary>Whether the key for an encrypted channel has arrived.</summary>
        public bool HasKey(ConsoleChannel c) => c.Encrypted && c.Link.Keys.TryGet(c.KeyRequestAlg, c.KeyId, out _);
        /// <summary>The channel's key comes from the local key file rather than the FNE.</summary>
        public bool KeyIsLocal(ConsoleChannel c) => c.Encrypted && c.Link.Keys.IsLocal(c.KeyRequestAlg, c.KeyId);

        /// <summary>A log line from one system: named when there are several.</summary>
        internal string Tagged(FneLink link, string message) => links.Count > 1 && link != null ? $"{link.Name}: {message}" : message;

        /// <summary>The local key file (.ekc) to use, or null. Set it before unlocking with <see cref="LoadKeyFile"/>.</summary>
        public string KeyFilePath { get; set; }
        /// <summary>The local key file is unlocked and its keys are in use.</summary>
        public bool KeyFileLoaded => localKeys != null;

        private LocalKeyFile localKeys;

        /// <summary>
        /// Unlocks the local key file with its password. Its keys are then used before any from the
        /// FNE. Returns null, or why it couldn't; <paramref name="wrongPassword"/> says whether trying
        /// another password is worth it. The password and the keys are never logged.
        /// </summary>
        public string LoadKeyFile(string password, out bool wrongPassword)
        {
            wrongPassword = false;
            if (string.IsNullOrWhiteSpace(KeyFilePath))
                return "no key file is set";

            LocalKeyFile opened = LocalKeyFile.Open(KeyFilePath, password, out string error, out wrongPassword);
            if (opened == null)
            {
                if (!wrongPassword)
                    AddLog($"warning: key file {Path.GetFileName(KeyFilePath)} wasn't loaded: {error}");
                return error;
            }

            localKeys?.Dispose();
            localKeys = opened;
            foreach (FneLink link in links)
                link.Keys.SetLocal(opened.Lookup, opened.Count);
            int used = chans.Count(c => c.Encrypted && c.Link.Keys.IsLocal(c.KeyRequestAlg, c.KeyId));
            AddLog($"keys: {opened.Count} from {Path.GetFileName(KeyFilePath)}" +
                   (opened.Skipped > 0 ? $" ({opened.Skipped} skipped: not traffic keys, or an algorithm that isn't supported)" : string.Empty) +
                   $"; {used} of {(links.Count > 1 ? "the" : "this system's")} channels use one");
            return null;
        }
        /// <summary>The channel being transmitted on, or null.</summary>
        public ConsoleChannel TxChannel => txChan;
        /// <summary>The current transmission, or null.</summary>
        public P25Transmitter Transmitter => tx;
        /// <summary>The tone being sent (alert, page, marker beep), or null.</summary>
        public ToneJob ActiveTone => toneJob;
        /// <summary>Whether a tone is being sent on this channel.</summary>
        public bool IsSendingTone(ConsoleChannel c) => toneChans.Contains(c);
        /// <summary>Index of the focused channel: the one PTT transmits on.</summary>
        public int FocusIndex => cursor;

        /// <summary>Focuses a channel (PTT then transmits on it).</summary>
        public void SetFocus(int index) => FocusTo(index);

        /// <summary>Changes whenever a log line is added.</summary>
        public long LogVersion => Interlocked.Read(ref logVersion);
        /// <summary>Changes whenever a recent call is added.</summary>
        public long RecentVersion => Interlocked.Read(ref recentVersion);
        private long logVersion, recentVersion;

        /// <summary>The remembered settings (shared with the settings file; call <see cref="SettingsChanged"/> after changing one).</summary>
        public AppSettings Settings => settings;

        /// <summary>Saves the settings shortly.</summary>
        public void SettingsChanged() => MarkDirty();

        /// <summary>The newest <paramref name="max"/> log lines, oldest first.</summary>
        public List<string> SnapshotLog(int max)
        {
            lock (uiLock)
                return log.Skip(Math.Max(0, log.Count - max)).ToList();
        }

        /// <summary>The newest <paramref name="max"/> recent calls, newest first.</summary>
        public List<(string Time, string Chan, string Who, string Detail, bool Tx)> SnapshotRecent(int max)
        {
            DateTime today = DateTime.Today;
            lock (uiLock)
                return recent.Take(max).Select(r => (r.When.Date == today ? r.When.ToString("HH:mm") : r.When.ToString("MM-dd HH:mm"), r.Chan, r.Who, r.Detail, r.Tx)).ToList();
        }

        /// <summary>What a channel is doing right now, for its card.</summary>
        public ChannelStatus GetStatus(ConsoleChannel c)
        {
            bool muted = c.Muted || globalMute;
            if (!c.IsP25)
                return new ChannelStatus(ChannelActivity.Unsupported, $"{c.Cfg.Mode}: not supported yet", muted);

            ToneJob job = toneJob;
            if (job != null && toneChans.Contains(c))
                return new ChannelStatus(ChannelActivity.SendingTone, $"sending {job.Plan.Name}", muted);

            P25Transmitter t = tx;
            if (t != null && t.IsTransmitting && txChan == c)
                return new ChannelStatus(ChannelActivity.Transmitting, $"TX {DateTime.Now - t.StartedAt:mm\\:ss}", muted);
            string patched = PatchTransmitText(c);
            if (patched != null)
                return new ChannelStatus(ChannelActivity.Transmitting, patched, muted);

            P25VoiceReceiver rx = c.Rx;
            if (c.Selected && rx != null && rx.InCall)
            {
                byte alg = rx.CurrentAlgorithm;
                bool enc = alg != 0 && alg != P25Defines.P25_ALGO_UNENCRYPT;
                bool noKey = enc && rx.CallHasMuted && !c.Link.Keys.TryGet(alg, rx.CurrentKeyId, out _);
                return new ChannelStatus(noKey ? ChannelActivity.ReceivingNoKey : ChannelActivity.Receiving,
                                         $"RX {c.Link.Aliases.Label(rx.CurrentSource)}{(noKey ? " (no key)" : string.Empty)}", muted);
            }

            if (!c.Selected && c.Active)
                return new ChannelStatus(ChannelActivity.Busy, $"busy: {c.Link.Aliases.Label(c.ActiveSrc)}", muted);
            if (c.Selected)
                return new ChannelStatus(ChannelActivity.Listening, muted ? "listening (muted)" : "listening", muted);
            return new ChannelStatus(ChannelActivity.Idle, "not listening", muted);
        }

        /*
        ** Network
        */

        internal void OnFrame(P25DataReceivedEvent e) => OnFrame(links[0], e);

        /// <summary>A voice frame from one system: only that system's channels on its talkgroup take it.</summary>
        internal void OnFrame(FneLink link, P25DataReceivedEvent e)
        {
            List<Chan> matching;
            lock (uiLock)
                matching = chans.Where(c => c.Link == link && c.IsP25 && c.Tg == e.DstId && !c.Hidden).ToList();     // a hidden zone's channels are off

            bool terminator = e.DUID == P25DUID.TDU || e.DUID == P25DUID.TDULC;
            foreach (Chan c in matching)
            {
                P25VoiceReceiver rx = c.Rx;
                if (c.Selected && rx != null)
                {
                    rx.Handle(e.DstId, e.SrcId, e.DUID, e.StreamId, e.Data);
                    continue;
                }

                // not selected: just show that the channel is busy
                lock (uiLock)
                {
                    if (terminator)
                    {
                        if (c.Active && e.StreamId == c.ActiveStream)
                            EndBusy(c);
                    }
                    else if (e.DUID == P25DUID.LDU1 || e.DUID == P25DUID.LDU2 || e.DUID == P25DUID.HDU)
                    {
                        c.Active = true;
                        c.ActiveSrc = e.SrcId;
                        c.ActiveStream = e.StreamId;
                        c.ActiveLast = UtcNow;
                    }
                }
            }
        }

        /// <summary>A channel that isn't listened to stops showing busy (caller holds uiLock).</summary>
        private void EndBusy(Chan c)
        {
            c.Active = false;
            c.LastCall = $"{c.Link.Aliases.Label(c.ActiveSrc)} at {DateTime.Now:HH:mm}";
            AddRecent(c.Cfg.Name, c.Link.Aliases.Label(c.ActiveSrc), "not listening", tx: false);
        }

        /// <summary>How long a call may go without frames before its end is taken as lost (as recall does).</summary>
        internal static readonly TimeSpan CallStale = TimeSpan.FromSeconds(3);

        /// <summary>Ends calls whose end (a TDU) never arrived, so a lost packet doesn't leave a channel receiving or busy.</summary>
        internal void CallHousekeeping()
        {
            List<Chan> all;
            lock (uiLock)
                all = chans.ToList();
            foreach (Chan c in all)
                c.Rx?.EndIfIdle(CallStale);     // not under uiLock: ending a call takes the patch and recall locks
            lock (uiLock)
            {
                foreach (Chan c in all.Where(c => c.Active && UtcNow - c.ActiveLast > CallStale))
                    EndBusy(c);
            }
        }

        /*
        ** Channels
        */

        public void Select(Chan c)
        {
            if (c.Selected)
                return;
            if (c.Hidden)
            {
                AddLog($"{c.Cfg.Name}: zone {c.InZone.Name} is hidden; show it again to listen here");
                return;
            }
            if (!c.IsP25)
            {
                AddLog($"{c.Cfg.Name}: {c.Cfg.Mode} channels aren't supported yet");
                return;
            }

            FneLink link = c.Link;
            RadioAliasBook aliases = link.Aliases;
            P25VoiceReceiver rx = new P25VoiceReceiver(c.Tg, link.Keys) { Clock = () => UtcNow };
            rx.Status += s => { if (!s.StartsWith("CALL ")) AddLog($"{c.Cfg.Name}: {s}"); };
            rx.KeyNeeded += (alg, kid) => link.Peer?.RequestKey(alg, kid);
            rx.DescribeSource = aliases.Describe;
            rx.CallStarted += src =>
            {
                AddLog($"{c.Cfg.Name}: RX {aliases.Describe(src)}");
                CallStarted?.Invoke(c, src);
            };
            rx.CallEnded += (src, secs, played, muted, crypt) =>
            {
                c.LastCall = $"{aliases.Label(src)} at {DateTime.Now:HH:mm} ({secs:F0} s)";
                AddLog($"{c.Cfg.Name}: end {aliases.Describe(src)}, {secs:F1} s, {crypt}{(muted > 0 ? $", {muted} frames muted" : string.Empty)}");
                AddRecent(c.Cfg.Name, aliases.Label(src), $"{secs:F0} s, {crypt}{(muted > 0 ? ", partly muted" : string.Empty)}", tx: false);
            };
            rx.Codeword += imbe => PatchCodeword(c, rx, imbe);
            rx.Codeword += imbe => RecallCodeword(c, rx, imbe);
            rx.CallEnded += (src, secs, played, muted, crypt) => PatchCallEnded(c);
            rx.CallEnded += (src, secs, played, muted, crypt) => RecallCallEnded(c);
            rx.Audio += samples =>
            {
                IAudioOutput sink = c.Sink;
                if (sink == null || c.Muted || globalMute || (txChan == c && tx?.IsTransmitting == true) || toneChans.Contains(c))
                    return;
                c.RxLevel?.Process(samples);
                AudioMath.ApplyGain(samples, c.Gain);
                sink.Write(samples);
            };

            if (!NoAudio)
            {
                try
                {
                    IAudioOutput sink = AudioBackend.Current.OpenOutput(OutputFor(c));
                    sink.Failed += msg => AddLog($"{c.Cfg.Name}: {msg}");
                    c.Sink = sink;
                }
                catch (Exception ex)
                {
                    AddLog($"{c.Cfg.Name}: no audio output ({ex.Message})");
                }
            }

            c.Rx = rx;
            c.Active = false;
            c.Selected = true;
            AddLog($"{c.Cfg.Name}: listening (tg {c.Tg})");
            MarkDirty();

            if (c.Encrypted && KeyStore.IsSupported(c.KeyRequestAlg) && !c.Link.Keys.TryGet(c.KeyRequestAlg, c.KeyId, out _))
                c.Link.Peer?.RequestKey(c.KeyRequestAlg, c.KeyId);
        }

        public void Deselect(Chan c, bool quiet = false)
        {
            if (!c.Selected)
                return;
            if (txChan == c)
                StopTx("channel deselected");
            if (c.Patch != 0)
                SetPatch(c, 0);     // a patch member has to be listening
            if (c.Record)
                SetRecord(c, false);    // only what the console hears can be recorded
            RecallCallEnded(c);

            c.Selected = false;
            P25VoiceReceiver rx = c.Rx;
            IAudioOutput sink = c.Sink;
            c.Rx = null;
            c.Sink = null;
            rx?.Dispose();
            sink?.Dispose();
            if (!quiet)
            {
                AddLog($"{c.Cfg.Name}: stopped listening");
                MarkDirty();
            }
        }

        public void ToggleSelect(Chan c)
        {
            if (c.Selected) Deselect(c); else Select(c);
        }

        public void ToggleMute(Chan c)
        {
            c.Muted = !c.Muted;
            AddLog($"{c.Cfg.Name}: {(c.Muted ? "muted" : "unmuted")}");
            MarkDirty();
        }

        public void ToggleGlobalMute()
        {
            globalMute = !globalMute;
            AddLog(globalMute ? "all receive audio muted" : "receive audio unmuted");
            MarkDirty();
        }

        public void Louder(Chan c)
        {
            c.Gain = Math.Min(8.0, c.Gain * 1.25);
            MarkDirty();
        }

        public void Quieter(Chan c)
        {
            c.Gain = Math.Max(0.25, c.Gain / 1.25);
            MarkDirty();
        }

        public void ToggleTalkPermit()
        {
            talkPermit = !talkPermit;
            AddLog(talkPermit ? "talk permit tone on" : "talk permit tone off");
            MarkDirty();
        }

        /// <summary>Talk permit tone on (self-test hook).</summary>
        public bool TalkPermit => talkPermit;

        /// <summary>
        /// The Windows console's talk permit tone: 50 ms of 1200 Hz at 20 % with 10 ms fades.
        /// </summary>
        public static short[] TalkPermitPcm()
        {
            const int rate = 8000, samples = rate * 50 / 1000, fade = rate * 10 / 1000;
            short[] pcm = new short[samples];
            for (int i = 0; i < samples; i++)
            {
                double env = i < fade ? (double)i / fade : i >= samples - fade ? (double)(samples - i - 1) / fade : 1.0;
                pcm[i] = (short)(Math.Sin(2 * Math.PI * 1200.0 * i / rate) * 0.20 * env * short.MaxValue);
            }
            return pcm;
        }

        public void RequestKey(Chan c)
        {
            if (c.Encrypted && KeyStore.IsSupported(c.KeyRequestAlg))
            {
                if (!c.Link.Connected)
                    AddLog($"{c.Cfg.Name}: not connected to {(links.Count > 1 ? c.Link.Name : "the FNE")} yet; its key is asked for once the console has logged in");
                else
                    c.Link.Peer.RequestKey(c.KeyRequestAlg, c.KeyId);
            }
            else
                AddLog($"{c.Cfg.Name}: clear channel, no key needed");
        }

        /*
        ** Transmit
        */

        public void ToggleTx()
        {
            if (toneJob != null)
            {
                CancelTones();
                return;
            }
            if (tx != null && tx.IsTransmitting)
            {
                StopTx(null);
                return;
            }

            StartTx();
        }

        /// <summary>
        /// Starts transmitting on the focused channel. Returns null on success, or the reason
        /// it didn't (also written to the log).
        /// </summary>
        internal string StartTx(Chan target = null)
        {
            string Refuse(string why)
            {
                AddLog(why);
                return why;
            }

            Chan c = target ?? chans[cursor];
            if (!c.IsP25)
                return Refuse($"{c.Cfg.Name}: can't transmit on a {c.Cfg.Mode} channel");
            if (c.Cfg.RxOnly)
                return Refuse($"{c.Cfg.Name}: receive-only channel");
            if (!c.Link.Connected)
                return Refuse(links.Count > 1 ? $"not connected to {c.Link.Name} yet" : "not connected to the FNE yet");
            if (PatchTransmitText(c) != null)
                return Refuse($"{c.Cfg.Name}: a patch is transmitting here");

            Select(c);

            P25Transmitter t = new P25Transmitter(c.Link.Peer, c.Link.Keys, c.Link.ConsoleRid, c.Tg, c.ActiveTxAlg, c.ActiveKeyId);
            if (!t.Start(out string error))
            {
                if (c.TxSecure && KeyStore.IsSupported(c.KeyRequestAlg))
                    c.Link.Peer.RequestKey(c.KeyRequestAlg, c.KeyId);
                return Refuse($"{c.Cfg.Name}: not transmitting: {error}");
            }

            if (!OpenMic(out string micError))
            {
                t.Stop();
                return Refuse($"{c.Cfg.Name}: no microphone ({micError})");
            }

            tx = t;
            txChan = c;
            StartPatchTx(c, t);     // the rest of its patch hears the console too
            RecallTxStarted(c, t);
            txFeed = t;
            if (talkPermit)
                c.Sink?.Write(TalkPermitPcm());     // played locally once the call has started, as the Windows console does
            AddLog($"{c.Cfg.Name}: TX start as {c.Link.ConsoleRid}{(t.Encrypted ? $" ({KeyStore.AlgorithmName(t.AlgorithmId)} key 0x{t.KeyId:X4})" : " (clear)")}");
            return null;
        }

        /// <summary>Starts transmitting on a channel without moving the focus to it, or stops the transmission in progress.</summary>
        public void ToggleTxOn(ConsoleChannel c)
        {
            if (toneJob != null)
            {
                CancelTones();
                return;
            }
            if (Transmitting)
            {
                StopTx(null);
                return;
            }
            StartTx(c);
        }

        /// <summary>
        /// Hold-to-talk on a channel without moving the focus to it: true when its button is
        /// pressed, false when it's let go.
        /// </summary>
        public void HoldToTalkOn(ConsoleChannel c, bool down)
        {
            if (!down)
            {
                OnDevicePtt(false);
                return;
            }
            if (toneJob != null)
            {
                AddLog("tones are being sent; PTT ignored");
                return;
            }
            if (Transmitting)
            {
                deviceHold = txChan == c;       // letting go ends a transmission on this channel only
                if (txChan != c)
                    AddLog($"{c.Cfg.Name}: already transmitting on {txChan?.Cfg.Name}");
                return;
            }
            deviceHold = StartTx(c) == null;
        }

        /// <summary>
        /// Switches a channel's transmit between secure and clear, when the codeplug allows it
        /// (selectable_encryption). Receive is not affected.
        /// </summary>
        public void ToggleTxSecure(ConsoleChannel c)
        {
            if (!c.Selectable)
            {
                AddLog(c.Encrypted ? $"{c.Cfg.Name}: the codeplug doesn't allow switching this channel to clear"
                                   : $"{c.Cfg.Name}: this channel has no encryption to switch");
                return;
            }
            if ((txChan == c && Transmitting) || IsSendingTone(c))
            {
                AddLog($"{c.Cfg.Name}: can't change encryption while transmitting");
                return;
            }
            c.TxClear = !c.TxClear;
            AddLog(c.TxClear ? $"{c.Cfg.Name}: transmit is now CLEAR (not encrypted)"
                             : $"{c.Cfg.Name}: transmit is {KeyStore.AlgorithmName(c.TxAlg)} again");
            MarkDirty();
        }

        public void StopTx(string reason)
        {
            P25Transmitter t = tx;
            if (t == null)
                return;

            txFeed = null;      // stop feeding mic audio before the call ends
            StopPatchTx();

            double secs = (DateTime.Now - t.StartedAt).TotalSeconds;
            bool was = t.IsTransmitting;
            t.Stop();
            RecallTxStopped(txChan);
            if (was)
            {
                AddLog($"{txChan?.Cfg.Name}: TX end, {secs:F1} s, {t.FramesSent} frames{(reason != null ? $" ({reason})" : string.Empty)}");
                AddRecent(txChan?.Cfg.Name, (txChan?.Link.Aliases ?? aliases).Label(t.SourceId), $"{secs:F0} s, {(t.Encrypted ? KeyStore.AlgorithmName(t.AlgorithmId) : "clear")}", tx: true);
            }

            tx = null;
            txChan = null;
            CloseMicIfUnused();

            deviceHold = false;

            // a "ptt hold" client exits when its transmission ends, however it ended
            ControlSession held = holdSession;
            holdSession = null;
            held?.Close();
        }

        /*
        ** Control socket (sidetone-cli ptt)
        */

        /// <summary>Control socket path, or null for none.</summary>
        public string ControlSocketPath { get; set; }

        internal ControlServer control;
        internal ControlSession holdSession;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<(Func<string> Action, TaskCompletionSource<string> Done)> uiCalls = new();
        internal readonly AutoResetEvent wake = new AutoResetEvent(false);

        /// <summary>Raised (on any thread) when work is waiting for <see cref="RunControlCalls"/> or <see cref="Tick"/>.</summary>
        public event Action WorkQueued;

        /// <summary>
        /// A call started on a listening channel (with its source radio ID). Raised on the network thread:
        /// a handler must not touch console state (Android wakes the screen with it).
        /// </summary>
        public event Action<ConsoleChannel, uint> CallStarted;

        private void Wake()
        {
            wake.Set();
            WorkQueued?.Invoke();
        }
        internal int uiThreadId = -1;

        /// <summary>Marks the calling thread as the UI thread (Run does this; the self-test too).</summary>
        public void ClaimUiThread() => uiThreadId = Environment.CurrentManagedThreadId;

        /// <summary>Starts the control socket (self-test hook; Run calls it too).</summary>
        internal bool ControlRunning => control != null;

        internal void StartControl()
        {
            if (string.IsNullOrWhiteSpace(ControlSocketPath))
                return;
            try
            {
                control = new ControlServer(ControlSocketPath, HandleControl);
                control.Start();
                AddLog($"control: PTT socket at {ControlSocketPath} (sidetone-cli ptt down / up / toggle / hold)");
            }
            catch (Exception ex)
            {
                control = null;
                AddLog($"control: no PTT socket ({ex.Message}); start with --socket PATH to use another");
            }
        }

        /*
        ** Dedicated PTT device (desk mic, footswitch)
        */

        /// <summary>PTT device options: part of its name or /dev/input path, PTT button, mute button (null = defaults).</summary>
        public (string Device, string Button, string MuteButton)? PttDevice { get; set; }

        internal PttDeviceWatcher pttDevice;
        internal bool PttDeviceWatching => pttDevice != null;
        internal void StopPttDevice() { pttDevice?.Dispose(); pttDevice = null; }
        internal bool deviceHold;    // the device's PTT is holding the current transmission

        /// <summary>Queues work for the UI thread without waiting for it.</summary>
        internal void PostToUi(Action action)
        {
            uiCalls.Enqueue((() => { action(); return null; }, new TaskCompletionSource<string>()));
            Wake();
        }

        internal void StartPttDevice()
        {
            // (not a positional pattern: "string x" doesn't match null, and the buttons are usually null)
            if (PttDevice is not { } opts || string.IsNullOrWhiteSpace(opts.Device))
                return;
            PttDeviceWatcher w = new PttDeviceWatcher(opts.Device, opts.Button, opts.MuteButton);
            w.Status += msg => PostToUi(() => AddLog(msg));
            w.Ptt += down => PostToUi(() => OnDevicePtt(down));
            w.Mute += () => PostToUi(ToggleMicMute);
            pttDevice = w;
            w.Start();
        }

        /// <summary>
        /// Hold-to-talk from a front end's key or button: true when it's pressed, false when it's let go.
        /// Letting go only ends a transmission that a press is holding.
        /// </summary>
        public void HoldToTalk(bool down) => OnDevicePtt(down);

        /// <summary>PTT device pressed or released (UI thread).</summary>
        internal void OnDevicePtt(bool down)
        {
            if (down)
            {
                if (toneJob != null)
                {
                    AddLog("tones are being sent; PTT ignored");
                    return;
                }
                string reply = PttDown(null);       // a refusal is already in the log
                deviceHold = reply.StartsWith("ok", StringComparison.Ordinal);
                return;
            }

            if (deviceHold && Transmitting)
                StopTx("PTT released");
            deviceHold = false;
        }

        internal void StopControl()
        {
            ControlServer c = control;
            control = null;
            c?.Dispose();
        }

        /// <summary>Runs a control command on the UI thread and waits for its reply.</summary>
        internal string OnUiThread(Func<string> action)
        {
            if (Environment.CurrentManagedThreadId == uiThreadId)
                return action();    // already on the UI thread (the console closing a held session itself)
            TaskCompletionSource<string> done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            uiCalls.Enqueue((action, done));
            Wake();
            return done.Task.Wait(TimeSpan.FromSeconds(3)) ? done.Task.Result : "error: the console didn't answer";
        }

        /// <summary>Runs queued control commands (UI thread).</summary>
        public void RunControlCalls()
        {
            while (uiCalls.TryDequeue(out var call))
            {
                try { call.Done.TrySetResult(call.Action()); }
                catch (Exception ex) { call.Done.TrySetResult($"error: {ex.Message}"); }
            }
        }

        /// <summary>Handles one control command line (connection thread).</summary>
        internal string HandleControl(string line, ControlSession session)
        {
            string cmd = string.Join(' ', line.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            switch (cmd)
            {
                case "ptt down":
                    return OnUiThread(() => PttDown(null));
                case "ptt up":
                    return OnUiThread(PttUp);
                case "ptt toggle":
                    return OnUiThread(() => Transmitting ? PttUp() : PttDown(null));
                case "ptt hold":
                    session.Closed += () => OnUiThread(() => ReleaseHold(session));
                    return OnUiThread(() => PttDown(session));
                case "status":
                    return OnUiThread(ControlStatus);
                case "help":
                    return "ok: commands: ptt down, ptt up, ptt toggle, ptt hold, status, help";
                default:
                    return $"error: unknown command '{line}' (try 'help')";
            }
        }

        internal string PttDown(ControlSession session)
        {
            if (Transmitting)
            {
                if (session != null)
                    holdSession = session;      // releasing this hold now ends the transmission
                return $"ok: already transmitting on {txChan?.Cfg.Name}";
            }
            if (toneJob != null)
                return "error: tones are being sent; stop them first";
            string error = StartTx();
            if (error != null)
                return $"error: {error}";
            holdSession = session;
            return $"ok: transmitting on {txChan?.Cfg.Name} (tg {tx.Talkgroup})";
        }

        internal string PttUp()
        {
            if (!Transmitting)
                return "ok: not transmitting";
            string name = txChan?.Cfg.Name;
            StopTx(null);
            return $"ok: stopped transmitting on {name}";
        }

        internal string ReleaseHold(ControlSession session)
        {
            if (holdSession != session)
                return "ok";
            holdSession = null;
            if (Transmitting)
                StopTx("PTT released");
            return "ok";
        }

        internal string ControlStatus()
        {
            if (Transmitting)
                return $"ok: transmitting on {txChan?.Cfg.Name} (tg {tx.Talkgroup}) for {(DateTime.Now - tx.StartedAt).TotalSeconds:F0} s";
            Chan c = chans[cursor];
            string conn = Connected ? "connected" : links.Count > 1 && ConnectedCount > 0 ? $"{ConnectedCount} of {links.Count} systems connected" : "not connected";
            return $"ok: idle, {conn}, PTT would transmit on {c.Cfg.Name}{(c.IsP25 ? $" (tg {c.Tg})" : $" ({c.Cfg.Mode}, no transmit)")}";
        }

        public bool Transmitting => tx != null && tx.IsTransmitting;

        /*
        ** Microphone and level meter
        */

        /// <summary>Opens the microphone if it isn't open yet.</summary>
        internal bool OpenMic(out string error)
        {
            error = null;
            if (capture != null)
                return true;
            try
            {
                IAudioInput cap = AudioBackend.Current.OpenInput(Audio?.InputTarget);
                cap.Block += OnMicBlock;
                cap.Failed += msg => pendingMicFail = msg;
                capture = cap;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // capture thread
        internal void OnMicBlock(short[] samples)
        {
            micLevel.Push(samples);
            if (RmsDb(samples) > NoAudioDb)
                Interlocked.Exchange(ref lastVoiceTicks, DateTime.UtcNow.Ticks);
            P25Transmitter feed = txFeed;
            if (feed == null)
                return;
            short[] tx = MicToTx(samples);
            micProcessor?.Process(tx);
            feed.PushAudio(tx);
        }

        /*
        ** Audio processing
        */

        private volatile VoiceProcessor micProcessor;
        private bool rxLevelingWas;

        /// <summary>Turns all processing off for this run (a command line option), whatever the settings say.</summary>
        public bool ProcessingOff { get; set; }

        /// <summary>The microphone stages in effect.</summary>
        public VoiceProcessing MicProcessing
        {
            get
            {
                if (ProcessingOff)
                    return VoiceProcessing.Off;
                AppSettings s = Settings;
                VoiceProcessing d = VoiceProcessing.MicDefault;
                return new VoiceProcessing(s?.MicLowCut ?? d.LowCut, s?.MicAutoLevel ?? d.AutoLevel, s?.MicNoiseGate ?? d.NoiseGate, s?.MicLimiter ?? d.Limiter);
            }
        }

        /// <summary>Received audio is evened out between talkers.</summary>
        public bool RxLeveling => !ProcessingOff && Settings?.RxAutoLevel == true;

        /// <summary>Picks up changed processing settings (call after changing them).</summary>
        public void ReloadAudioProcessing(bool log = false)
        {
            VoiceProcessing mic = MicProcessing;
            bool rx = RxLeveling;
            bool changed = (micProcessor?.Stages ?? VoiceProcessing.Off) != mic || rx != rxLevelingWas;
            rxLevelingWas = rx;
            if ((micProcessor?.Stages ?? VoiceProcessing.Off) != mic)
                micProcessor = mic.Any ? new VoiceProcessor(mic) : null;
            foreach (Chan c in chans)
            {
                if (!rx)
                    c.RxLevel = null;
                else if (c.RxLevel == null)
                    c.RxLevel = new VoiceProcessor(VoiceProcessing.Receive);
            }
            if (log || changed)
                AddLog($"audio processing: microphone {mic}; receive leveling {(rx ? "on" : "off")}");
        }

        /*
        ** Mic mute and the no-audio warning
        */

        /// <summary>Below this (dBFS RMS) the microphone counts as silent for the no-audio warning.</summary>
        internal const double NoAudioDb = -55.0;
        /// <summary>Transmitting this long without audio above <see cref="NoAudioDb"/> raises the warning.</summary>
        internal static readonly TimeSpan NoAudioAfter = TimeSpan.FromSeconds(2);

        internal volatile bool micMuted;
        internal long lastVoiceTicks;
        internal bool noAudioWarned;

        public bool MicMuted => micMuted;
        public bool NoAudioWarning => noAudioWarned;

        /// <summary>What goes to the transmitter for one mic block: silence while the mic is muted.</summary>
        internal short[] MicToTx(short[] samples) => micMuted ? new short[samples.Length] : samples;

        internal static double RmsDb(short[] samples)
        {
            if (samples == null || samples.Length == 0)
                return LevelMeter.FloorDb;
            double sum = 0;
            foreach (short v in samples)
                sum += (double)v * v;
            return LevelMeter.ToDb(Math.Sqrt(sum / samples.Length));
        }

        /// <summary>Whether a transmission has gone long enough without microphone audio to warn.</summary>
        internal static bool NoAudioDue(DateTime nowUtc, DateTime txStartUtc, DateTime lastVoiceUtc, bool muted) =>
            !muted && nowUtc - (lastVoiceUtc > txStartUtc ? lastVoiceUtc : txStartUtc) > NoAudioAfter;

        public void ToggleMicMute()
        {
            micMuted = !micMuted;
            noAudioWarned = false;
            AddLog(micMuted ? "microphone muted (transmissions send silence; u or the mute button to unmute)" : "microphone unmuted");
        }

        /// <summary>Closes the microphone once neither transmit nor the meter needs it.</summary>
        internal void CloseMicIfUnused()
        {
            if (micMonitor || txFeed != null)
                return;
            IAudioInput cap = capture;
            capture = null;
            cap?.Dispose();
            micLevel.Reset();
        }

        /// <summary>Reloads the local alias file and, when FNE sync is on, downloads the list again.</summary>
        public void RefreshAliases()
        {
            foreach (FneLink link in links)
            {
                link.Aliases.LoadLocal();
                if (link.Aliases.SyncEnabled)
                    link.Peer?.SyncAliases();
            }
        }

        public void ToggleMicMonitor()
        {
            if (micMonitor)
            {
                micMonitor = false;
                CloseMicIfUnused();
                AddLog("mic meter off");
                return;
            }

            micMonitor = true;
            if (!NoAudio && !OpenMic(out string error))
            {
                micMonitor = false;
                AddLog($"mic meter: no microphone ({error})");
                return;
            }
            AddLog("mic meter on: the microphone stays open (nothing is transmitted); v turns it off");
        }

        public void Housekeeping()
        {
            CallHousekeeping();
            PatchHousekeeping();
            RecallHousekeeping();
            RecordingHousekeeping();
            HistoryHousekeeping();
            TimeAnnouncementHousekeeping();
            string micFail = pendingMicFail;
            if (micFail != null)
            {
                pendingMicFail = null;
                IAudioInput cap = capture;
                capture = null;
                cap?.Dispose();
                micLevel.Reset();
                if (micMonitor)
                {
                    micMonitor = false;
                    AddLog($"mic meter off: microphone stopped ({micFail})");
                }
                if (tx != null)
                    pendingTxStop = $"microphone: {micFail}";
            }

            string stop = pendingTxStop;
            if (stop != null)
            {
                pendingTxStop = null;
                StopTx(stop);
            }

            if (Transmitting && DateTime.Now - tx.StartedAt > txLimit)
                StopTx($"{txLimit.TotalSeconds:F0} s transmit limit reached");

            if (Transmitting && !noAudioWarned && !NoAudio &&
                NoAudioDue(DateTime.UtcNow, tx.StartedAt.ToUniversalTime(), new DateTime(Interlocked.Read(ref lastVoiceTicks), DateTimeKind.Utc), micMuted))
            {
                noAudioWarned = true;
                AddLog("no audio from the microphone: check that it's plugged in, not turned down, and is the input in use (sidetone-cli devices)");
            }
            else if (noAudioWarned && (!Transmitting || DateTime.UtcNow.Ticks - Interlocked.Read(ref lastVoiceTicks) < TimeSpan.TicksPerSecond / 2))
                noAudioWarned = false;

            if (settingsDirty && DateTime.Now - dirtyAt > TimeSpan.FromSeconds(1))
                SaveSettings();

            // channel marker: a beep on every idle listening channel, every markerInterval
            if (markerOn && DateTime.Now >= nextMarker)
            {
                nextMarker = DateTime.Now + markerInterval;
                if (toneJob == null && !Transmitting && links.Any(l => l.CanSend))
                {
                    List<Chan> idle = TxCapableListening().Where(c => !(c.Rx?.InCall ?? false) && !c.Active).ToList();
                    if (idle.Count > 0)
                        SendTone(TonePlan.Marker(), idle, quiet: true);
                }
            }
        }

        /*
        ** Remembered settings
        */

        /// <summary>A channel's remembered settings, kept under its own system.</summary>
        private bool ChannelSaved(Chan c, out ChannelSettings saved) => settings.For(c.Link.Name).Channels.TryGetValue(ChanKey(c), out saved);

        internal static string ChanKey(Chan c) => $"{c.Zone}/{c.Cfg.Name}/{(c.Tg != 0 ? c.Tg.ToString() : c.Cfg.Tgid)}";

        internal void MarkDirty()
        {
            settingsDirty = true;
            dirtyAt = DateTime.Now;
        }

        /// <summary>
        /// Applies the remembered settings for this system: listening channels, volumes, mutes, focus, last page values
        /// (internal for the self-test). The channel marker is never switched on automatically.
        /// </summary>
        internal void RestoreSettings()
        {
            SystemSettings ss = settings.For(system.Name);
            globalMute = ss.GlobalMute;
            lastPageA = ss.PageA ?? string.Empty;
            lastPageB = ss.PageB ?? string.Empty;
            lastPageRid = ss.PageRid ?? string.Empty;
            RestoreHiddenZones(ss);
            RestoreStreamVolumes(ss);
            foreach (ConsoleZone z in zones)
                z.Output = ss.ZoneOutputs != null && ss.ZoneOutputs.TryGetValue(z.Name, out string zoneOut) && !string.IsNullOrWhiteSpace(zoneOut) ? zoneOut : null;
            foreach (Chan c in chans)
                c.Output = ChannelSaved(c, out ChannelSettings saved) && !string.IsNullOrWhiteSpace(saved.Output) ? saved.Output : null;
            CheckSavedOutputs();

            List<string> restored = new List<string>();
            for (int i = 0; i < chans.Count; i++)
            {
                Chan c = chans[i];
                string key = ChanKey(c);
                if (key == ss.Focused && (ss.FocusedSystem == null || string.Equals(ss.FocusedSystem, c.Link.Name, StringComparison.OrdinalIgnoreCase)))
                    cursor = i;
                if (!ChannelSaved(c, out ChannelSettings cs))
                    continue;
                c.Muted = cs.Muted;
                c.TxClear = cs.TxClear && c.Selectable;
                c.Record = cs.Record && cs.Listening && c.IsP25;
                c.Gain = Math.Clamp(cs.Gain, 0.25, 8.0);
                if (c.Hidden)
                    c.Record = false;
                if (cs.Listening && c.IsP25 && !c.Hidden)
                {
                    Select(c);
                    restored.Add(c.Cfg.Name + (c.Record ? " (recording)" : string.Empty));
                }
            }
            if (restored.Count > 0)
                AddLog($"restored: listening on {string.Join(", ", restored)}");
            SettleFocus();
            settingsDirty = false;
        }

        /// <summary>
        /// Writes the current settings to the settings file (internal for the self-test).
        /// </summary>
        internal void SaveSettings()
        {
            settingsDirty = false;
            if (settingsPath == null)
                return;

            SystemSettings ss = settings.For(system.Name);
            settings.TalkPermitTone = talkPermit;
            ss.GlobalMute = globalMute;
            ss.PageA = lastPageA;
            ss.PageB = lastPageB;
            ss.PageRid = lastPageRid;
            ss.Focused = ChanKey(chans[cursor]);
            ss.FocusedSystem = links.Count > 1 ? chans[cursor].Link.Name : null;
            ss.HiddenZones = zones.Where(z => z.Hidden).Select(z => z.Name).ToList();
            if (ss.HiddenZones.Count == 0)
                ss.HiddenZones = null;
            foreach (Chan c in chans)
                settings.For(c.Link.Name).Channels[ChanKey(c)] = new ChannelSettings { Listening = c.Selected, Muted = c.Muted, Gain = Math.Round(c.Gain, 3), TxClear = c.Selectable && c.TxClear, Record = c.Record, Output = c.Output };
            ss.ZoneOutputs = zones.Any(z => z.Output != null) ? zones.Where(z => z.Output != null).ToDictionary(z => z.Name, z => z.Output) : null;
            SaveStreamVolumes(ss);

            try { settings.Save(settingsPath); }
            catch (Exception ex) { AddLog($"could not save settings to {settingsPath}: {ex.Message}"); }
        }

        /*
        ** Radio commands: check, inhibit, uninhibit (one box: radio ID, action, send)
        */

        public static readonly (string Label, ExtendedFunction Function)[] RadioActions =
        {
            ("Check", ExtendedFunction.CHECK),
            ("Inhibit", ExtendedFunction.INHIBIT),
            ("Uninhibit", ExtendedFunction.UNINHIBIT)
        };

        /// <summary>The system radio commands and digital pages go to: the focused channel's.</summary>
        public FneLink CommandLink => chans[cursor].Link;

        private string NotConnectedText(FneLink link) => links.Count > 1 ? $"not connected to {link.Name} yet" : "not connected to the FNE yet";

        /// <summary>
        /// Sends a radio command (an index into <see cref="RadioActions"/>) to one radio.
        /// Returns null when it was sent, or why it wasn't.
        /// </summary>
        public string SendRadioCommand(int action, uint rid)
        {
            (string label, ExtendedFunction fn) = RadioActions[action];
            FneLink link = CommandLink;
            RadioAliasBook aliases = link.Aliases;
            string error = NotConnectedText(link);
            if (link.Peer == null || !link.Peer.SendExtendedFunction(fn, rid, out error))
                return error ?? "the command wasn't sent";

            lastPageRid = rid.ToString();
            MarkDirty();
            commanded[rid] = DateTime.Now;
            AddLog($"radio {label.ToLowerInvariant()} sent to {aliases.Describe(rid)}");
            AddRecent("radio", rid.ToString(), label.ToLowerInvariant(), tx: true);
            return null;
        }

        /// <summary>
        /// Sends a digital page (P25 call alert) to one radio. Returns null when it was sent, or why it wasn't.
        /// </summary>
        public string SendDigitalPage(uint rid)
        {
            FneLink link = CommandLink;
            RadioAliasBook aliases = link.Aliases;
            string error = NotConnectedText(link);
            if (link.Peer == null || !link.Peer.SendCallAlert(rid, out error))
                return error ?? "the page wasn't sent";

            lastPageRid = rid.ToString();
            MarkDirty();
            AddLog($"digital page (call alert) sent to {aliases.Describe(rid)}");
            AddRecent("call alert", aliases.Label(rid), "digital page", tx: true);
            return null;
        }

        /*
        ** Tones
        */

        internal List<Chan> TxCapableListening() =>
            chans.Where(c => c.Selected && c.IsP25 && !c.Cfg.RxOnly && (links.Count == 1 || c.Link.CanSend)).ToList();

        /// <summary>
        /// Sends alert tone 1-5 on every listening channel (internal for the self-test).
        /// </summary>
        public void SendAlert(int number) => SendManual(TonePlan.Alert(number));

        /// <summary>
        /// Sends a QuickCall II page on every listening channel (internal for the self-test).
        /// </summary>
        public void SendPage(double aHz, double bHz) => SendManual(TonePlan.Page(aHz, bHz));

        /// <summary>
        /// Why an alert tone or page can't be sent right now, or null when it can.
        /// </summary>
        public string ToneBlocker()
        {
            if (toneJob != null)
                return $"a tone is already being sent ({toneJob.Plan.Name}); Esc stops it";
            if (Transmitting)
                return "stop transmitting before sending a tone";
            if (!links.Any(l => l.CanSend))
                return "not connected to the FNE yet";
            if (TxCapableListening().Count == 0)
                return "no listening channels to send the tone on (click a card to listen)";
            return null;
        }

        internal void SendManual(TonePlan plan)
        {
            string why = ToneBlocker();
            if (why != null)
            {
                AddLog(why);
                return;
            }
            SendTone(plan, TxCapableListening(), quiet: false);
        }

        /// <summary>
        /// Sends a QuickCall II page from tone frequencies as typed (Hz). Returns null when it was
        /// started, or what is wrong; the tones are remembered for next time.
        /// </summary>
        public string SendTonePage(string aText, string bText)
        {
            aText = (aText ?? string.Empty).Trim();
            bText = (bText ?? string.Empty).Trim();
            bool okA = double.TryParse(aText, NumberStyles.Float, CultureInfo.InvariantCulture, out double a);
            bool okB = double.TryParse(bText, NumberStyles.Float, CultureInfo.InvariantCulture, out double b);
            if (!okA || a < TonePlan.MinPageHz || a > TonePlan.MaxPageHz)
                return $"tone A must be {TonePlan.MinPageHz:F0}-{TonePlan.MaxPageHz:F0} Hz";
            if (!okB || b < TonePlan.MinPageHz || b > TonePlan.MaxPageHz)
                return $"tone B must be {TonePlan.MinPageHz:F0}-{TonePlan.MaxPageHz:F0} Hz";

            string why = ToneBlocker();
            if (why != null)
                return why;

            lastPageA = aText;
            lastPageB = bText;
            MarkDirty();
            SendPage(a, b);
            return null;
        }

        /// <summary>A radio ID as typed: 1 to 16777215.</summary>
        public static bool TryParseRid(string text, out uint rid) =>
            uint.TryParse((text ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out rid) && rid != 0 && rid <= 0xFFFFFF;

        /// <summary>Whether a radio command (an index into <see cref="RadioActions"/>) should be confirmed before it's sent.</summary>
        public static bool RadioActionNeedsConfirm(int action) => RadioActions[action].Function == ExtendedFunction.INHIBIT;

        /// <summary>The last page's tones and radio ID, as typed (remembered between runs).</summary>
        public string LastPageA => lastPageA;
        public string LastPageB => lastPageB;
        public string LastPageRid => lastPageRid;

        // the last page's values, remembered between runs
        internal string lastPageA = string.Empty, lastPageB = string.Empty, lastPageRid = string.Empty;

        // radios sent a command recently (radio ID -> time), so their replies are reported
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<uint, DateTime> commanded = new();

        /// <summary>
        /// Reports control messages that concern this console (internal for the self-test). Octets of a TSBK: 0 opcode,
        /// 1 MFID, then the arguments; for an extended function: 2-3 function, 4-6 argument, 7-9 target radio.
        /// </summary>
        internal void OnP25Control(byte lco, uint src, uint dst, byte[] tsbk) => OnP25Control(links[0], lco, src, dst, tsbk);

        internal void OnP25Control(FneLink link, byte lco, uint src, uint dst, byte[] tsbk)
        {
            uint me = link.ConsoleRid;
            RadioAliasBook aliases = link.Aliases;
            switch (lco)
            {
                case P25Defines.TSBK_IOSP_CALL_ALRT:
                    if (dst == me && me != 0)
                    {
                        AddLog($"call alert (page) from {aliases.Describe(src)}");
                        AddRecent("call alert", aliases.Label(src), "paged this console", tx: false);
                        NotifyPaged(link, src);
                    }
                    break;

                case P25Defines.TSBK_IOSP_ACK_RSP:
                    if (dst == me && me != 0)
                        AddLog($"acknowledged by {aliases.Describe(src)}");
                    break;

                case P25Defines.TSBK_IOSP_EXT_FNCT:
                {
                    if (tsbk == null)
                        break;
                    ushort fn = (ushort)(tsbk[2] << 8 | tsbk[3]);
                    uint arg = (uint)(tsbk[4] << 16 | tsbk[5] << 8 | tsbk[6]);
                    uint target = (uint)(tsbk[7] << 16 | tsbk[8] << 8 | tsbk[9]);
                    string what = fn switch
                    {
                        (ushort)ExtendedFunction.CHECK_ACK => "answered the radio check",
                        (ushort)ExtendedFunction.INHIBIT_ACK => "confirmed it is inhibited",
                        (ushort)ExtendedFunction.UNINHIBIT_ACK => "confirmed it is uninhibited",
                        _ => null
                    };
                    if (what == null)
                        break;      // our own command coming back, or a function we don't report

                    // a reply to us: argument is our RID (check) or the radio was commanded from here recently
                    bool recent = commanded.TryGetValue(target, out DateTime when) && DateTime.Now - when < TimeSpan.FromMinutes(2);
                    if ((arg == me && me != 0) || recent)
                    {
                        AddLog($"radio {aliases.Describe(target)} {what}");
                        AddRecent("radio", aliases.Label(target), what.Replace("confirmed it is ", string.Empty).Replace("answered the ", string.Empty), tx: false);
                    }
                    break;
                }
            }
        }

        /// <summary>Console RID used by the self-test when there is no peer (the primary system's).</summary>
        internal uint consoleRidForTest { get => links[0].RidForTest; set => links[0].RidForTest = value; }

        internal void SendTone(TonePlan plan, List<Chan> targets, bool quiet)
        {
            List<(string, P25Transmitter)> started = new List<(string, P25Transmitter)>();
            HashSet<Chan> sending = new HashSet<Chan>();
            foreach (Chan c in targets)
            {
                P25Transmitter t = new P25Transmitter(c.Link.Sink, c.Link.Keys, c.Link.ConsoleRid, c.Tg, c.ActiveTxAlg, c.ActiveKeyId);
                if (t.Start(out string error))
                {
                    started.Add((c.Cfg.Name, t));
                    sending.Add(c);
                }
                else
                {
                    AddLog($"{c.Cfg.Name}: no {plan.Name}: {error}");
                    if (c.TxSecure && KeyStore.IsSupported(c.KeyRequestAlg))
                        c.Link.Peer?.RequestKey(c.KeyRequestAlg, c.KeyId);
                }
            }
            if (started.Count == 0)
                return;

            // monitor locally on one channel's speaker (not every one), unless muted
            Chan monitorChan = sending.FirstOrDefault(c => c.Sink != null && !c.Muted);
            Action<short[]> monitor = monitorChan == null ? null : pcm =>
            {
                if (!globalMute && !monitorChan.Muted)
                    monitorChan.Sink?.Write(pcm);
            };

            ToneJob job = new ToneJob(plan, started, monitor);
            job.Finished += j =>
            {
                if (!quiet || j.Cancelled)
                    AddLog($"{plan.Name} {(j.Cancelled ? "stopped" : "sent")} on {string.Join(", ", j.Channels)}");
                if (!quiet)
                    foreach (string name in j.Channels)
                        AddRecent(name, $"{chans.FirstOrDefault(x => x.Cfg.Name == name)?.Link.ConsoleRid ?? ConsoleRid}", plan.Name, tx: true);
                toneChans = new HashSet<Chan>();
                toneJob = null;
            };

            toneChans = sending;
            toneJob = job;
            if (!quiet)
                AddLog($"sending {plan.Name} on {string.Join(", ", started.Select(t => t.Item1))}");
            job.Start();
        }

        public void CancelTones(bool wait = false)
        {
            StopPreview();
            ToneJob job = toneJob;
            if (job == null)
                return;
            job.Cancel();
            for (int i = 0; wait && job.Running && i < 50; i++)
                Thread.Sleep(20);
        }

        public void ToggleMarker()
        {
            markerOn = !markerOn;
            nextMarker = DateTime.Now;     // first beep straight away
            AddLog(markerOn ? $"channel marker on: a {TonePlan.MarkerHz:F0} Hz beep every {markerInterval.TotalSeconds:F0} s on idle listening channels"
                            : "channel marker off");
        }

        /// <summary>Channel marker on (self-test hook).</summary>
        public bool MarkerOn => markerOn;

        public void AddRecent(string chan, string who, string detail, bool tx)
        {
            lock (uiLock)
            {
                recent.AddFirst((DateTime.Now, chan ?? "?", who, detail, tx));
                while (recent.Count > MaxRecentCalls)
                    recent.RemoveLast();
                recentVersion++;
            }
            HistoryChanged();
        }

        /*
        ** Log
        */

        /// <summary>Called, under the log lock, after a line is added (the terminal keeps its scroll position).</summary>
        protected virtual void OnLogAdded() { }

        /// <summary>Adds a line to the log.</summary>
        public void AddLog(string message)
        {
            message = (message ?? string.Empty).Replace("(SIDETONE) ", string.Empty);
            string line = $"{DateTime.Now:HH:mm:ss} {message}";
            lock (uiLock)
            {
                log.AddLast(line);
                while (log.Count > MaxLogLines)
                    log.RemoveFirst();
                logVersion++;
                OnLogAdded();
            }

            string file = LogFilePath;
            if (file != null)
            {
                lock (logFileLock)
                {
                    try
                    {
                        if (file != logFileMade)
                        {
                            string dir = Path.GetDirectoryName(file);
                            if (!string.IsNullOrEmpty(dir))
                                Directory.CreateDirectory(dir);
                            logFileMade = file;
                        }
                        File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
                    }
                    catch { /* the log on screen still has it */ }
                }
            }
        }

        private readonly object logFileLock = new object();
        private string logFileMade;

        /// <summary>
        /// Folder for a log file per day (<see cref="LogFiles"/>), or null for none. A log file given
        /// to the constructor (--log FILE) wins over it.
        /// </summary>
        public string LogFolder { get; set; }

        /// <summary>The file log lines are being written to right now, or null.</summary>
        public string LogFilePath => logFile ?? (LogFolder != null ? LogFiles.FileFor(LogFolder, DateTime.Now) : null);
    } // public class DispatchConsole
}
