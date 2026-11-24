// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* What both consoles decide before a console starts: which audio devices, PTT device, key file
* and transmit time limit to use. A command line option wins; otherwise what the graphical
* console's Settings saved; otherwise the default. A saved device that isn't connected right now
* is skipped with a warning (it may just be unplugged); one named on the command line is an error.
*
* Also the log files: one per day in ~/.local/state/sidetone/logs, kept for two weeks.
*/

using dvmconsole;

namespace Sidetone.Core
{
    public static class StartupChoices
    {
        /// <summary>The transmit time limit's range and default, in seconds (the same in both consoles).</summary>
        public const int MinTxLimit = 5, MaxTxLimit = 900, DefaultTxLimit = 120;

        /// <summary>
        /// The transmit time limit: the option's, else the one saved in Settings, else the codeplug's
        /// <c>transmitTimeoutSeconds</c> (as the Windows console: 180 when the codeplug doesn't say), else 120.
        /// Always 5 to 900 s: a codeplug's 0 ("no limit" on Windows) is Sidetone's longest, 900, since a stuck PTT
        /// must still stop.
        /// </summary>
        public static int TxLimit(int? option, AppSettings saved, Codeplug codeplug = null) =>
            Math.Clamp(option ?? saved?.TxLimitSeconds ?? CodeplugTxLimit(codeplug) ?? DefaultTxLimit, MinTxLimit, MaxTxLimit);

        /// <summary>The codeplug's transmit timeout in seconds (0 → <see cref="MaxTxLimit"/>), or null without a codeplug.</summary>
        public static int? CodeplugTxLimit(Codeplug codeplug) =>
            codeplug == null ? null : codeplug.TransmitTimeoutSeconds == 0 ? MaxTxLimit : codeplug.TransmitTimeoutSeconds;

        /// <summary>
        /// The audio devices: the options', or the saved ones when they are connected (a warning goes
        /// into <paramref name="notes"/> when one isn't), or the system defaults.
        /// </summary>
        public static AudioSelection Audio(string optOutput, string optInput, AppSettings saved, List<string> notes)
        {
            string output = optOutput ?? saved?.AudioOutput, input = optInput ?? saved?.AudioInput;
            bool savedOutput = optOutput == null && !string.IsNullOrWhiteSpace(output);
            bool savedInput = optInput == null && !string.IsNullOrWhiteSpace(input);
            if (savedOutput || savedInput)
            {
                AudioDeviceList list = null;
                try { list = AudioDeviceList.Query(); }
                catch (Exception) { /* can't check; the sound system gets the name as saved */ }
                if (list != null && savedOutput && !Connected(list.Outputs, output))
                {
                    notes.Add($"warning: the saved audio output '{output}' isn't connected; using the system default");
                    output = null;
                }
                if (list != null && savedInput && !Connected(list.Inputs, input))
                {
                    notes.Add($"warning: the saved microphone '{input}' isn't connected; using the system default");
                    input = null;
                }
            }
            return AudioSelection.Choose(string.IsNullOrWhiteSpace(output) ? null : output, string.IsNullOrWhiteSpace(input) ? null : input);
        }

        private static bool Connected(List<AudioDevice> devices, string name)
        {
            try
            {
                AudioDeviceList.Resolve(devices, name, "--output");
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// The PTT device: the option's (a bad one is an error), or the saved one (a bad one is a
        /// warning in <paramref name="notes"/>), or none.
        /// </summary>
        public static (string Device, string Button, string Mute)? PttDevice(string optDevice, string optButton, string optMute, AppSettings saved, List<string> notes)
        {
            if (optDevice != null)
            {
                _ = new PttDeviceWatcher(optDevice, optButton, optMute);    // checks the button names
                return (optDevice, optButton, optMute);
            }
            if (optButton != null || optMute != null)
                throw new ArgumentException("--ptt-button and --mute-button need --ptt-device");
            if (string.IsNullOrWhiteSpace(saved?.PttDeviceName))
                return null;
            try
            {
                _ = new PttDeviceWatcher(saved.PttDeviceName, saved.PttButton, saved.MuteButton);
                return (saved.PttDeviceName, saved.PttButton, saved.MuteButton);
            }
            catch (ArgumentException ex)
            {
                notes.Add($"warning: the saved PTT device isn't being used: {ex.Message}");
                return null;
            }
        }

        /// <summary>The local key file: the option's, the one chosen in Settings, or the codeplug's; null for none.</summary>
        public static string KeyFile(Codeplug codeplug, string option, AppSettings saved) => LocalKeyFile.Resolve(codeplug, option ?? saved?.KeyFile);
    } // public static class StartupChoices

    /// <summary>
    /// The consoles' log files: one per day, so a call can be looked up afterwards.
    /// </summary>
    public static class LogFiles
    {
        /// <summary>How many days of log files are kept.</summary>
        public const int KeepDays = 14;

        /// <summary>~/.local/state/sidetone/logs (or under $XDG_STATE_HOME).</summary>
        public static string DefaultFolder
        {
            get
            {
                string state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
                if (string.IsNullOrWhiteSpace(state))
                    state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
                return Path.Combine(state, AppSettings.FolderName, "logs");
            }
        }

        /// <summary>The file for one day.</summary>
        public static string FileFor(string folder, DateTime day) => Path.Combine(folder, $"sidetone-{day:yyyy-MM-dd}.log");

        /// <summary>Deletes this folder's log files older than <paramref name="keepDays"/> days; returns how many went.</summary>
        public static int Prune(string folder, DateTime today, int keepDays = KeepDays)
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(folder))
                    return 0;
                DateTime oldest = today.Date.AddDays(1 - keepDays);
                foreach (string path in Directory.GetFiles(folder, "sidetone-*.log"))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (DateTime.TryParseExact(name.Substring("sidetone-".Length), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                                               System.Globalization.DateTimeStyles.None, out DateTime day) && day < oldest)
                    {
                        File.Delete(path);
                        removed++;
                    }
                }
            }
            catch (Exception)
            {
                // old logs staying around is no reason to stop
            }
            return removed;
        }
    } // public static class LogFiles
}
