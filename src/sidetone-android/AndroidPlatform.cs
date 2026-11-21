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
        private bool keepScreenOn, keyHolding, permissionsAsked;
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
            string files = app.FilesDir?.AbsolutePath ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(files, "config"));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(files, "data"));
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(files, "state"));
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", app.CacheDir?.AbsolutePath);

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
            ApplyScreen();
        }

        /// <summary>Keeps the screen on while a console is connected, if Settings says so.</summary>
        public void ApplyScreen()
        {
            Activity a = Activity;
            Window w = a?.Window;
            if (w == null)
                return;
            if (keepScreenOn && running != null)
                w.AddFlags(WindowManagerFlags.KeepScreenOn);
            else
                w.ClearFlags(WindowManagerFlags.KeepScreenOn);
        }

        /*
        ** Staying connected
        */

        public void ConsoleStarted(DispatchConsole console)
        {
            running = console;
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
