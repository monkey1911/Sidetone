// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone for Android. The application: sets up where files go and the platform's own audio,
* text to speech, password store and notifications before Avalonia starts, then starts the same
* App as the desktop.
*/

using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Sidetone.Droid
{
    [Application(Label = "Sidetone", Icon = "@drawable/icon", AllowBackup = false)]
    public sealed class SidetoneApplication : AvaloniaAndroidApplication<App>
    {
        public SidetoneApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public override void OnCreate()
        {
            AndroidPlatform.Setup(this);
            base.OnCreate();
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
    } // public sealed class SidetoneApplication
}
