// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone for Android. Text to speech through Android's own engine (Google's, Samsung's, or
* whichever the phone uses), written to a WAV file that the core reads and levels like any
* announcement. Runs on the core's worker thread; Android answers on its main thread.
*/

using Android.Content;
using Android.OS;
using Android.Speech.Tts;

namespace Sidetone.Droid
{
    public sealed class AndroidSpeech : IPlatformSpeech
    {
        private static readonly TimeSpan InitWait = TimeSpan.FromSeconds(10);

        private readonly Context context;
        private readonly object sync = new object();
        private TextToSpeech tts;
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);
        private OperationResult initResult = OperationResult.Error;

        /// <summary>Made on Android's main thread (by the application), which is where the engine answers.</summary>
        public AndroidSpeech(Context context)
        {
            this.context = context;
            Engine(wait: false);     // start it now: it takes a moment
        }

        public string Name => "Android text to speech";

        public string VoiceName
        {
            get
            {
                try { return Engine(wait: false)?.Voice?.Name ?? "the phone's own"; }
                catch (Exception) { return "the phone's own"; }
            }
        }

        private sealed class InitListener : Java.Lang.Object, TextToSpeech.IOnInitListener
        {
            private readonly AndroidSpeech owner;
            public InitListener(AndroidSpeech owner) => this.owner = owner;
            public void OnInit(OperationResult status)
            {
                owner.initResult = status;
                owner.ready.Set();
            }
        }

        private TextToSpeech Engine(bool wait)
        {
            lock (sync)
            {
                tts ??= new TextToSpeech(context, new InitListener(this));
            }
            if (!wait)
                return ready.IsSet && initResult == OperationResult.Success ? tts : null;
            if (!ready.Wait(InitWait))
                throw new ArgumentException("Android's text to speech didn't start (is a text-to-speech engine installed?)");
            if (initResult != OperationResult.Success)
                throw new ArgumentException("Android's text to speech couldn't start (Settings, Accessibility, Text-to-speech output)");
            return tts;
        }

        private sealed class Progress : UtteranceProgressListener
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string Error;
            public override void OnStart(string utteranceId) { }
            public override void OnDone(string utteranceId) => Done.Set();
            [Obsolete]
            public override void OnError(string utteranceId) { Error = "it failed"; Done.Set(); }
            public override void OnError(string utteranceId, TextToSpeechError errorCode) { Error = $"it failed ({errorCode})"; Done.Set(); }
        }

        public void SpeakToWav(string text, double rate, string wavPath)
        {
            TextToSpeech engine = Engine(wait: true);
            Progress progress = new Progress();
            lock (sync)
            {
                engine.SetOnUtteranceProgressListener(progress);
                engine.SetSpeechRate((float)rate);
                string id = Guid.NewGuid().ToString("N");
                OperationResult queued = engine.SynthesizeToFile(text, new Bundle(), new Java.IO.File(wavPath), id);
                if (queued != OperationResult.Success)
                    throw new ArgumentException("Android's text to speech didn't take the text");
            }
            if (!progress.Done.Wait(Speech.Timeout))
                throw new ArgumentException($"Android's text to speech took longer than {Speech.Timeout.TotalSeconds:F0} s");
            if (progress.Error != null)
                throw new ArgumentException($"Android's text to speech: {progress.Error}");
        }
    } // public sealed class AndroidSpeech
}
