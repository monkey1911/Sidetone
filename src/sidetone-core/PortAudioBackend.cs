// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Audio on macOS: PortAudio (PortAudioSharp2, Apache-2.0, with PortAudio's own library for Intel and Apple
* silicon) over Core Audio. Devices are named as Core Audio names them; the name is what Settings saves.
*
* Each output is a stream of its own, opened in stereo (Core Audio puts a mono stream on the left speaker
* only) at 8 kHz when the device takes that rate, otherwise at the device's own rate with Sidetone's
* resampler. Write queues the audio for the stream's callback and waits while more than half a second is
* queued, so a fast writer (a web stream's burst) is paced as pw-play paces it. The microphone is read the
* same way, mixed to mono and taken to 8 kHz in 160-sample blocks.
*
* Nothing here is macOS-only code, but only macOS uses it (Linux has PipeWire, Windows WASAPI).
*/

using PortAudioSharp;

namespace Sidetone.Core
{
    public sealed class PortAudioBackend : IAudioBackend
    {
        public const int Rate = 8000;
        private static readonly object initLock = new object();
        private static bool ready;

        public string Name => "Core Audio";

        /// <summary>Starts PortAudio once per process. Throws InvalidOperationException, saying why, when it can't.</summary>
        internal static void Init()
        {
            lock (initLock)
            {
                if (ready)
                    return;
                try
                {
                    PortAudio.LoadNativeLibrary();
                    PortAudio.Initialize();
                    ready = true;
                }
                catch (Exception ex) when (ex is DllNotFoundException || ex is PortAudioException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
                {
                    throw new InvalidOperationException($"the audio library didn't start ({ex.Message})");
                }
            }
        }

        public AudioDeviceList ListDevices()
        {
            Init();
            AudioDeviceList list = new AudioDeviceList();
            for (int i = 0; i < PortAudio.DeviceCount; i++)
            {
                DeviceInfo d = PortAudio.GetDeviceInfo(i);
                string name = d.name?.Trim();
                if (string.IsNullOrEmpty(name))
                    continue;
                if (d.maxOutputChannels > 0 && !list.Outputs.Any(o => o.Name == name))
                    list.Outputs.Add(new AudioDevice { Id = (uint)i + 1, Name = name, Description = name });
                if (d.maxInputChannels > 0 && !list.Inputs.Any(o => o.Name == name))
                    list.Inputs.Add(new AudioDevice { Id = (uint)i + 1, Name = name, Description = name });
            }
            if (PortAudio.DefaultOutputDevice != PortAudio.NoDevice)
                list.DefaultOutputName = PortAudio.GetDeviceInfo(PortAudio.DefaultOutputDevice).name?.Trim();
            if (PortAudio.DefaultInputDevice != PortAudio.NoDevice)
                list.DefaultInputName = PortAudio.GetDeviceInfo(PortAudio.DefaultInputDevice).name?.Trim();
            return list;
        }

        /// <summary>The device index for a saved name (null: the default). A name that isn't there now falls back to the default.</summary>
        private static int Find(string name, bool output)
        {
            Init();
            if (!string.IsNullOrEmpty(name))
                for (int i = 0; i < PortAudio.DeviceCount; i++)
                {
                    DeviceInfo d = PortAudio.GetDeviceInfo(i);
                    if ((output ? d.maxOutputChannels : d.maxInputChannels) > 0 && d.name?.Trim() == name)
                        return i;
                }
            int fallback = output ? PortAudio.DefaultOutputDevice : PortAudio.DefaultInputDevice;
            if (fallback == PortAudio.NoDevice)
                throw new InvalidOperationException(output ? "there's no audio output" : "there's no microphone");
            return fallback;
        }

        public IAudioOutput OpenOutput(string device) => new PortAudioOutput(Find(device, output: true));

        public IAudioInput OpenInput(string device) => new PortAudioInput(Find(device, output: false));

        public IReadOnlyList<string> Problems()
        {
            try
            {
                Init();
                List<string> problems = new List<string>();
                if (PortAudio.DefaultOutputDevice == PortAudio.NoDevice)
                    problems.Add("there's no audio output; you'll see activity but hear nothing");
                if (PortAudio.DefaultInputDevice == PortAudio.NoDevice)
                    problems.Add("there's no microphone; transmit won't work");
                return problems;
            }
            catch (InvalidOperationException ex)
            {
                return new[] { ex.Message };
            }
        }

        /// <summary>
        /// Opens a stream at 8 kHz if the device takes it, else at the device's own rate. Returns the rate it opened at.
        /// </summary>
        internal static (PortAudioSharp.Stream Stream, int Rate) Open(int device, bool output, int channels, PortAudioSharp.Stream.Callback callback)
        {
            DeviceInfo info = PortAudio.GetDeviceInfo(device);
            StreamParameters p = new StreamParameters
            {
                device = device,
                channelCount = channels,
                sampleFormat = SampleFormat.Float32,
                suggestedLatency = output ? info.defaultHighOutputLatency : info.defaultHighInputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero
            };
            StreamParameters? inP = output ? null : p, outP = output ? p : null;
            try
            {
                return (new PortAudioSharp.Stream(inP, outP, Rate, PortAudio.FramesPerBufferUnspecified, StreamFlags.ClipOff, callback, null), Rate);
            }
            catch (PortAudioException ex) when (ex.ErrorCode == ErrorCode.InvalidSampleRate)
            {
                int own = (int)Math.Round(info.defaultSampleRate);
                return (new PortAudioSharp.Stream(inP, outP, own, PortAudio.FramesPerBufferUnspecified, StreamFlags.ClipOff, callback, null), own);
            }
        }
    } // public sealed class PortAudioBackend

    /// <summary>One output stream, fed from a queue (see the file's notes).</summary>
    internal sealed class PortAudioOutput : IAudioOutput
    {
        private const int Channels = 2;
        private readonly object sync = new object();
        private readonly float[] ring;          // mono samples at the stream's rate
        private int head, count;
        private readonly int maxQueued;
        private readonly PortAudioSharp.Stream stream;
        private readonly PortAudioSharp.Stream.Callback callback;     // kept: the native side calls it
        private readonly WavFile.StreamResampler up;
        private bool disposed, failed;

        public event Action<string> Failed;

        public PortAudioOutput(int device)
        {
            callback = Pull;
            try
            {
                (stream, int rate) = PortAudioBackend.Open(device, output: true, Channels, callback);
                up = rate == PortAudioBackend.Rate ? null : new WavFile.StreamResampler(PortAudioBackend.Rate, rate);
                maxQueued = rate / 2;
                ring = new float[rate * 2];
                stream.Start();
            }
            catch (PortAudioException ex)
            {
                stream?.Dispose();
                throw new InvalidOperationException($"the output didn't open ({ex.Message})");
            }
        }

        public void Write(short[] samples)
        {
            float[] mono = new float[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                mono[i] = samples[i] / 32768f;
            if (up != null)
                mono = up.Push(mono);
            lock (sync)
            {
                // more than half a second waiting: hold the writer until the callback catches up (a second at most)
                DateTime until = DateTime.UtcNow.AddSeconds(1);
                while (count > maxQueued && !disposed && DateTime.UtcNow < until)
                    Monitor.Wait(sync, 50);
                if (disposed)
                    return;
                foreach (float f in mono)
                {
                    if (count == ring.Length)
                    {
                        head = (head + 1) % ring.Length;    // still full (the device stalled): the oldest goes
                        count--;
                    }
                    ring[(head + count) % ring.Length] = f;
                    count++;
                }
            }
        }

        private unsafe StreamCallbackResult Pull(IntPtr input, IntPtr output, uint frames, ref StreamCallbackTimeInfo time, StreamCallbackFlags flags, IntPtr user)
        {
            float* o = (float*)output;
            lock (sync)
            {
                for (uint i = 0; i < frames; i++)
                {
                    float f = 0;
                    if (count > 0)
                    {
                        f = ring[head];
                        head = (head + 1) % ring.Length;
                        count--;
                    }
                    o[i * Channels] = f;
                    o[i * Channels + 1] = f;
                }
                Monitor.PulseAll(sync);
            }
            return StreamCallbackResult.Continue;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;
                Monitor.PulseAll(sync);
            }
            try { stream.Stop(); }
            catch (PortAudioException ex) when (!failed) { failed = true; Failed?.Invoke($"the output didn't close cleanly ({ex.Message})"); }
            stream.Dispose();
        }
    } // internal sealed class PortAudioOutput

    /// <summary>The microphone: mixed to mono, taken to 8 kHz, and raised in 160-sample blocks on a thread of its own.</summary>
    internal sealed class PortAudioInput : IAudioInput
    {
        private const int BlockSize = 160;
        private readonly PortAudioSharp.Stream stream;
        private readonly PortAudioSharp.Stream.Callback callback;
        private readonly WavFile.StreamResampler down;
        private readonly int channels;
        private readonly object sync = new object();
        private readonly Queue<float[]> arrived = new Queue<float[]>();
        private readonly Thread thread;
        private volatile bool stopping;

        public event Action<short[]> Block;
        public event Action<string> Failed;

        public PortAudioInput(int device)
        {
            callback = Push;
            channels = Math.Min(2, Math.Max(1, PortAudio.GetDeviceInfo(device).maxInputChannels));
            try
            {
                (stream, int rate) = PortAudioBackend.Open(device, output: false, channels, callback);
                down = rate == PortAudioBackend.Rate ? null : new WavFile.StreamResampler(rate, PortAudioBackend.Rate);
                stream.Start();
            }
            catch (PortAudioException ex)
            {
                stream?.Dispose();
                throw new InvalidOperationException($"the microphone didn't open ({ex.Message})");
            }
            thread = new Thread(Deliver) { IsBackground = true, Name = "microphone" };
            thread.Start();
        }

        /// <summary>The audio callback: copies the frames out (mixed to mono) and returns at once.</summary>
        private unsafe StreamCallbackResult Push(IntPtr input, IntPtr output, uint frames, ref StreamCallbackTimeInfo time, StreamCallbackFlags flags, IntPtr user)
        {
            if (input == IntPtr.Zero)
                return StreamCallbackResult.Continue;
            float* i = (float*)input;
            float[] mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                    sum += i[f * channels + c];
                mono[f] = sum / channels;
            }
            lock (sync)
            {
                arrived.Enqueue(mono);
                Monitor.Pulse(sync);
            }
            return StreamCallbackResult.Continue;
        }

        private void Deliver()
        {
            short[] block = new short[BlockSize];
            int filled = 0;
            try
            {
                while (!stopping)
                {
                    float[] piece;
                    lock (sync)
                    {
                        while (arrived.Count == 0 && !stopping)
                            Monitor.Wait(sync, 200);
                        if (stopping)
                            return;
                        piece = arrived.Dequeue();
                    }
                    foreach (float f in down != null ? down.Push(piece) : piece)
                    {
                        block[filled++] = (short)Math.Clamp(Math.Round(f * 32767.0), -32768, 32767);
                        if (filled == BlockSize)
                        {
                            Block?.Invoke((short[])block.Clone());
                            filled = 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Failed?.Invoke($"the microphone stopped ({ex.Message})");
            }
        }

        public void Dispose()
        {
            stopping = true;
            lock (sync)
                Monitor.PulseAll(sync);
            try { stream.Stop(); }
            catch (PortAudioException) { /* closing anyway */ }
            stream.Dispose();
            thread.Join(TimeSpan.FromSeconds(1));
        }
    } // internal sealed class PortAudioInput
}
