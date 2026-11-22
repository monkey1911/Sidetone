// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Where files go on macOS and Windows. The core keeps settings, recordings, logs and history under the
* XDG folders (each adds "sidetone"), as on Linux; here they're pointed at the system's own places when
* they aren't set, as Android does for its app storage, so every path works unchanged:
*   macOS: ~/Library/Application Support/sidetone for all of it.
*   Windows: settings in %APPDATA%\sidetone (they roam with the profile); recordings, logs, history and
*     the PTT socket in %LOCALAPPDATA%\sidetone (they shouldn't).
* Linux and Android are left alone.
*
* It also hands the core the system's own speech, password store and notifications (MacPlatform.cs).
*/

namespace Sidetone.Core
{
    public static class DesktopPlatform
    {
        /// <summary>Sets up the folders and the system's own services on macOS and Windows. Call first thing.</summary>
        public static void Setup()
        {
            SetFolders();
            if (OperatingSystem.IsMacOS())
                MacPlatform.Register();
        }

        /// <summary>Sets the XDG folders that aren't set to the system's own places (macOS, Windows).</summary>
        private static void SetFolders()
        {
            if (OperatingSystem.IsMacOS())
            {
                string support = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
                Default("XDG_CONFIG_HOME", support);
                Default("XDG_DATA_HOME", support);
                Default("XDG_STATE_HOME", support);
            }
            else if (OperatingSystem.IsWindows())
            {
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                Default("XDG_CONFIG_HOME", roaming);
                Default("XDG_DATA_HOME", local);
                Default("XDG_STATE_HOME", local);
                string runtime = Path.Combine(local, "sidetone");
                if (Default("XDG_RUNTIME_DIR", runtime))
                    Directory.CreateDirectory(runtime);     // the control socket goes here; it must exist
            }
        }

        /// <summary>Sets a variable unless it's already set; true when it was set here.</summary>
        private static bool Default(string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) || string.IsNullOrEmpty(value))
                return false;
            Environment.SetEnvironmentVariable(name, value);
            return true;
        }
    } // public static class DesktopPlatform
}
