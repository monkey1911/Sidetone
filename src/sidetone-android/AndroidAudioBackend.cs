// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone for Android. Audio through Android's own AudioTrack (one per stream, like pw-play) and
* AudioRecord (the microphone, with the platform's voice-call processing). The format is the core's:
* 8 kHz, 16-bit, mono, in 160-sample blocks; Android converts for the device.
*
* Devices are Android's (speaker, earpiece, wired, USB, Bluetooth), named by type and product so
* a saved choice finds the same device again; a stream is routed there with setPreferredDevice.
*/

using Android;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using AndroidX.Core.Content;

namespace Sidetone.Droid
{
    public sealed class AndroidAudioBackend : IAudioBackend
    {
        public const int Rate = 8000;

        private readonly Context context;
        private readonly AudioManager manager;

        public AndroidAudioBackend(Context context)
        {
            this.context = context;
            manager = (AudioManager)context.GetSystemService(Context.AudioService);
        }

        public string Name => "Android";

        /// <summary>A device's stable name: its type and product, like "BluetoothSco:Pixel Buds".</summary>
        internal static string NameOf(AudioDeviceInfo d) => $"{d.Type}:{d.ProductName}";

        private static string Describe(AudioDeviceInfo d)
        {
            string kind = d.Type switch
            {
                AudioDeviceType.BuiltinSpeaker => "Speaker",
                AudioDeviceType.BuiltinEarpiece => "Earpiece",
                AudioDeviceType.BuiltinMic => "Microphone",
                AudioDeviceType.WiredHeadset => "Wired headset",
                AudioDeviceType.WiredHeadphones => "Wired headphones",
                AudioDeviceType.UsbHeadset => "USB headset",
                AudioDeviceType.UsbDevice => "USB audio",
                AudioDeviceType.BluetoothSco => "Bluetooth (calls)",
                AudioDeviceType.BluetoothA2dp => "Bluetooth (media)",
                _ => d.Type.ToString()
            };
            string product = d.ProductName?.ToString();
            return string.IsNullOrWhiteSpace(product) || d.Type is AudioDeviceType.BuiltinSpeaker or AudioDeviceType.BuiltinEarpiece or AudioDeviceType.BuiltinMic
                ? kind : $"{kind}: {product}";
        }

        private static bool Useful(AudioDeviceInfo d) => d.Type is AudioDeviceType.BuiltinSpeaker or AudioDeviceType.BuiltinEarpiece or AudioDeviceType.BuiltinMic or
            AudioDeviceType.WiredHeadset or AudioDeviceType.WiredHeadphones or AudioDeviceType.UsbHeadset or AudioDeviceType.UsbDevice or
            AudioDeviceType.BluetoothSco or AudioDeviceType.BluetoothA2dp or AudioDeviceType.LineAnalog or AudioDeviceType.LineDigital;

        public AudioDeviceList ListDevices()
        {
            AudioDeviceList list = new AudioDeviceList();
            uint id = 1;
            foreach (AudioDeviceInfo d in manager.GetDevices(GetDevicesTargets.Outputs).Where(Useful))
                if (!list.Outputs.Any(o => o.Name == NameOf(d)))
                    list.Outputs.Add(new AudioDevice { Id = id++, Name = NameOf(d), Description = Describe(d) });
            foreach (AudioDeviceInfo d in manager.GetDevices(GetDevicesTargets.Inputs).Where(Useful))
                if (!list.Inputs.Any(o => o.Name == NameOf(d)))
                    list.Inputs.Add(new AudioDevice { Id = id++, Name = NameOf(d), Description = Describe(d) });
            return list;
        }

        /// <summary>The device a saved name means, or null for Android's own choice.</summary>
        private AudioDeviceInfo Find(string name, GetDevicesTargets kind) =>
            string.IsNullOrEmpty(name) ? null : manager.GetDevices(kind).FirstOrDefault(d => NameOf(d) == name);

        public IAudioOutput OpenOutput(string device) => new AndroidOutput(Find(device, GetDevicesTargets.Outputs));

        public IAudioInput OpenInput(string device)
        {
            if (ContextCompat.CheckSelfPermission(context, Manifest.Permission.RecordAudio) != Permission.Granted)
                throw new InvalidOperationException("Sidetone isn't allowed to use the microphone (Android settings, Apps, Sidetone, Permissions)");
            return new AndroidInput(Find(device, GetDevicesTargets.Inputs));
        }

        public IReadOnlyList<string> Problems()
        {
            List<string> problems = new List<string>();
            if (ContextCompat.CheckSelfPermission(context, Manifest.Permission.RecordAudio) != Permission.Granted)
                problems.Add("the microphone isn't allowed yet: transmit needs it (Sidetone asks when a console starts)");
            return problems;
        }
    } // public sealed class AndroidAudioBackend

    /// <summary>One stream played with AudioTrack.</summary>
    internal sealed class AndroidOutput : IAudioOutput
    {
        private readonly AudioTrack track;
        private readonly object sync = new object();
        private bool playing, disposed, failed;

        public event Action<string> Failed;

        public AndroidOutput(AudioDeviceInfo device)
        {
            int min = AudioTrack.GetMinBufferSize(AndroidAudioBackend.Rate, ChannelOut.Mono, Encoding.Pcm16bit);
            int size = Math.Max(min, AndroidAudioBackend.Rate * 2 / 5);    // 200 ms
            track = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Media)
                    .SetContentType(AudioContentType.Speech)
                    .Build())
                .SetAudioFormat(new AudioFormat.Builder()
                    .SetSampleRate(AndroidAudioBackend.Rate)
                    .SetChannelMask(ChannelOut.Mono)
                    .SetEncoding(Encoding.Pcm16bit)
                    .Build())
                .SetBufferSizeInBytes(size)
                .SetTransferMode(AudioTrackMode.Stream)
                .SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)
                .Build();
            if (device != null)
                track.SetPreferredDevice(device);
        }

        public void Write(short[] samples)
        {
            lock (sync)
            {
                if (disposed || failed)
                    return;
                try
                {
                    if (!playing)
                    {
                        track.Play();
                        playing = true;
                    }
                    int written = track.Write(samples, 0, samples.Length, WriteMode.Blocking);
                    if (written < 0)
                        Fail($"audio output error {written}");
                }
                catch (Exception ex) when (ex is Java.Lang.IllegalStateException || ex is InvalidOperationException)
                {
                    Fail(ex.Message);
                }
            }
        }

        private void Fail(string why)
        {
            failed = true;
            Failed?.Invoke(why);
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;
                try
                {
                    if (playing)
                        track.Stop();
                }
                catch (Java.Lang.IllegalStateException) { }
                track.Release();
            }
        }
    } // internal sealed class AndroidOutput

    /// <summary>The microphone, read with AudioRecord on a thread of its own, 160 samples at a time.</summary>
    internal sealed class AndroidInput : IAudioInput
    {
        private const int BlockSize = 160;
        private readonly AudioRecord record;
        private readonly Thread thread;
        private volatile bool stopping;

        public event Action<short[]> Block;
        public event Action<string> Failed;

        public AndroidInput(AudioDeviceInfo device)
        {
            int min = AudioRecord.GetMinBufferSize(AndroidAudioBackend.Rate, ChannelIn.Mono, Encoding.Pcm16bit);
            record = new AudioRecord(AudioSource.VoiceCommunication, AndroidAudioBackend.Rate, ChannelIn.Mono, Encoding.Pcm16bit,
                                     Math.Max(min, AndroidAudioBackend.Rate * 2 / 5));
            if (record.State != State.Initialized)
            {
                record.Release();
                throw new InvalidOperationException("the microphone couldn't be opened");
            }
            if (device != null)
                record.SetPreferredDevice(device);
            thread = new Thread(Run) { IsBackground = true, Name = "sidetone-mic" };
            record.StartRecording();
            thread.Start();
        }

        private void Run()
        {
            short[] buffer = new short[BlockSize];
            int filled = 0;
            try
            {
                while (!stopping)
                {
                    int n = record.Read(buffer, filled, BlockSize - filled);
                    if (n < 0)
                    {
                        if (!stopping)
                            Failed?.Invoke($"microphone error {n}");
                        return;
                    }
                    filled += n;
                    if (filled < BlockSize)
                        continue;
                    Block?.Invoke((short[])buffer.Clone());
                    filled = 0;
                }
            }
            catch (Exception ex) when (ex is Java.Lang.IllegalStateException || ex is InvalidOperationException)
            {
                if (!stopping)
                    Failed?.Invoke(ex.Message);
            }
        }

        public void Dispose()
        {
            if (stopping)
                return;
            stopping = true;
            try { record.Stop(); } catch (Java.Lang.IllegalStateException) { }
            thread.Join(500);
            record.Release();
        }
    } // internal sealed class AndroidInput
}
