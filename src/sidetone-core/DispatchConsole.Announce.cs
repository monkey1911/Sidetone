// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Announcements: WAV files played onto talkgroups, like the Windows console's custom alert tones.
* A list of files is remembered (Settings); each can go out on the focused channel or on every
* listening channel, like the alert tones, and is sent the same way: a lead-in for the grant, 20 ms
* voice frames at a steady pace, Esc to stop. Any common WAV format is accepted and converted to
* 8 kHz mono (the Windows console takes only 8 kHz mono 16-bit), and the level is evened out.
*/

using Chan = Sidetone.Core.ConsoleChannel;

namespace Sidetone.Core
{
    public partial class DispatchConsole
    {
        /// <summary>Converted audio, by file and its last write time, so a file is only converted once.</summary>
        private readonly Dictionary<string, (DateTime Written, short[] Samples)> announcementCache = new Dictionary<string, (DateTime, short[])>(StringComparer.Ordinal);

        /// <summary>The level choices for announcements, in dB compared with the microphone's.</summary>
        public static readonly int[] AnnouncementLevels = { -9, -6, -3, 0, 3 };

        /// <summary>A level's name, like "3 dB quieter".</summary>
        public static string AnnouncementLevelName(int db) =>
            db == 0 ? "Same as the microphone" : db < 0 ? $"{-db} dB quieter" : $"{db} dB louder";

        /// <summary>How loud announcements go out, in dB compared with the microphone's level.</summary>
        public int AnnouncementLevel => settings?.AnnouncementLevelDb is int db && AnnouncementLevels.Contains(db) ? db : 0;

        /// <summary>Sets the announcements' level (one of <see cref="AnnouncementLevels"/>). Returns null, or why not.</summary>
        public string SetAnnouncementLevel(int db)
        {
            if (!AnnouncementLevels.Contains(db))
                return $"the level can be {string.Join(", ", AnnouncementLevels.Select(l => $"{l:+0;-0;0} dB"))}";
            if (settings == null || AnnouncementLevel == db)
                return null;
            settings.AnnouncementLevelDb = db == 0 ? null : db;
            MarkDirty();
            AddLog($"announcements: level {AnnouncementLevelName(db).ToLowerInvariant()}");
            return null;
        }

        /// <summary>A copy of announcement audio at the chosen level (kept within full scale).</summary>
        internal short[] AtAnnouncementLevel(short[] samples)
        {
            int db = AnnouncementLevel;
            if (db == 0)
                return samples;
            double gain = Math.Pow(10, db / 20.0);
            short[] copy = new short[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                copy[i] = (short)Math.Clamp(Math.Round(samples[i] * gain), -32767, 32767);
            return copy;
        }

        /// <summary>The remembered announcement files, in the order they were added.</summary>
        public IReadOnlyList<string> Announcements => (IReadOnlyList<string>)Settings?.Announcements ?? Array.Empty<string>();

        /// <summary>An announcement's display name: its file name without .wav.</summary>
        public static string AnnouncementName(string path) => Path.GetFileNameWithoutExtension(path ?? string.Empty);

        /// <summary>An announcement's audio as 8 kHz mono (converted once, then from memory). Throws ArgumentException with a plain reason.</summary>
        public short[] AnnouncementAudio(string path)
        {
            DateTime written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            lock (announcementCache)
            {
                if (announcementCache.TryGetValue(path, out var cached) && cached.Written == written)
                    return cached.Samples;
            }
            short[] samples = WavFile.ReadAnnouncement(path);
            lock (announcementCache)
                announcementCache[path] = (written, samples);
            return samples;
        }

        /// <summary>How long an announcement lasts, or null when the file can't be read.</summary>
        public TimeSpan? AnnouncementLength(string path)
        {
            try { return TimeSpan.FromSeconds(AnnouncementAudio(path).Length / (double)VoiceProcessor.SampleRate); }
            catch (ArgumentException) { return null; }
            catch (IOException) { return null; }
        }

        /// <summary>Adds a WAV file to the list. Returns null, or why it can't be used.</summary>
        public string AddAnnouncement(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "no file given";
            path = Path.GetFullPath(path);
            try { AnnouncementAudio(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return ex.Message;
            }
            Settings.Announcements ??= new List<string>();
            if (!Settings.Announcements.Contains(path))
            {
                Settings.Announcements.Add(path);
                AddLog($"announcement added: {AnnouncementName(path)} ({AnnouncementLength(path)?.TotalSeconds:F0} s)");
                MarkDirty();
            }
            return null;
        }

        /// <summary>Takes a file off the list (the file itself stays).</summary>
        public void RemoveAnnouncement(string path)
        {
            if (Settings?.Announcements?.Remove(path) == true)
            {
                lock (announcementCache)
                    announcementCache.Remove(path);
                AddLog($"announcement removed from the list: {AnnouncementName(path)}");
                MarkDirty();
            }
        }

        /// <summary>
        /// Sends an announcement on the focused channel, or on every listening channel. Returns null when it
        /// has started, or why not (also logged). Esc (CancelTones) stops it, as it does a tone.
        /// </summary>
        public string PlayAnnouncement(string path, bool allListening)
        {
            string Refuse(string why)
            {
                AddLog(why);
                return why;
            }

            string blocked = AnnouncementBlocked();
            if (blocked != null)
                return Refuse(blocked);

            short[] samples;
            try { samples = AnnouncementAudio(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return Refuse($"announcement {AnnouncementName(path)}: {ex.Message}");
            }
            TimeSpan length = TimeSpan.FromSeconds(samples.Length / (double)VoiceProcessor.SampleRate);
            if (length > txLimit)
                return Refuse($"announcement {AnnouncementName(path)} is {length.TotalSeconds:F0} s, longer than the {txLimit.TotalSeconds:F0} s transmit time limit");

            string why = AnnouncementTargets(allListening, out List<Chan> targets);
            if (why != null)
                return Refuse(why);

            SendAnnouncement(TonePlan.FromAudio($"announcement {AnnouncementName(path)}", AtAnnouncementLevel(samples)), targets);
            return null;
        }
    } // public partial class DispatchConsole
}
