// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* What the console uses of its own transmission, whichever mode the channel is: P25 (P25Transmitter) or
* DMR (DmrTransmitter). Tones, announcements and patches still send through P25Transmitter only.
*/

namespace Sidetone.Core
{
    public interface IVoiceTransmitter : IDisposable
    {
        /// <summary>The codec of <see cref="ClearFrame"/>.</summary>
        VoiceCodec Codec { get; }
        uint SourceId { get; }
        uint Talkgroup { get; }
        byte AlgorithmId { get; }
        ushort KeyId { get; }
        bool Encrypted { get; }
        bool IsTransmitting { get; }
        uint StreamId { get; }
        /// <summary>When the transmission started (UTC).</summary>
        DateTime StartedAt { get; }
        /// <summary>Voice frames (20 ms each) sent so far.</summary>
        int FramesSent { get; }

        /// <summary>Raised with each clear voice codeword as it is encoded (Instant recall keeps them).</summary>
        event Action<byte[]> ClearFrame;

        /// <summary>Starts a call; false with a plain reason when it can't.</summary>
        bool Start(out string error);
        /// <summary>One 20 ms block of microphone audio (160 samples at 8 kHz).</summary>
        void PushAudio(short[] samples);
        /// <summary>Ends the call.</summary>
        void Stop();
    } // public interface IVoiceTransmitter
}
