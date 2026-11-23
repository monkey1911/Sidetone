// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Audio on Windows: WASAPI through NAudio (MIT; the library the Windows console uses). Devices are the
* active endpoints; a device's Name is its endpoint ID (stable across reboots, like PipeWire's node.name,
* so it's what Settings saves) and its Description the name Windows shows.
*
* Shared mode wants the device's own mix format, so Sidetone converts to it itself rather than relying on
* a converter that may differ between Windows versions: an output takes 8 kHz mono and resamples it up
* (WavFile.StreamResampler) to the mix rate and channel count, as 32-bit float; the microphone's mix format
* is mixed to mono and resampled down to 8 kHz in 160-sample blocks. Writes are paced like pw-play's: the
* writer waits while more than half a second is queued.
*/

using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// shortcut: WasapiOut and WasapiCapture are marked obsolete in NAudio 3.3 (for WasapiPlayer/WasapiRecorder builders);
// they're the classes the Windows console runs on and still work. Move to the builders once they've been tried on hardware.
#pragma warning disable CS0618

namespace Sidetone.Core
{
    [SupportedOSPlatform("windows")]
    public sealed class WasapiBackend : IAudioBackend
    {
        public const int Rate = 8000;

        public string Name => "Windows audio";

        public AudioDeviceList ListDevices()
        {
            AudioDeviceList list = new AudioDeviceList();
            try
            {
                using MMDeviceEnumerator en = new MMDeviceEnumerator();
                uint id = 1;
                foreach (MMDevice d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    list.Outputs.Add(new AudioDevice { Id = id++, Name = d.ID, Description = d.FriendlyName });
                foreach (MMDevice d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    list.Inputs.Add(new AudioDevice { Id = id++, Name = d.ID, Description = d.FriendlyName });
                if (en.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out MMDevice output))
                    list.DefaultOutputName = output.ID;
                if (en.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out MMDevice input))
                    list.DefaultInputName = input.ID;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                throw new InvalidOperationException($"Windows didn't list the audio devices ({ex.Message})");
            }
            return list;
        }

        /// <summary>The endpoint for a saved ID, or the default (multimedia for output, communications for the microphone).</summary>
        internal static MMDevice Find(string id, DataFlow flow)
        {
            using MMDeviceEnumerator en = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    MMDevice d = en.GetDevice(id);
                    if (d.State == DeviceState.Active)
                        return d;
                }
                catch (System.Runtime.InteropServices.COMException) { /* not there now: the default plays instead */ }
            }
            if (en.TryGetDefaultAudioEndpoint(flow, flow == DataFlow.Render ? Role.Multimedia : Role.Communications, out MMDevice fallback))
                return fallback;
            throw new InvalidOperationException(flow == DataFlow.Render ? "there's no audio output" : "there's no microphone");
        }

        public IAudioOutput OpenOutput(string device) => new WasapiOutput(Find(device, DataFlow.Render));

        public IAudioInput OpenInput(string device) => new WasapiInput(Find(device, DataFlow.Capture));

        public IReadOnlyList<string> Problems()
        {
            List<string> problems = new List<string>();
            try
            {
                using MMDeviceEnumerator en = new MMDeviceEnumerator();
                if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    problems.Add("there's no audio output; you'll see activity but hear nothing");
                if (!en.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
                    problems.Add("there's no microphone; transmit won't work");
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                problems.Add($"Windows audio didn't answer ({ex.Message})");
            }
            return problems;
        }

        /// <summary>Reads one sample of a device format (32-bit float, or 16/24/32-bit integer).</summary>
        internal static float Sample(ReadOnlySpan<byte> b, int at, WaveFormat f) => f.BitsPerSample switch
        {
            32 when f.Encoding == WaveFormatEncoding.IeeeFloat || f.Encoding == WaveFormatEncoding.Extensible => BitConverter.ToSingle(b.Slice(at, 4)),
            32 => BitConverter.ToInt32(b.Slice(at, 4)) / 2147483648f,
            24 => ((b[at] | b[at + 1] << 8 | (sbyte)b[at + 2] << 16)) / 8388608f,
            16 => BitConverter.ToInt16(b.Slice(at, 2)) / 32768f,
            _ => 0f
        };
    } // public sealed class WasapiBackend

    /// <summary>One output: 8 kHz mono in, the device's mix format out (see the file's notes).</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WasapiOutput : IAudioOutput, IWaveProvider
    {
        private readonly object sync = new object();
        private readonly float[] ring;      // mono, at the device's rate
        private int head, count;
        private readonly int maxQueued, channels;
        private readonly WasapiOut player;
        private readonly WavFile.StreamResampler up;
        private bool disposed;

        public event Action<string> Failed;
        public WaveFormat WaveFormat { get; }

        public WasapiOutput(MMDevice device)
        {
            WaveFormat mix;
            using (AudioClient client = device.CreateAudioClient())
                mix = client.MixFormat;
            channels = Math.Max(1, mix.Channels);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, channels);
            up = mix.SampleRate == WasapiBackend.Rate ? null : new WavFile.StreamResampler(WasapiBackend.Rate, mix.SampleRate);
            maxQueued = mix.SampleRate / 2;
            ring = new float[mix.SampleRate * 2];
            try
            {
                player = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
                player.PlaybackStopped += (s, e) =>
                {
                    if (e.Exception != null && !disposed)
                        Failed?.Invoke($"the output stopped ({e.Exception.Message})");
                };
                player.Init(this);
                player.Play();
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                player?.Dispose();
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
                DateTime until = DateTime.UtcNow.AddSeconds(1);
                while (count > maxQueued && !disposed && DateTime.UtcNow < until)
                    Monitor.Wait(sync, 50);
                if (disposed)
                    return;
                foreach (float f in mono)
                {
                    if (count == ring.Length)
                    {
                        head = (head + 1) % ring.Length;
                        count--;
                    }
                    ring[(head + count) % ring.Length] = f;
                    count++;
                }
            }
        }

        /// <summary>WASAPI asks for audio: what's queued, silence after it (it never ends).</summary>
        public int Read(Span<byte> buffer)
        {
            int frames = buffer.Length / (4 * channels);
            lock (sync)
            {
                for (int i = 0; i < frames; i++)
                {
                    float f = 0;
                    if (count > 0)
                    {
                        f = ring[head];
                        head = (head + 1) % ring.Length;
                        count--;
                    }
                    for (int c = 0; c < channels; c++)
                        BitConverter.TryWriteBytes(buffer.Slice((i * channels + c) * 4, 4), f);
                }
                Monitor.PulseAll(sync);
            }
            return frames * 4 * channels;
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
            try { player.Stop(); }
            catch (System.Runtime.InteropServices.COMException) { /* closing anyway */ }
            player.Dispose();
        }
    } // internal sealed class WasapiOutput

    /// <summary>The microphone, in its mix format, mixed to mono and taken to 8 kHz in 160-sample blocks.</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WasapiInput : IAudioInput
    {
        private const int BlockSize = 160;
        private readonly WasapiCapture capture;
        private readonly WavFile.StreamResampler down;
        private readonly short[] block = new short[BlockSize];
        private int filled;
        private bool disposed;

        public event Action<short[]> Block;
        public event Action<string> Failed;

        public WasapiInput(MMDevice device)
        {
            try
            {
                capture = new WasapiCapture(device, true, 50);
                WaveFormat f = capture.WaveFormat;
                down = f.SampleRate == WasapiBackend.Rate ? null : new WavFile.StreamResampler(f.SampleRate, WasapiBackend.Rate);
                capture.DataAvailable += (s, e) => Take(e.BufferSpan.Slice(0, e.BytesRecorded), f);
                capture.RecordingStopped += (s, e) =>
                {
                    if (e.Exception != null && !disposed)
                        Failed?.Invoke(e.Exception is UnauthorizedAccessException || e.Exception.HResult == unchecked((int)0x80070005)
                            ? "Windows didn't let Sidetone use the microphone (Settings, Privacy & security, Microphone, let desktop apps access it)"
                            : $"the microphone stopped ({e.Exception.Message})");
                };
                capture.StartRecording();
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                capture?.Dispose();
                throw new InvalidOperationException($"the microphone didn't open ({ex.Message})");
            }
        }

        private void Take(ReadOnlySpan<byte> data, WaveFormat f)
        {
            int bytesPerSample = f.BitsPerSample / 8, frameBytes = bytesPerSample * f.Channels;
            if (frameBytes == 0)
                return;
            float[] mono = new float[data.Length / frameBytes];
            for (int i = 0; i < mono.Length; i++)
            {
                float sum = 0;
                for (int c = 0; c < f.Channels; c++)
                    sum += WasapiBackend.Sample(data, i * frameBytes + c * bytesPerSample, f);
                mono[i] = sum / f.Channels;
            }
            foreach (float s in down != null ? down.Push(mono) : mono)
            {
                block[filled++] = (short)Math.Clamp(Math.Round(s * 32767.0), -32768, 32767);
                if (filled == BlockSize)
                {
                    Block?.Invoke((short[])block.Clone());
                    filled = 0;
                }
            }
        }

        public void Dispose()
        {
            disposed = true;
            try { capture.StopRecording(); }
            catch (System.Runtime.InteropServices.COMException) { /* closing anyway */ }
            capture.Dispose();
        }
    } // internal sealed class WasapiInput
}
