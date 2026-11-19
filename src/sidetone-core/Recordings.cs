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
    } // public static class Recordings
}
