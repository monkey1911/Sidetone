// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Shared core of both front ends.
*
* The audio device layer. Everything in Sidetone that plays or records sound, or lists sound
* devices, goes through IAudioBackend, so a platform needs only its own backend: PipeWire on Linux
* today; WASAPI on Windows and Core Audio on macOS later. The format is always 8 kHz, 16-bit, mono,
* in 20 ms (160-sample) blocks; a backend converts to and from whatever the device wants.
*/

namespace Sidetone.Core
{
    /// <summary>Somewhere to play audio: one per stream (one per channel, one for a replay).</summary>
    public interface IAudioOutput : IAudioSink
    {
        /// <summary>Raised once if playback stops working, with the reason.</summary>
        event Action<string> Failed;
    }

    /// <summary>A microphone being recorded.</summary>
    public interface IAudioInput : IDisposable
    {
        /// <summary>Raised on the backend's own thread with each 160-sample block.</summary>
        event Action<short[]> Block;
        /// <summary>Raised once if recording stops working, with the reason.</summary>
        event Action<string> Failed;
    }

    /// <summary>A platform's sound system.</summary>
    public interface IAudioBackend
    {
        /// <summary>Its name, as people know it ("PipeWire").</summary>
        string Name { get; }

        /// <summary>Lists output and input devices and the defaults. Throws InvalidOperationException, saying why, when it can't.</summary>
        AudioDeviceList ListDevices();

        /// <summary>Starts playing to a device (as its <see cref="AudioDevice.Name"/>), or the default when null.</summary>
        IAudioOutput OpenOutput(string device);

        /// <summary>Starts recording from a device (as its <see cref="AudioDevice.Name"/>), or the default when null.</summary>
        IAudioInput OpenInput(string device);

        /// <summary>What's missing for playback and recording to work on this machine, to warn about at startup; empty when nothing is.</summary>
        IReadOnlyList<string> Problems();
    }

    /// <summary>The backend in use.</summary>
    public static class AudioBackend
    {
        private static IAudioBackend current;

        /// <summary>The platform's backend (settable for tests).</summary>
        public static IAudioBackend Current
        {
            get => current ??= ForPlatform();
            set => current = value;
        }

        private static IAudioBackend ForPlatform() =>
            OperatingSystem.IsLinux() ? new PipeWireBackend()
            : OperatingSystem.IsMacOS() ? new PortAudioBackend()
            : OperatingSystem.IsWindows() ? new WasapiBackend()
            : new NoAudioBackend($"audio isn't supported on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} yet");
    } // public static class AudioBackend

    /// <summary>PipeWire, through its command-line tools (pw-play, pw-record, pw-dump).</summary>
    public sealed class PipeWireBackend : IAudioBackend
    {
        public string Name => "PipeWire";

        public AudioDeviceList ListDevices() => AudioDeviceList.QueryPipeWire();

        public IAudioOutput OpenOutput(string device) => new PipeWireSink(target: device);

        public IAudioInput OpenInput(string device) => new PipeWireCapture(target: device);

        public IReadOnlyList<string> Problems()
        {
            List<string> problems = new List<string>();
            if (PipeWireSink.FindPlayer() == null)
                problems.Add("pw-play not found; you'll see activity but hear nothing");
            if (PipeWireCapture.FindRecorder() == null)
                problems.Add("pw-record not found; transmit won't work");
            return problems;
        }
    } // public sealed class PipeWireBackend

    /// <summary>For a platform without a backend yet: lists nothing, and refuses to open devices, saying why.</summary>
    public sealed class NoAudioBackend : IAudioBackend
    {
        private readonly string why;

        public NoAudioBackend(string why) => this.why = why;

        public string Name => "no audio";
        public AudioDeviceList ListDevices() => throw new InvalidOperationException(why);
        public IAudioOutput OpenOutput(string device) => throw new InvalidOperationException(why);
        public IAudioInput OpenInput(string device) => throw new InvalidOperationException(why);
        public IReadOnlyList<string> Problems() => new[] { why };
    } // public sealed class NoAudioBackend
}
