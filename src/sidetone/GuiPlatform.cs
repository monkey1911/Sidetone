// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* What a device platform (Android) adds to the console view: a hardware PTT key, keeping the
* screen on, staying connected in the background, battery settings. On a desktop there is none
* (GuiPlatform.Current is null) and the view works as it always has.
*/

using Avalonia.Platform.Storage;

namespace Sidetone.Gui
{
    public interface IGuiPlatform
    {
        /// <summary>
        /// Waits for the next hardware key and calls back on the UI thread with its key code and name. Only one
        /// wait at a time; a new one replaces the old.
        /// </summary>
        void LearnPttKey(Action<int, string> learned);

        /// <summary>Stops waiting for a key.</summary>
        void CancelLearn();

        /// <summary>Applies the saved device settings (the PTT key, keeping the screen on) for the console on show, or null.</summary>
        void Apply(AppSettings settings, DispatchConsole console);

        /// <summary>A console started (not the demo): keep the app running and connected in the background.</summary>
        void ConsoleStarted(DispatchConsole console);

        /// <summary>The console stopped: the app may stop when it's out of sight.</summary>
        void ConsoleStopped();

        /// <summary>Opens the platform's battery optimisation settings, where Sidetone can be let run unrestricted.</summary>
        void OpenBatterySettings();

        /// <summary>The device, for the saved debug logs (like "Samsung SM-G736U, Android 14 (API 34)").</summary>
        string DeviceDescription { get; }

        /// <summary>Offers files to other apps through the system's share sheet. False when nothing could take them.</summary>
        bool Share(IReadOnlyList<string> files, string subject);
    }

    public static class GuiPlatform
    {
        /// <summary>The device platform, or null on a desktop.</summary>
        public static IGuiPlatform Current { get; set; }

        /// <summary>
        /// Where files picked from outside the app (codeplugs, key files, announcements) are copied when the picker
        /// gives no path on disk (Android's document picker): $XDG_DATA_HOME/sidetone/imported/KIND.
        /// </summary>
        public static string ImportFolder(string kind)
        {
            string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "sidetone", "imported", kind);
        }

        /// <summary>
        /// A picked file's path on disk: its own when it has one, otherwise a copy in ImportFolder(kind), made now
        /// (an earlier copy with the same name is replaced). Null when it can't be read.
        /// </summary>
        public static async Task<string> LocalPath(IStorageFile file, string kind)
        {
            string path = file.TryGetLocalPath();
            if (path != null && File.Exists(path))
                return path;
            string folder = ImportFolder(kind);
            Directory.CreateDirectory(folder);
            string name = Path.GetFileName(file.Name);
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
                name = "file";
            string copy = Path.Combine(folder, name);
            await using (Stream from = await file.OpenReadAsync())
            await using (FileStream to = File.Create(copy))
                await from.CopyToAsync(to);
            return copy;
        }
    } // public static class GuiPlatform
}
