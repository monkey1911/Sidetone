// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* Puts Sidetone in the desktop's app launcher: a .desktop file and the icon, in the user's own
* data folder (~/.local/share, or $XDG_DATA_HOME), following the freedesktop.org conventions that
* GNOME, KDE, Hyprland launchers and the rest all read. Nothing is installed system-wide.
*/

using System.Reflection;

namespace Sidetone.Gui
{
    public static class AppLauncher
    {
        public const string DesktopFileName = "sidetone.desktop";
        public const string IconName = "sidetone";
        private const string Marker = "X-Sidetone-Managed=true";

        /// <summary>The user's data folder.</summary>
        public static string DataHome
        {
            get
            {
                string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrWhiteSpace(data))
                    data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                return data;
            }
        }

        public static string DesktopPath(string dataHome) => Path.Combine(dataHome, "applications", DesktopFileName);

        /// <summary>
        /// This program, as a launcher entry should start it: the AppImage file when running from one
        /// (the program itself then runs from a temporary mount that changes every start), otherwise
        /// this executable.
        /// </summary>
        public static string ProgramPath
        {
            get
            {
                string appImage = Environment.GetEnvironmentVariable("APPIMAGE");
                return !string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage) ? appImage : Environment.ProcessPath;
            }
        }

        /// <summary>
        /// A launcher entry installed system-wide by a package (in $XDG_DATA_DIRS, or /usr/local/share
        /// and /usr/share), or null when there is none.
        /// </summary>
        public static string SystemEntry(string dataDirs = null)
        {
            dataDirs ??= Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
            if (string.IsNullOrWhiteSpace(dataDirs))
                dataDirs = "/usr/local/share:/usr/share";
            foreach (string dir in dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                string path = Path.Combine(dir, "applications", DesktopFileName);
                if (File.Exists(path))
                    return path;
            }
            return null;
        }
        private static string SvgPath(string dataHome) => Path.Combine(dataHome, "icons", "hicolor", "scalable", "apps", IconName + ".svg");
        private static string PngPath(string dataHome) => Path.Combine(dataHome, "icons", "hicolor", "256x256", "apps", IconName + ".png");

        /// <summary>One of the icon files built into the program (sidetone.svg, sidetone-256.png), or null.</summary>
        public static Stream OpenIcon(string name) => typeof(AppLauncher).Assembly.GetManifestResourceStream(name);

        /// <summary>
        /// A program path as the Exec key wants it: in double quotes, with the characters the
        /// desktop entry specification reserves escaped.
        /// </summary>
        public static string QuoteExec(string path)
        {
            string quoted = "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";
            return quoted.Replace("\\", "\\\\").Replace("%", "%%");     // then as a desktop-file string
        }

        /// <summary>The launcher entry for the program at <paramref name="exePath"/>.</summary>
        public static string DesktopEntry(string exePath) => string.Join("\n", new[]
        {
            "[Desktop Entry]",
            "Type=Application",
            "Name=Sidetone",
            "GenericName=Dispatch Console",
            "Comment=An IP dispatch console for DVMProject FNE networks",
            $"Exec={QuoteExec(exePath)}",
            $"Icon={IconName}",
            "Terminal=false",
            "Categories=Network;HamRadio;",
            "Keywords=radio;dispatch;console;P25;DVM;FNE;PTT;",
            "StartupWMClass=sidetone",
            Marker,
            string.Empty
        });

        /// <summary>The launcher entry is there.</summary>
        public static bool IsInstalled(string dataHome) => File.Exists(DesktopPath(dataHome));

        /// <summary>
        /// Writes the launcher entry and the icon. Returns the entry's path. Throws IOException or
        /// UnauthorizedAccessException when the folder can't be written.
        /// </summary>
        public static string Install(string dataHome, string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath))
                throw new IOException("couldn't work out where the program is");

            WriteIcon("sidetone.svg", SvgPath(dataHome));
            WriteIcon("sidetone-256.png", PngPath(dataHome));

            string desktop = DesktopPath(dataHome);
            Directory.CreateDirectory(Path.GetDirectoryName(desktop));
            File.WriteAllText(desktop, DesktopEntry(Path.GetFullPath(exePath)));
            return desktop;
        }

        private static void WriteIcon(string resource, string path)
        {
            using Stream icon = OpenIcon(resource);
            if (icon == null)
                return;     // a launcher entry without its icon still works
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using FileStream file = File.Create(path);
            icon.CopyTo(file);
        }

        /// <summary>
        /// Points an existing launcher entry at this copy of the program, for when a new version
        /// was extracted to a different folder. Does nothing when there is no entry, when someone
        /// else wrote it, or when it's already right. Returns true when it was rewritten.
        /// </summary>
        public static bool Refresh(string dataHome, string exePath)
        {
            try
            {
                string desktop = DesktopPath(dataHome);
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(desktop))
                    return false;
                string now = File.ReadAllText(desktop);
                if (!now.Contains(Marker) || now == DesktopEntry(Path.GetFullPath(exePath)))
                    return false;
                Install(dataHome, exePath);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Removes the launcher entry and the icon. Returns true when there was an entry.</summary>
        public static bool Remove(string dataHome)
        {
            bool was = IsInstalled(dataHome);
            foreach (string path in new[] { DesktopPath(dataHome), SvgPath(dataHome), PngPath(dataHome) })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            return was;
        }
    } // public static class AppLauncher
}
