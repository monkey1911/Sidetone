// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Remembered TUI settings, kept per system in a small JSON file
* (~/.config/sidetone/settings.json, or $XDG_CONFIG_HOME/sidetone/settings.json).
* Settings from before the rename (~/.config/dvmcli) are copied over the first time.
* Holds no passwords or key material.
*/

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidetone.Core
{
    /// <summary>
    /// Settings for one channel card.
    /// </summary>
    public sealed class ChannelSettings
    {
        public bool Listening { get; set; }
        public bool Muted { get; set; }
        public double Gain { get; set; } = 1.0;
    }

    /// <summary>
    /// Settings for one system.
    /// </summary>
    public sealed class SystemSettings
    {
        /// <summary>Per channel, keyed by "zone/name/tg".</summary>
        public Dictionary<string, ChannelSettings> Channels { get; set; } = new Dictionary<string, ChannelSettings>();
        public string Focused { get; set; }
        public bool GlobalMute { get; set; }
        public string PageA { get; set; } = string.Empty;
        public string PageB { get; set; } = string.Empty;
        public string PageRid { get; set; } = string.Empty;
    }

    /// <summary>
    /// All remembered settings.
    /// </summary>
    public sealed class AppSettings
    {
        public bool TalkPermitTone { get; set; } = true;
        /// <summary>The graphical console's theme: "light", "dark", or null to follow the desktop.</summary>
        public string Theme { get; set; }
        /// <summary>Audio output chosen in the graphical console's Settings (a PipeWire node name), or null for the system default.</summary>
        public string AudioOutput { get; set; }
        /// <summary>Microphone chosen in Settings (a PipeWire node name), or null for the system default.</summary>
        public string AudioInput { get; set; }
        /// <summary>PTT device chosen in Settings (its name), or null for none.</summary>
        public string PttDeviceName { get; set; }
        /// <summary>PTT button on that device (like BTN_THUMB), or null to choose automatically.</summary>
        public string PttButton { get; set; }
        /// <summary>Mute button on that device, "none", or null to choose automatically.</summary>
        public string MuteButton { get; set; }
        /// <summary>Transmit time limit in seconds, or null for the default.</summary>
        public int? TxLimitSeconds { get; set; }
        /// <summary>Space starts and stops transmit instead of hold-to-talk.</summary>
        public bool SpaceToggle { get; set; }
        /// <summary>The codeplug the graphical console opened last.</summary>
        public string LastCodeplug { get; set; }
        /// <summary>The system the graphical console used last.</summary>
        public string LastSystem { get; set; }
        public Dictionary<string, SystemSettings> Systems { get; set; } = new Dictionary<string, SystemSettings>(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>Settings folder name under the config folder.</summary>
        public const string FolderName = "sidetone";
        /// <summary>The folder name from before the project was renamed.</summary>
        public const string LegacyFolderName = "dvmcli";

        private static bool legacyChecked;

        /// <summary>The default settings file.</summary>
        public static string DefaultPath
        {
            get
            {
                string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrWhiteSpace(config))
                    config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                if (!legacyChecked)
                {
                    legacyChecked = true;
                    AdoptLegacyFolder(config);
                }
                return Path.Combine(config, FolderName, "settings.json");
            }
        }

        /// <summary>
        /// Copies the settings folder from before the rename (settings and the alias cache) to the new
        /// name, once: only when the new folder doesn't exist yet. The old folder is left alone, so an
        /// older version still finds its settings. Returns true when something was copied.
        /// </summary>
        public static bool AdoptLegacyFolder(string configRoot)
        {
            try
            {
                string from = Path.Combine(configRoot, LegacyFolderName), to = Path.Combine(configRoot, FolderName);
                if (!Directory.Exists(from) || Directory.Exists(to))
                    return false;
                CopyFolder(from, to, 0);
                return true;
            }
            catch (Exception)
            {
                return false;   // settings are a convenience; starting fresh is fine
            }
        }

        private static void CopyFolder(string from, string to, int depth)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
            if (depth >= 4)
                return;
            foreach (string dir in Directory.GetDirectories(from))
                CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)), depth + 1);
        }

        /// <summary>
        /// Loads settings; a missing or unreadable file gives defaults (and <paramref name="error"/> says why, if unreadable).
        /// </summary>
        public static AppSettings Load(string path, out string error)
        {
            error = null;
            try
            {
                if (path == null || !File.Exists(path))
                    return new AppSettings();
                AppSettings s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
                s.Systems = new Dictionary<string, SystemSettings>(s.Systems ?? new(), StringComparer.OrdinalIgnoreCase);
                return s;
            }
            catch (Exception ex)
            {
                error = $"settings file {path} unreadable, using defaults ({ex.Message})";
                return new AppSettings();
            }
        }

        /// <summary>
        /// Saves settings (written to a temporary file, then renamed, so a crash can't leave half a file).
        /// </summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>The settings for a system (created if new).</summary>
        public SystemSettings For(string systemName)
        {
            if (!Systems.TryGetValue(systemName ?? string.Empty, out SystemSettings s))
                Systems[systemName ?? string.Empty] = s = new SystemSettings();
            return s;
        }
    } // public sealed class AppSettings
}
