// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Shared core of both front ends.
*
* The debug log: what Sidetone was doing, for finding out why something went wrong on someone
* else's machine or phone. Unlike the console's log (which starts with a console), it starts with
* the program, so it also covers opening a codeplug. Each line is written to the file at once, so
* it survives a freeze or a crash: the last lines say what was happening. Crashes (unhandled
* exceptions) are written with their stack traces, and a watchdog notes when the UI stops
* answering, with the step it was on.
*
* One file a day in $XDG_STATE_HOME/sidetone/debug (Android: the app's own storage), kept for 7
* days. MakeBundle zips the debug logs and the console's log files for sending to someone.
*
* Never written here: codeplug contents, passwords, keys. Codeplugs hold FNE passwords, so a
* codeplug is described by its name and size only.
*/

using System.IO.Compression;
using System.Text;

namespace Sidetone.Core
{
    public static class DebugLog
    {
        public const int KeepDays = 7;
        private static readonly object sync = new object();
        private static string folder;
        private static volatile string step = "starting";
        private static DateTime stepAt = DateTime.UtcNow;
        private static Thread watchdog;

        /// <summary>How long the UI may not answer before it counts as frozen, and how often it's asked (tests shorten them).</summary>
        internal static TimeSpan FreezeAfter = TimeSpan.FromSeconds(5), AskEvery = TimeSpan.FromSeconds(2);

        /// <summary>$XDG_STATE_HOME/sidetone/debug (or ~/.local/state/sidetone/debug).</summary>
        public static string DefaultFolder
        {
            get
            {
                string state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
                if (string.IsNullOrEmpty(state))
                    state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
                return Path.Combine(state, "sidetone", "debug");
            }
        }

        /// <summary>The folder being written to, or null before Start.</summary>
        public static string Folder => folder;

        /// <summary>What the program was last doing (the watchdog reports it when the UI freezes).</summary>
        public static string Step => step;

        /// <summary>Starts the debug log in a folder (again: a no-op). Old days' files are deleted.</summary>
        public static void Start(string where, string header)
        {
            lock (sync)
            {
                if (folder != null)
                    return;
                try
                {
                    Directory.CreateDirectory(where);
                    folder = where;
                    foreach (string f in Directory.GetFiles(where, "debug-*.log"))
                    {
                        if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-KeepDays))
                            File.Delete(f);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    folder = null;
                    return;
                }
            }
            Write("start", $"Sidetone {typeof(DebugLog).Assembly.GetName().Version} {header}");
        }

        private static string FileFor(DateTime day) => Path.Combine(folder, $"debug-{day:yyyy-MM-dd}.log");

        /// <summary>Writes a line (and flushes it to the file straight away). Any thread.</summary>
        public static void Write(string area, string text)
        {
            string f = folder;
            if (f == null)
                return;
            DateTime now = DateTime.Now;
            string line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {area}: {text}{Environment.NewLine}";
            lock (sync)
            {
                try { File.AppendAllText(FileFor(now), line); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* nowhere to say so */ }
            }
        }

        /// <summary>Notes the step the program is on (also written to the log).</summary>
        public static void Mark(string area, string what)
        {
            step = $"{area}: {what}";
            stepAt = DateTime.UtcNow;
            Write(area, what);
        }

        /// <summary>Writes an exception with its whole chain and stack traces.</summary>
        public static void Exception(string area, string what, Exception ex)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(what).Append(": ");
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                sb.AppendLine($"{e.GetType().FullName}: {e.Message}");
                if (e.StackTrace != null)
                    sb.AppendLine(e.StackTrace);
                if (e.InnerException != null)
                    sb.Append("  caused by ");
            }
            Write(area, sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Writes unhandled exceptions (a crash on any thread, or a task's exception nobody looked at) to the log
        /// before the process goes.
        /// </summary>
        public static void CatchCrashes()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    Exception("crash", $"unhandled exception (terminating: {e.IsTerminating}; last step: {step})", ex);
                else
                    Write("crash", $"unhandled {e.ExceptionObject} (last step: {step})");
            };
            TaskScheduler.UnobservedTaskException += (s, e) => Exception("crash", "a task's exception was never looked at", e.Exception);
        }

        /// <summary>
        /// Watches the UI thread: every two seconds it asks <paramref name="post"/> to run a small job there. When one
        /// hasn't run after five seconds, the UI is frozen: that's written once, with the step it was on, and again when
        /// it answers.
        /// </summary>
        public static void WatchUi(Action<Action> post)
        {
            lock (sync)
            {
                if (watchdog != null)
                    return;
                watchdog = new Thread(() =>
                {
                    while (true)
                    {
                        ManualResetEventSlim done = new ManualResetEventSlim(false);
                        try { post(() => done.Set()); }
                        catch (Exception) { return; }
                        if (!done.Wait(FreezeAfter))
                        {
                            DateTime frozenAt = DateTime.UtcNow - FreezeAfter;
                            string was = step;
                            Write("freeze", $"the UI hasn't answered for {FreezeAfter.TotalSeconds:F0} s; it was on: {was} (since {(DateTime.UtcNow - stepAt).TotalSeconds:F0} s ago)");
                            done.Wait();
                            Write("freeze", $"the UI answered again after {(DateTime.UtcNow - frozenAt).TotalSeconds:F1} s");
                        }
                        Thread.Sleep(AskEvery);
                    }
                }) { IsBackground = true, Name = "sidetone-watchdog" };
                watchdog.Start();
            }
        }

        /// <summary>
        /// Zips the debug logs and the console's log files (the last <paramref name="days"/> days of each), and a short
        /// description of the system, into <paramref name="zipPath"/>. Returns how many files went in.
        /// </summary>
        public static int MakeBundle(string zipPath, string about, string consoleLogs, int days = 3)
        {
            Write("logs", $"saving the logs to {Path.GetFileName(zipPath)}");
            DateTime since = DateTime.Now.Date.AddDays(-(days - 1));
            int count = 0;
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            ZipArchiveEntry info = zip.CreateEntry("about.txt");
            using (StreamWriter w = new StreamWriter(info.Open()))
                w.Write(about);
            foreach ((string dir, string pattern, string into) in new[] { (folder, "debug-*.log", "debug"), (consoleLogs, "sidetone-*.log", "console") })
            {
                if (dir == null || !Directory.Exists(dir))
                    continue;
                foreach (string f in Directory.GetFiles(dir, pattern).OrderBy(f => f))
                {
                    if (File.GetLastWriteTime(f) < since)
                        continue;
                    lock (sync)     // a debug line being written now waits; the file is copied whole
                    {
                        using FileStream from = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using Stream to = zip.CreateEntry($"{into}/{Path.GetFileName(f)}").Open();
                        from.CopyTo(to);
                    }
                    count++;
                }
            }
            return count;
        }

        /// <summary>For tests: stops writing, so Start can be called again with another folder.</summary>
        internal static void StopForTest()
        {
            lock (sync)
                folder = null;
        }
    } // public static class DebugLog
}
