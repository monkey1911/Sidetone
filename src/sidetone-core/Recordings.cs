// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Deleting old talkgroup recordings. Recordings live in FOLDER/SYSTEM/YYYY-MM-DD/; only
* Sidetone's own audio files (.opus, .wav, and unfinished .part files) in those date-named folders
* are ever deleted, and a day's folder only when that leaves it empty. Anything else in the
* recordings folder is left alone, even if the folder is set to somewhere shared like home.
*/

using System.Globalization;

namespace Sidetone.Core
{
    public static class Recordings
    {
        private static readonly string[] Ours = { ".opus", ".wav", ".part" };

        /// <summary>
        /// Deletes recordings from days more than <paramref name="keepDays"/> before <paramref name="today"/>
        /// (today counts as day 1). Returns how many files went.
        /// </summary>
        public static int Prune(string folder, DateTime today, int keepDays)
        {
            if (keepDays <= 0 || string.IsNullOrWhiteSpace(folder))
                return 0;
            int removed = 0;
            DateTime oldest = today.Date.AddDays(1 - keepDays);
            try
            {
                if (!Directory.Exists(folder))
                    return 0;
                foreach (string systemDir in Directory.GetDirectories(folder))
                {
                    foreach (string dayDir in Directory.GetDirectories(systemDir))
                    {
                        if (!DateTime.TryParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day) || day >= oldest)
                            continue;
                        foreach (string file in Directory.GetFiles(dayDir))
                        {
                            if (!Ours.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                                continue;
                            try
                            {
                                File.Delete(file);
                                removed++;
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* next time */ }
                        }
                        try
                        {
                            if (!Directory.EnumerateFileSystemEntries(dayDir).Any())
                                Directory.Delete(dayDir);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // a folder that can't be read is skipped; the next pass tries again
            }
            return removed;
        }
        /// <summary>
        /// The finished recordings in a folder (FOLDER/SYSTEM/yyyy-MM-dd/file), newest first, read back from the names
        /// RecordingPath gives them: HHmmss_CHANNEL_TGnnn_RX|TX_WHO[-n].opus|wav. Anything else is left out.
        /// </summary>
        public static List<RecordingFile> List(string folder)
        {
            List<RecordingFile> found = new List<RecordingFile>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return found;
            try
            {
                foreach (string systemDir in Directory.GetDirectories(folder))
                    foreach (string dayDir in Directory.GetDirectories(systemDir))
                    {
                        if (!DateTime.TryParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
                            continue;
                        foreach (string file in Directory.GetFiles(dayDir))
                            if (RecordingFile.Parse(file, Path.GetFileName(systemDir), day) is RecordingFile r)
                                found.Add(r);
                    }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* what was read so far */ }
            return found.OrderByDescending(r => r.Started).ThenBy(r => r.Path, StringComparer.Ordinal).ToList();
        }
    } // public static class Recordings

    /// <summary>One recording file, as Recordings.List reads it back from its name.</summary>
    public sealed class RecordingFile
    {
        public string Path { get; init; }
        /// <summary>The system's folder name (as RecordingPath made it safe for a file name).</summary>
        public string System { get; init; }
        public DateTime Started { get; init; }
        /// <summary>The channel's name as in the file name (spaces and other signs became underscores).</summary>
        public string Channel { get; init; }
        public uint Talkgroup { get; init; }
        public bool Tx { get; init; }
        public string Who { get; init; }

        /// <summary>A file's details, or null when it isn't a finished recording of Sidetone's.</summary>
        internal static RecordingFile Parse(string path, string system, DateTime day)
        {
            string ext = global::System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".opus" && ext != ".wav")
                return null;
            string[] parts = global::System.IO.Path.GetFileNameWithoutExtension(path).Split('_');
            // the TGnnn part followed by RX or TX: everything between the time and it is the channel (which may hold underscores)
            int tg = -1;
            for (int i = 2; i < parts.Length - 2; i++)
                if (parts[i].Length > 2 && parts[i].StartsWith("TG", StringComparison.Ordinal) && uint.TryParse(parts[i].AsSpan(2), out _) && (parts[i + 1] == "RX" || parts[i + 1] == "TX"))
                {
                    tg = i;
                    break;
                }
            if (tg < 0 || parts[0].Length != 6 || !TimeSpan.TryParseExact(parts[0], "hhmmss", CultureInfo.InvariantCulture, out TimeSpan at))
                return null;
            // (a second call in the same second gets "-2" after the talker; it's left on, as "Engine-7" is a name)
            string who = string.Join("_", parts.Skip(tg + 2));
            return new RecordingFile
            {
                Path = path,
                System = system,
                Started = day.Date + at,
                Channel = string.Join("_", parts.Skip(1).Take(tg - 1)),
                Talkgroup = uint.Parse(parts[tg].AsSpan(2), CultureInfo.InvariantCulture),
                Tx = parts[tg + 1] == "TX",
                Who = who
            };
        }
    } // public sealed class RecordingFile
}
