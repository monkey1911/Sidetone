// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone for Android. The on-device self-test, run by CI on an Android 11 emulator (and by
* anyone, with the "selftest" extra): the parts that only Android can check. The vocoder library
* loads and round-trips speech, the folders are the app's own and settings save there, the audio
* backend lists devices and opens an output (and the microphone when it's allowed), the platform's
* text to speech makes audio, the keystore remembers a password, and the demo console is on show.
* Results go to logcat (tag "sidetone"): PASS and FAIL lines, then ALL ANDROID CHECKS PASSED.
*/

using Android.App;
using Log = Android.Util.Log;
using Avalonia.Threading;
using dvmconsole;

namespace Sidetone.Droid
{
    public static class DeviceSelfTest
    {
        private static int failures, checks;

        private static void Check(bool ok, string what)
        {
            checks++;
            if (!ok)
                failures++;
            Log.Info(AndroidPlatform.LogTag, $"{(ok ? "PASS" : "FAIL")}: {what}");
        }

        private static void Try(string what, Func<bool> test)
        {
            try { Check(test(), what); }
            catch (Exception ex) { Check(false, $"{what} ({ex.GetType().Name}: {ex.Message})"); }
        }

        /// <summary>Runs the checks a few seconds after the activity is up (the view needs to be made).</summary>
        public static void RunSoon(Activity activity)
        {
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                Run();
            });
        }

        private static void Run()
        {
            failures = checks = 0;
            Log.Info(AndroidPlatform.LogTag, $"sidetone android self-test (Sidetone {typeof(DeviceSelfTest).Assembly.GetName().Version}, Android API {(int)Android.OS.Build.VERSION.SdkInt})");

            Try("vocoder: libvocoder loads, and IMBE encodes and decodes a second of speech-like audio", () =>
            {
                MBEEncoder encoder = new MBEEncoder(MBE_MODE.IMBE_88BIT);
                MBEDecoder decoder = new MBEDecoder(MBE_MODE.IMBE_88BIT);
                byte[] cw = new byte[11];
                short[] heard = new short[160];
                double energy = 0;
                for (int f = 0; f < 50; f++)
                {
                    short[] frame = new short[160];
                    for (int i = 0; i < 160; i++)
                    {
                        double t = (f * 160 + i) / 8000.0;
                        frame[i] = (short)(6000 * (Math.Sin(2 * Math.PI * 140 * t) + 0.5 * Math.Sin(2 * Math.PI * 280 * t) + 0.3 * Math.Sin(2 * Math.PI * 700 * t)) / 1.8);
                    }
                    encoder.encode(frame, cw);
                    decoder.decode(cw, heard);
                    if (f >= 10)
                        energy += heard.Sum(s => (double)s * s);
                }
                return energy > 0;
            });

            Try("folders: settings, data and logs are in the app's own storage, and settings save there", () =>
            {
                string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                string files = AndroidPlatform.Instance.AppContext.FilesDir.AbsolutePath;
                AppSettings s = new AppSettings { HardwarePttKey = 25 };
                string path = Path.Combine(config, "sidetone", "selftest.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                s.Save(path);
                AppSettings back = AppSettings.Load(path, out _);
                File.Delete(path);
                return config.StartsWith(files, StringComparison.Ordinal) && AppSettings.DefaultPath.StartsWith(files, StringComparison.Ordinal) &&
                       LogFiles.DefaultFolder.StartsWith(files, StringComparison.Ordinal) && back.HardwarePttKey == 25;
            });

            Try("audio: Android's devices are listed and an output plays a block", () =>
            {
                AudioDeviceList list = AudioBackend.Current.ListDevices();
                using IAudioOutput output = AudioBackend.Current.OpenOutput(null);
                bool failed = false;
                output.Failed += _ => failed = true;
                for (int i = 0; i < 5; i++)
                    output.Write(new short[160]);
                return AudioBackend.Current.Name == "Android" && list.Outputs.Count > 0 && !failed;
            });

            Try("audio: the microphone opens when it's allowed (refused plainly when not)", () =>
            {
                if (!AndroidPlatform.Instance.HasMicrophone)
                {
                    try { AudioBackend.Current.OpenInput(null); return false; }
                    catch (InvalidOperationException ex) { return ex.Message.Contains("microphone"); }
                }
                using IAudioInput input = AudioBackend.Current.OpenInput(null);
                int blocks = 0;
                input.Block += _ => Interlocked.Increment(ref blocks);
                Thread.Sleep(600);
                return blocks > 5;
            });

            Try("text to speech: Android's engine is used and says a sentence, levelled like any announcement", () =>
            {
                SpeechEngine e = Speech.Choose(null, null, out string why);
                if (e == null || e.Kind != SpeechEngineKind.Platform)
                    return false;
                try
                {
                    short[] said = Speech.Synthesize(e, "Sidetone self test.");
                    return said.Length > 4000 && Math.Abs(WavFile.SpeechLevelDb(said) - VoiceProcessor.TargetDb) < 3;
                }
                catch (ArgumentException ex) when (ex.Message.Contains("didn't start") || ex.Message.Contains("couldn't start"))
                {
                    Log.Info(AndroidPlatform.LogTag, $"  (no text-to-speech engine on this device: {ex.Message})");
                    return true;    // an emulator image may have none; the device without one is told why
                }
            });

            Try("keystore: a key file password is remembered, encrypted, and forgotten", () =>
            {
                string keyFile = "/selftest/demo.ekc";
                bool stored = SecretStore.Store(keyFile, "s3cret");
                string back = SecretStore.Lookup(keyFile);
                bool cleared = SecretStore.Clear(keyFile);
                return stored && back == "s3cret" && cleared && SecretStore.Lookup(keyFile) == null;
            });

            Try("debug log: it's in the app's own storage and has this start written down", () =>
            {
                string files = AndroidPlatform.Instance.AppContext.FilesDir.AbsolutePath;
                string today = Directory.GetFiles(DebugLog.Folder, "debug-*.log").OrderBy(f => f).Last();
                string text = File.ReadAllText(today);
                return DebugLog.Folder.StartsWith(files, StringComparison.Ordinal) && text.Contains("start: Sidetone") && text.Contains("app: activity created (self-test)");
            });

            Try("hardware PTT: Zebra's key broadcasts, Sonim's and Kodiak's PTT intents and Sidetone's PTT_DOWN/UP key the console (screen off or locked)", () =>
            {
                AndroidPlatform p = AndroidPlatform.Instance;
                p.Apply(new AppSettings { HardwarePttKey = (int)Android.Views.Keycode.ButtonL2 }, null);
                try
                {
                    ConsoleService.PttBroadcasts r = new ConsoleService.PttBroadcasts();
                    Android.Content.Intent Zebra(Android.Views.KeyEventActions a)
                    {
                        Android.Content.Intent i = new Android.Content.Intent("com.symbol.button.L2");
                        i.PutExtra(Android.Content.Intent.ExtraKeyEvent, new Android.Views.KeyEvent(a, Android.Views.Keycode.ButtonL2));
                        return i;
                    }
                    r.OnReceive(p.AppContext, Zebra(Android.Views.KeyEventActions.Down));
                    r.OnReceive(p.AppContext, Zebra(Android.Views.KeyEventActions.Up));
                    r.OnReceive(p.AppContext, new Android.Content.Intent("org.sidetone.app.PTT_DOWN"));
                    r.OnReceive(p.AppContext, new Android.Content.Intent("org.sidetone.app.PTT_UP"));
                    // Sonim's firmware sends to the declared receiver; Kodiak-style carries the direction in the key event
                    PttKeyReceiver declared = new PttKeyReceiver();
                    declared.OnReceive(p.AppContext, new Android.Content.Intent(PttIntents.SonimDown));
                    declared.OnReceive(p.AppContext, new Android.Content.Intent(PttIntents.SonimUp));
                    Android.Content.Intent kodiak = new Android.Content.Intent(PttIntents.Kodiak);
                    kodiak.PutExtra(Android.Content.Intent.ExtraKeyEvent, new Android.Views.KeyEvent(Android.Views.KeyEventActions.Down, (Android.Views.Keycode)228));
                    declared.OnReceive(p.AppContext, kodiak);
                    string log = File.ReadAllText(Directory.GetFiles(DebugLog.Folder, "debug-*.log").OrderBy(f => f).Last());
                    return log.Contains("com.symbol.button.L2: KEYCODE_BUTTON_L2 down") && !log.Contains("KEYCODE_BUTTON_L2 down (not the learned") &&
                           log.Contains("org.sidetone.app.PTT_DOWN down: keyed") && log.Contains("org.sidetone.app.PTT_UP up: keyed") &&
                           log.Contains("com.sonim.intent.action.PTT_KEY_DOWN down: keyed") && log.Contains("com.sonim.intent.action.PTT_KEY_UP up: keyed") &&
                           log.Contains("com.kodiak.intent.action.PTT_BUTTON KEYCODE_12 down: keyed");
                }
                finally { p.Apply(new AppSettings(), null); }
            });

            Try("hardware PTT: a learned key is named the way Settings shows it", () =>
                SettingsOverlay.KeyLabel(Android.Views.KeyEvent.KeyCodeToString(Android.Views.Keycode.VolumeDown), 25) == "Volume down");

            bool shown = false;
            ManualResetEventSlim done = new ManualResetEventSlim(false);
            Dispatcher.UIThread.Post(() =>
            {
                ConsoleView v = App.SingleView;
                shown = v != null && v.ActiveConsole != null && v.ActiveConsole.Channels.Count > 0 && v.IsEffectivelyVisible;
                done.Set();
            });
            done.Wait(TimeSpan.FromSeconds(10));
            Check(shown, "the demo console is on show");

            Log.Info(AndroidPlatform.LogTag, failures == 0 ? $"ALL ANDROID CHECKS PASSED ({checks})" : $"{failures} ANDROID CHECK(S) FAILED");
        }
    } // public static class DeviceSelfTest
}
