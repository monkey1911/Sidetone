// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Call history that survives closing the console: the Recent calls list and instant recall's
* last ten calls per channel (with their voice, so Replay and Recall work on calls from before a
* restart). It goes to one file per system, ~/.local/state/sidetone/history/SYSTEM.json, readable
* only by the user, written a minute after calls come in and when the console closes.
*
* Secure calls' voice is held decrypted, so it is only saved when Settings says so (off by
* default); their Recent calls entries are always saved. Turning the history off deletes the file.
* The Windows console keeps its call history in memory only.
*/

using System.Text.Json;
using Chan = Sidetone.Core.ConsoleChannel;

namespace Sidetone.Core
{
    public partial class DispatchConsole
    {
        /// <summary>The most voice a history file holds (the oldest calls are left out past it).</summary>
        private const long MaxHistoryAudioBytes = 20L * 1024 * 1024;
        /// <summary>How long after a call the history is saved.</summary>
        private static readonly TimeSpan HistorySaveDelay = TimeSpan.FromMinutes(1);

        /// <summary>Folder for the history files, or null for none (the demo, tests, --no-settings).</summary>
        public string HistoryFolder { get; set; }

        /// <summary>The default history folder: ~/.local/state/sidetone/history.</summary>
        public static string DefaultHistoryFolder => Path.Combine(Path.GetDirectoryName(LogFiles.DefaultFolder), "history");

        /// <summary>This system's history file, or null.</summary>
        public string HistoryPath => HistoryFolder == null ? null : Path.Combine(HistoryFolder, SafeName(system.Name) + ".json");

        /// <summary>Calls are kept between runs (Settings; on unless turned off).</summary>
        public bool KeepHistory => Settings?.KeepHistory != false;

        /// <summary>Secure calls' voice is kept between runs too (Settings; off unless turned on).</summary>
        public bool KeepSecureHistory => Settings?.HistoryKeepSecure == true;

        private volatile bool historyDirty;
        private DateTime historyDue;
        private readonly object historyFileLock = new object();

        private static string SafeName(string text) => string.Concat((text ?? "system").Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '.' ? ch : '_'));

        /// <summary>Something new for the history: it is saved a little later.</summary>
        private void HistoryChanged()
        {
            if (historyDirty)
                return;
            historyDirty = true;
            historyDue = DateTime.Now + HistorySaveDelay;
        }

        private void HistoryHousekeeping()
        {
            if (historyDirty && DateTime.Now >= historyDue)
                SaveHistory(background: true);
        }

        /*
        ** The file
        */

        private sealed class SavedHistory
        {
            public int Version { get; set; } = 1;
            public List<SavedRecent> Recent { get; set; } = new List<SavedRecent>();
            public List<SavedCall> Calls { get; set; } = new List<SavedCall>();
        }

        private sealed class SavedRecent
        {
            public DateTime When { get; set; }
            public string Chan { get; set; }
            public string Who { get; set; }
            public string Detail { get; set; }
            public bool Tx { get; set; }
        }

        private sealed class SavedCall
        {
            /// <summary>The channel, as remembered settings name it ("zone/name/tg").</summary>
            public string Key { get; set; }
            public string Channel { get; set; }
            public uint Talkgroup { get; set; }
            public uint Source { get; set; }
            public string Who { get; set; }
            public DateTime Started { get; set; }
            public bool Tx { get; set; }
            public string Crypt { get; set; }
            public bool Cut { get; set; }
            /// <summary>The clear IMBE codewords, 11 bytes each, one after another, in base64.</summary>
            public string Frames { get; set; }
        }

        private static readonly JsonSerializerOptions HistoryJson = new JsonSerializerOptions { WriteIndented = false };

        /// <summary>
        /// Writes the history file now (or in the background). Secure calls' voice is left out unless
        /// it is to be kept; nothing is written when history is off.
        /// </summary>
        public void SaveHistory(bool background = false)
        {
            historyDirty = false;
            string path = HistoryPath;
            if (path == null || !KeepHistory)
                return;

            SavedHistory snapshot = new SavedHistory();
            lock (uiLock)
                snapshot.Recent = recent.Select(r => new SavedRecent { When = r.When, Chan = r.Chan, Who = r.Who, Detail = r.Detail, Tx = r.Tx }).ToList();
            bool secureToo = KeepSecureHistory;
            long bytes = 0;
            lock (recallLock)
            {
                // newest calls first across all channels, so a full file drops the oldest
                foreach ((Chan c, RecalledCall call) in recalled.SelectMany(kv => kv.Value.Select(call => (kv.Key, call))).OrderByDescending(x => x.call.Started))
                {
                    if (!secureToo && call.Crypt != "clear")
                        continue;
                    long size = (long)call.Frames.Count * P25VoiceReceiver.ImbeLength;
                    if (bytes + size > MaxHistoryAudioBytes)
                        continue;
                    bytes += size;
                    byte[] frames = new byte[size];
                    for (int i = 0; i < call.Frames.Count; i++)
                        Buffer.BlockCopy(call.Frames[i], 0, frames, i * P25VoiceReceiver.ImbeLength, P25VoiceReceiver.ImbeLength);
                    snapshot.Calls.Add(new SavedCall
                    {
                        Key = ChanKey(c), Channel = call.Channel, Talkgroup = call.Talkgroup, Source = call.Source, Who = call.Who,
                        Started = call.Started, Tx = call.Tx, Crypt = call.Crypt, Cut = call.Cut, Frames = Convert.ToBase64String(frames)
                    });
                }
            }

            void Write()
            {
                lock (historyFileLock)
                {
                    try
                    {
                        MakePrivateFolder(HistoryFolder);
                        string partial = path + ".part";
                        using (FileStream f = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            if (!OperatingSystem.IsWindows())
                                File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                            JsonSerializer.Serialize(f, snapshot, HistoryJson);
                        }
                        File.Move(partial, path, overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        AddLog($"warning: the call history couldn't be saved ({ex.Message})");
                    }
                }
            }

            if (background)
                Task.Run(Write);
            else
                Write();
        }

        /// <summary>
        /// Reads this system's history at start: the Recent calls list and each channel's kept calls.
        /// Calls on channels that are no longer in the codeplug are left out.
        /// </summary>
        internal void LoadHistory()
        {
            string path = HistoryPath;
            if (path == null)
                return;
            if (!KeepHistory)
            {
                DeleteHistory();
                return;
            }
            if (!File.Exists(path))
                return;

            SavedHistory saved;
            try
            {
                using FileStream f = File.OpenRead(path);
                saved = JsonSerializer.Deserialize<SavedHistory>(f, HistoryJson);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                AddLog($"warning: the call history couldn't be read ({ex.Message}); starting without it");
                return;
            }
            if (saved == null)
                return;

            lock (uiLock)
            {
                // the file's newest first, after anything already in this run's list
                foreach (SavedRecent r in saved.Recent ?? new List<SavedRecent>())
                {
                    if (recent.Count >= MaxRecentCalls)
                        break;
                    recent.AddLast((r.When, r.Chan ?? "?", r.Who, r.Detail, r.Tx));
                }
                recentVersion++;
            }

            Dictionary<string, Chan> byKey = chans.GroupBy(ChanKey).ToDictionary(g => g.Key, g => g.First());
            int calls = 0;
            HashSet<Chan> channels = new HashSet<Chan>();
            lock (recallLock)
            {
                foreach (SavedCall s in (saved.Calls ?? new List<SavedCall>()).OrderByDescending(s => s.Started))
                {
                    if (s.Key == null || !byKey.TryGetValue(s.Key, out Chan c) || string.IsNullOrEmpty(s.Frames))
                        continue;
                    byte[] frames;
                    try { frames = Convert.FromBase64String(s.Frames); }
                    catch (FormatException) { continue; }
                    if (frames.Length == 0 || frames.Length % P25VoiceReceiver.ImbeLength != 0)
                        continue;
                    RecalledCall call = new RecalledCall
                    {
                        Channel = s.Channel ?? c.Cfg.Name, Talkgroup = s.Talkgroup, Source = s.Source, Who = s.Who,
                        Started = s.Started, Tx = s.Tx, Crypt = s.Crypt ?? "clear", Cut = s.Cut
                    };
                    for (int i = 0; i < frames.Length; i += P25VoiceReceiver.ImbeLength)
                        call.Frames.Add(frames.AsSpan(i, P25VoiceReceiver.ImbeLength).ToArray());
                    if (!recalled.TryGetValue(c, out LinkedList<RecalledCall> list))
                        recalled[c] = list = new LinkedList<RecalledCall>();
                    if (list.Count >= RecallPerChannel)
                        continue;
                    list.AddLast(call);     // newest first, so older ones go after
                    calls++;
                    channels.Add(c);
                }
                recallVersion++;
            }
            if (calls > 0 || (saved.Recent?.Count ?? 0) > 0)
                AddLog($"history: {saved.Recent?.Count ?? 0} recent calls and {calls} calls to replay on {channels.Count} channels from before");
        }

        /// <summary>Deletes this system's history file (history was turned off).</summary>
        public void DeleteHistory()
        {
            string path = HistoryPath;
            if (path == null || !File.Exists(path))
                return;
            lock (historyFileLock)
            {
                try
                {
                    File.Delete(path);
                    AddLog("history: off, so the saved call history was deleted");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    AddLog($"warning: the saved call history couldn't be deleted ({ex.Message})");
                }
            }
        }

        /// <summary>After the history settings change: delete the file (off), or write it again (what's kept changed).</summary>
        public void HistorySettingsChanged()
        {
            if (!KeepHistory)
                DeleteHistory();
            else
                SaveHistory(background: true);
        }
    } // public partial class DispatchConsole
}
