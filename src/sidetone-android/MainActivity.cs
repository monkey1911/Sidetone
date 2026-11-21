// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone for Android. The activity: shows the console view, gives Back to it (closing a box, or
* going back to the channels), and passes hardware keys to the platform for the PTT key.
*
* Started with the extra "demo" (true), it shows the made-up demo system; with "selftest" (true),
* it runs the on-device checks and writes their results to logcat (tag "sidetone"):
*   adb shell am start -n org.sidetone.app/org.sidetone.app.MainActivity --ez selftest true
*/

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;

namespace Sidetone.Droid
{
    [Activity(
        Name = "org.sidetone.app.MainActivity",
        Label = "Sidetone",
        Theme = "@style/SidetoneTheme",
        Icon = "@drawable/icon",
        MainLauncher = true,
        Exported = true,
        LaunchMode = LaunchMode.SingleTask,
        WindowSoftInputMode = SoftInput.AdjustResize,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                               ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation |
                               ConfigChanges.Density)]
    public sealed class MainActivity : AvaloniaMainActivity
    {
        protected override void OnCreate(Bundle savedInstanceState)
        {
            AndroidPlatform.Setup(Application);
            Intent start = Intent;
            bool selfTest = start?.GetBooleanExtra("selftest", false) == true;
            if (App.SingleView == null)
            {
                // read before the view is made: the view starts with these
                App.Options.Demo = selfTest || start?.GetBooleanExtra("demo", false) == true;
                string theme = start?.GetStringExtra("theme");
                if (theme == "light" || theme == "dark")
                    App.Options.Theme = theme;
            }

            AndroidPlatform.Instance.Activity = this;
            DebugLog.Mark("app", $"activity created{(selfTest ? " (self-test)" : App.Options.Demo ? " (demo)" : string.Empty)}");
            base.OnCreate(savedInstanceState);

            BackRequested += (s, e) =>
            {
                if (App.SingleView?.GoBack() == true)
                    e.Handled = true;
            };

            if (selfTest)
                DeviceSelfTest.RunSoon(this);
        }

        protected override void OnResume()
        {
            base.OnResume();
            DebugLog.Write("app", "on screen");
            AndroidPlatform.Instance.Activity = this;
            AndroidPlatform.Instance.ApplyScreen();
        }

        protected override void OnStop()
        {
            DebugLog.Write("app", "out of sight");
            AndroidPlatform.Instance.ActivityStopped(this);
            base.OnStop();
        }

        protected override void OnDestroy()
        {
            if (AndroidPlatform.Instance.Activity == this)
                AndroidPlatform.Instance.Activity = null;
            base.OnDestroy();
        }

        public override bool DispatchKeyEvent(KeyEvent e)
        {
            if (e != null && AndroidPlatform.Instance.OnKey(e))
                return true;
            return base.DispatchKeyEvent(e);
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            AndroidPlatform.Instance.PermissionsAnswered();
        }
    } // public sealed class MainActivity
}
