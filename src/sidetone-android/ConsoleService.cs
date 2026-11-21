// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone for Android. The foreground service that keeps a console connected with the screen off
* or the app out of sight: a permanent notification ("Connected to SYSTEM", with Open and
* Disconnect), a partial wake lock and a low-latency Wi-Fi lock. The console itself runs where it
* always does; the service only keeps the process alive and allowed to use the network and the
* microphone (Android 11 lets a background app use the microphone only from a service declared
* for it).
*
* It also holds a media session, so Bluetooth and headset buttons reach Sidetone with the screen
* off: when one is the learned PTT key, it keys the console.
*
* Paged notifications (DesktopNotify.Platform) are posted here too, on their own channel.
*/

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media.Session;
using Android.Net.Wifi;
using Android.OS;
using Android.Views;
using AndroidX.Core.App;

namespace Sidetone.Droid
{
    [Service(Name = "org.sidetone.app.ConsoleService", Exported = false,
             ForegroundServiceType = ForegroundService.TypeMicrophone | ForegroundService.TypeMediaPlayback)]
    public sealed class ConsoleService : Service
    {
        private const int ConnectionId = 1;
        private const string ConnectionChannel = "connection", PagesChannel = "pages";
        private const string ActionStart = "org.sidetone.app.START", ActionDisconnect = "org.sidetone.app.DISCONNECT";
        private const string ExtraSystem = "system", ExtraMicrophone = "microphone";
        private static int nextNotification = 100;

        private PowerManager.WakeLock wakeLock;
        private WifiManager.WifiLock wifiLock;
        private MediaSession media;

        /// <summary>Starts the service, or updates it (the system name, and whether the microphone may be used now).</summary>
        public static void Start(Context context, string system, bool microphone)
        {
            Intent i = new Intent(context, typeof(ConsoleService));
            i.SetAction(ActionStart);
            i.PutExtra(ExtraSystem, system);
            i.PutExtra(ExtraMicrophone, microphone);
            try { context.StartForegroundService(i); }
            catch (Exception ex) when (ex is Java.Lang.IllegalStateException || ex is Java.Lang.SecurityException)
            {
                Android.Util.Log.Warn(AndroidPlatform.LogTag, $"the connection service didn't start: {ex.Message}");
            }
        }

        public static void Stop(Context context) => context.StopService(new Intent(context, typeof(ConsoleService)));

        public override IBinder OnBind(Intent intent) => null;

        private static void Channels(Context context)
        {
            NotificationManager nm = (NotificationManager)context.GetSystemService(NotificationService);
            nm.CreateNotificationChannel(new NotificationChannel(ConnectionChannel, "Connection", NotificationImportance.Low)
            {
                Description = "Shown while Sidetone is connected, so it keeps running with the screen off"
            });
            nm.CreateNotificationChannel(new NotificationChannel(PagesChannel, "Pages", NotificationImportance.High)
            {
                Description = "When a radio sends a call alert to this console"
            });
        }

        private static PendingIntent OpenApp(Context context)
        {
            Intent open = new Intent(context, typeof(MainActivity));
            open.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
            return PendingIntent.GetActivity(context, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            if (intent?.Action == ActionDisconnect)
            {
                AndroidPlatform.Instance.Disconnect();
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            string system = intent?.GetStringExtra(ExtraSystem) ?? "the FNE";
            bool microphone = intent?.GetBooleanExtra(ExtraMicrophone, false) == true;
            Channels(this);

            Intent disconnect = new Intent(this, typeof(ConsoleService));
            disconnect.SetAction(ActionDisconnect);
            PendingIntent stop = PendingIntent.GetService(this, 1, disconnect, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            Notification n = new NotificationCompat.Builder(this, ConnectionChannel)
                .SetContentTitle($"Connected to {system}")
                .SetContentText(microphone ? "Listening; PTT works with the screen off" : "Listening; allow the microphone to transmit")
                .SetSmallIcon(Resource.Drawable.icon)
                .SetOngoing(true)
                .SetContentIntent(OpenApp(this))
                .AddAction(0, "Disconnect", stop)
                .SetCategory(NotificationCompat.CategoryService)
                .Build();

            ForegroundService types = ForegroundService.TypeMediaPlayback | (microphone ? ForegroundService.TypeMicrophone : 0);
            try { StartForeground(ConnectionId, n, types); }
            catch (Java.Lang.SecurityException)
            {
                StartForeground(ConnectionId, n, ForegroundService.TypeMediaPlayback);     // without the microphone, until it's allowed
            }

            HoldLocks();
            StartMediaSession();
            return StartCommandResult.Sticky;
        }

        private void HoldLocks()
        {
            if (wakeLock == null)
            {
                PowerManager pm = (PowerManager)GetSystemService(PowerService);
                wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "sidetone:connection");
                wakeLock.SetReferenceCounted(false);
                wakeLock.Acquire();
            }
            if (wifiLock == null)
            {
                WifiManager wm = (WifiManager)ApplicationContext.GetSystemService(WifiService);
                wifiLock = wm?.CreateWifiLock(Android.Net.WifiMode.FullLowLatency, "sidetone:connection");
                wifiLock?.SetReferenceCounted(false);
                wifiLock?.Acquire();
            }
        }

        /// <summary>A media session, so media buttons (Bluetooth PTT buttons, headset buttons) come here with the screen off.</summary>
        private void StartMediaSession()
        {
            if (media != null)
                return;
            media = new MediaSession(this, "Sidetone");
            media.SetCallback(new MediaButtons());
            media.SetPlaybackState(new PlaybackState.Builder()
                .SetState(PlaybackStateCode.Playing, 0, 1)
                .SetActions(PlaybackState.ActionPlayPause | PlaybackState.ActionPlay | PlaybackState.ActionPause)
                .Build());
            media.Active = true;
        }

        private sealed class MediaButtons : MediaSession.Callback
        {
            public override bool OnMediaButtonEvent(Intent mediaButtonIntent)
            {
                KeyEvent e = mediaButtonIntent?.GetParcelableExtra(Intent.ExtraKeyEvent) as KeyEvent;
                AndroidPlatform p = AndroidPlatform.Instance;
                if (e != null && p.PttKeyCode == (int)e.KeyCode)
                    return p.PttKey(e.Action == KeyEventActions.Down, e.RepeatCount);
                return base.OnMediaButtonEvent(mediaButtonIntent);
            }
        }

        public override void OnDestroy()
        {
            media?.Release();
            media = null;
            if (wakeLock?.IsHeld == true)
                wakeLock.Release();
            wakeLock = null;
            if (wifiLock?.IsHeld == true)
                wifiLock.Release();
            wifiLock = null;
            base.OnDestroy();
        }

        /// <summary>Shows a notification on the Pages channel (DesktopNotify.Platform). Returns false when notifications aren't allowed.</summary>
        public static bool Notify(Context context, string title, string body, bool urgent)
        {
            try
            {
                Channels(context);
                NotificationManagerCompat nm = NotificationManagerCompat.From(context);
                if (!nm.AreNotificationsEnabled())
                    return false;
                Notification n = new NotificationCompat.Builder(context, PagesChannel)
                    .SetContentTitle(title)
                    .SetContentText(body)
                    .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))
                    .SetSmallIcon(Resource.Drawable.icon)
                    .SetPriority(urgent ? NotificationCompat.PriorityHigh : NotificationCompat.PriorityDefault)
                    .SetCategory(NotificationCompat.CategoryMessage)
                    .SetContentIntent(OpenApp(context))
                    .SetAutoCancel(true)
                    .Build();
                nm.Notify(Interlocked.Increment(ref nextNotification), n);
                return true;
            }
            catch (Java.Lang.SecurityException)
            {
                return false;
            }
        }
    } // public sealed class ConsoleService
}
