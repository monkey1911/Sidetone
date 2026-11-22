// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone for Android. The platform: where files go (the app's own storage, through the XDG
* variables the core already reads), the platform's audio, text to speech, password store and
* notifications, and what the console view asks of a device (IGuiPlatform): the hardware PTT key,
* keeping the screen on, the foreground service, permissions and the battery settings.
*/

using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Threading;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace Sidetone.Droid
{
    public sealed class AndroidPlatform : IGuiPlatform
    {
        public const string LogTag = "sidetone";
        private const int PermissionRequest = 7;

        private static AndroidPlatform instance;
        public static AndroidPlatform Instance => instance ?? throw new InvalidOperationException("AndroidPlatform.Setup hasn't run");

        public Context AppContext { get; }

        /// <summary>The activity on show, or null.</summary>
        public Activity Activity { get; set; }

        private Action<int, string> learning;
        private int? pttKey;
        private bool keepScreenOn, wakeOnCall = true, keyHolding, permissionsAsked, shownOverLock;
        private DispatchConsole running;     // the console the service keeps going, or null

        private AndroidPlatform(Context context) => AppContext = context;

        /// <summary>Sets everything up once (from the application, before Avalonia starts; again is harmless).</summary>
        public static void Setup(Context context)
        {
            if (instance != null)
                return;
            Context app = context.ApplicationContext ?? context;
            instance = new AndroidPlatform(app);

            // the core keeps settings, logs, history and recordings under the XDG folders: point them into the app's own storage
            string files = app.FilesDir?.AbsolutePath ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
            System.Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(files, "config"));
            System.Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(files, "data"));
            System.Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(files, "state"));
            System.Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", app.CacheDir?.AbsolutePath);

            // the debug log first, so everything after it (and a crash) is written down
            DebugLog.Start(DebugLog.DefaultFolder, instance.DeviceDescription);
            DebugLog.CatchCrashes();
            Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (s, e) =>
                DebugLog.Exception("crash", $"unhandled exception on an Android thread (last step: {DebugLog.Step})", e.Exception);
            instance.LogPreviousExits();

            AudioBackend.Current = new AndroidAudioBackend(app);
            Speech.Platform = new AndroidSpeech(app);
            SecretStore.Platform = (AndroidSecrets.Lookup, AndroidSecrets.Store, AndroidSecrets.Clear);
            DesktopNotify.Platform = (title, body, urgent) => ConsoleService.Notify(app, title, body, urgent);
            GuiPlatform.Current = instance;
        }

        /*
        ** The hardware PTT key
        */

        public void LearnPttKey(Action<int, string> learned) => learning = learned;

        public void CancelLearn() => learning = null;

        /// <summary>Keys that are never PTT: they'd take away Back, Home or the power button.</summary>
        private static bool Reserved(Keycode code) =>
            code is Keycode.Back or Keycode.Home or Keycode.Power or Keycode.AppSwitch or Keycode.Menu or Keycode.Unknown;

        /// <summary>A key from the activity. Returns true when it was used here (learned, or PTT).</summary>
        public bool OnKey(KeyEvent e)
        {
            Keycode code = e.KeyCode;
            if (learning != null && e.Action == KeyEventActions.Down && !Reserved(code))
            {
                Action<int, string> done = learning;
                learning = null;
                int number = (int)code;
                string name = KeyEvent.KeyCodeToString(code);
                DebugLog.Write("ptt", $"learned key {name} ({number}) from {e.Device?.Name ?? "an unknown device"}");
                Dispatcher.UIThread.Post(() => { done(number, name); App.SingleView?.RefreshNow(); });
                return true;
            }
            return pttKey != null && (int)code == pttKey && PttKey(e.Action == KeyEventActions.Down, e.RepeatCount);
        }

        /// <summary>The PTT key went down or up (from the activity, or a media button through the service). Hold-to-talk.</summary>
        public bool PttKey(bool down, int repeat = 0)
        {
            ConsoleView view = App.SingleView;
            DispatchConsole c = view?.ActiveConsole;
            if (c == null)
                return false;           // nothing to key: let the key do what it usually does
            if (down && repeat > 0)
                return true;            // a held key repeats; it's already keyed
            if (down == keyHolding)
                return true;
            keyHolding = down;
            Dispatcher.UIThread.Post(() =>
            {
                c.HoldToTalk(down);
                view.RefreshNow();
            });
            return true;
        }

        /// <summary>The learned PTT key's code, or null (the service's media buttons use it).</summary>
        public int? PttKeyCode => pttKey;

        public void Apply(AppSettings settings, DispatchConsole console)
        {
            pttKey = settings?.HardwarePttKey;
            keepScreenOn = settings?.KeepScreenOn == true;
            wakeOnCall = settings?.WakeOnCall != false;
            ApplyScreen();
        }

        /// <summary>Keeps the screen on while a console is connected, if Settings says so.</summary>
        public void ApplyScreen()
        {
            Activity a = Activity;
            if (a == null)
                return;
            bool on = keepScreenOn && running != null;
            a.RunOnUiThread(() =>       // a window's flags may only be changed on the main thread
            {
                Window w = a.Window;
                if (w == null)
                    return;
                if (on)
                    w.AddFlags(WindowManagerFlags.KeepScreenOn);
                else
                    w.ClearFlags(WindowManagerFlags.KeepScreenOn);
            });
        }

        /*
        ** Staying connected
        */

        public void ConsoleStarted(DispatchConsole console)
        {
            running = console;
            console.CallStarted += (c, src) => CallCameIn(console, c);
            AskForPermissions();
            ConsoleService.Start(AppContext, console.SystemConfig.Name, HasMicrophone);
            ApplyScreen();
        }

        public void ConsoleStopped()
        {
            running = null;
            keyHolding = false;
            ConsoleService.Stop(AppContext);
            ApplyScreen();
        }

        /// <summary>
        /// A call started (on the network thread). With the screen off and Settings' "Wake the screen" on, the screen
        /// is turned on for the call, the way a radio lights up, then sleeps again on the phone's own timeout. If
        /// Sidetone was on show when the screen went off, it shows over the lock screen; unlocking is still needed for
        /// anything else. Muted channels, or everything muted, don't wake it.
        /// </summary>
        private void CallCameIn(DispatchConsole console, ConsoleChannel channel)
        {
            if (!wakeOnCall || console != running || channel.Muted || console.GlobalMute)
                return;
            PowerManager pm = (PowerManager)AppContext.GetSystemService(Context.PowerService);
            if (pm == null || pm.IsInteractive)
                return;
            Activity a = Activity;
            a?.RunOnUiThread(() =>
            {
                a.SetShowWhenLocked(true);
                a.SetTurnScreenOn(true);
                shownOverLock = true;
            });
#pragma warning disable CA1422, CS0618 // a screen wake lock is how a background app turns the screen on; it's let go after 3 s
            PowerManager.WakeLock wake = pm.NewWakeLock(WakeLockFlags.ScreenBright | WakeLockFlags.AcquireCausesWakeup | WakeLockFlags.OnAfterRelease, "sidetone:call");
#pragma warning restore CA1422, CS0618
            wake.SetReferenceCounted(false);
            wake.Acquire(3000);
        }

        /// <summary>The activity went out of sight (the screen went off again, or another app): no longer shown over the lock screen.</summary>
        public void ActivityStopped(Activity a)
        {
            if (!shownOverLock || a == null)
                return;
            shownOverLock = false;
            a.SetShowWhenLocked(false);
            a.SetTurnScreenOn(false);
        }

        /// <summary>The service's Disconnect: closes the console and goes back to the start.</summary>
        public void Disconnect() => Dispatcher.UIThread.Post(() => App.SingleView?.Disconnect());

        public bool HasMicrophone => ContextCompat.CheckSelfPermission(AppContext, Manifest.Permission.RecordAudio) == Permission.Granted;

        private static bool Granted(Context c, string permission) => ContextCompat.CheckSelfPermission(c, permission) == Permission.Granted;

        /// <summary>Asks for the microphone (and, on Android 13+, notifications) the first time a console starts.</summary>
        private void AskForPermissions()
        {
            Activity a = Activity;
            if (a == null || permissionsAsked)
                return;
            List<string> wanted = new List<string>();
            if (!Granted(a, Manifest.Permission.RecordAudio))
                wanted.Add(Manifest.Permission.RecordAudio);
            if (OperatingSystem.IsAndroidVersionAtLeast(33) && !Granted(a, Manifest.Permission.PostNotifications))
                wanted.Add(Manifest.Permission.PostNotifications);
            if (OperatingSystem.IsAndroidVersionAtLeast(31) && !Granted(a, Manifest.Permission.BluetoothConnect))
                wanted.Add(Manifest.Permission.BluetoothConnect);
            if (wanted.Count == 0)
                return;
            permissionsAsked = true;
            ActivityCompat.RequestPermissions(a, wanted.ToArray(), PermissionRequest);
        }

        /// <summary>The permission question was answered: with the microphone allowed, the service can use it now.</summary>
        public void PermissionsAnswered()
        {
            DispatchConsole c = running;
            if (c != null)
                ConsoleService.Start(AppContext, c.SystemConfig.Name, HasMicrophone);
        }

        public string DeviceDescription =>
            $"{Build.Manufacturer} {Build.Model}, Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt})";

        /// <summary>
        /// Writes why the app stopped the last times (Android keeps the reasons: a crash, the app not responding, the
        /// system ending it for memory, the person swiping it away), with the thread dump of an app-not-responding,
        /// which shows where it was stuck. Each reason is written once.
        /// </summary>
        private void LogPreviousExits()
        {
            try
            {
                ActivityManager am = (ActivityManager)AppContext.GetSystemService(Context.ActivityService);
                ISharedPreferences prefs = AppContext.GetSharedPreferences("debug", FileCreationMode.Private);
                long seen = prefs.GetLong("last-exit", 0), newest = seen;
                IList<ApplicationExitInfo> exits = am?.GetHistoricalProcessExitReasons(AppContext.PackageName, 0, 5);
                foreach (ApplicationExitInfo x in (exits ?? new List<ApplicationExitInfo>()).OrderBy(x => x.Timestamp))
                {
                    if (x.Timestamp <= seen)
                        continue;
                    newest = Math.Max(newest, x.Timestamp);
                    DateTime when = DateTimeOffset.FromUnixTimeMilliseconds(x.Timestamp).LocalDateTime;
                    ApplicationExitInfoReason reason = (ApplicationExitInfoReason)x.Reason;
                    DebugLog.Write("last-exit", $"{when:yyyy-MM-dd HH:mm:ss}: {reason} ({x.Description ?? "no description"}), status {x.Status}, importance {x.Importance}");
                    if (reason == ApplicationExitInfoReason.Anr)
                    {
                        using Stream trace = x.TraceInputStream;
                        if (trace != null)
                        {
                            using StreamReader r = new StreamReader(trace);
                            char[] buffer = new char[48 * 1024];
                            int n = r.ReadBlock(buffer, 0, buffer.Length);
                            DebugLog.Write("last-exit", "the app-not-responding thread dump:\n" + new string(buffer, 0, n));
                        }
                    }
                }
                if (newest != seen)
                    prefs.Edit().PutLong("last-exit", newest).Apply();
            }
            catch (Exception ex)
            {
                DebugLog.Exception("last-exit", "couldn't read why the app last stopped", ex);
            }
        }

        public void OpenBatterySettings()
        {
            try
            {
                Intent i = new Intent(Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings);
                i.AddFlags(ActivityFlags.NewTask);
                AppContext.StartActivity(i);
            }
            catch (ActivityNotFoundException)
            {
                Intent i = new Intent(Android.Provider.Settings.ActionApplicationDetailsSettings, Android.Net.Uri.Parse("package:" + AppContext.PackageName));
                i.AddFlags(ActivityFlags.NewTask);
                AppContext.StartActivity(i);
            }
        }
    } // public sealed class AndroidPlatform
}
