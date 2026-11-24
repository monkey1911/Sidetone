// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* What the console uses of a listening channel's receiver, whichever mode it is: P25 (IMBE voice,
* P25VoiceReceiver) or DMR (AMBE voice, DmrVoiceReceiver).
*/

namespace Sidetone.Core
{
    /// <summary>The voice codec a channel's calls are kept in (Instant recall, recordings, the history).</summary>
    public enum VoiceCodec
    {
        /// <summary>P25: 88-bit IMBE, 11 bytes per 20 ms.</summary>
        Imbe,
        /// <summary>DMR: AMBE+2, 9 bytes per 20 ms.</summary>
        Ambe
    }

    /// <summary>A listening channel's receiver: decodes its calls into audio and reports them.</summary>
    public interface IVoiceReceiver : IDisposable
    {
        /// <summary>Raised with each decoded 20 ms block of PCM.</summary>
        event Action<short[]> Audio;
        /// <summary>Raised with a status line for the user (call start/end, warnings).</summary>
        event Action<string> Status;
        /// <summary>Raised once per call when an encrypted call needs a key the store doesn't have (P25 only).</summary>
        event Action<byte, ushort> KeyNeeded;
        /// <summary>Raised with each clear voice codeword (in <see cref="Codec"/>) just before it is decoded.</summary>
        event Action<byte[]> Codeword;
        /// <summary>Raised when a call starts (source radio ID).</summary>
        event Action<uint> CallStarted;
        /// <summary>Raised when a call ends: source, duration in seconds, frames played, frames muted, crypto description.</summary>
        event Action<uint, double, int, int, string> CallEnded;

        /// <summary>The codec of <see cref="Codeword"/>.</summary>
        VoiceCodec Codec { get; }
        /// <summary>True while a call is in progress.</summary>
        bool InCall { get; }
        /// <summary>Source radio of the current (or last) call.</summary>
        uint CurrentSource { get; }
        /// <summary>Network stream ID of the call in progress (0 when idle).</summary>
        uint CurrentStreamId { get; }
        /// <summary>Algorithm of the current call (0 = not known yet or clear on DMR; 0x80 = clear on P25).</summary>
        byte CurrentAlgorithm { get; }
        /// <summary>Key ID of the current call.</summary>
        ushort CurrentKeyId { get; }
        /// <summary>When the current call started.</summary>
        DateTime CallStartedAt { get; }
        /// <summary>True if any voice in the current call has been muted (no key, encrypted DMR, or before sync).</summary>
        bool CallHasMuted { get; }
        /// <summary>The clock for <see cref="EndIfIdle"/> (UTC); the console's, so tests can step it.</summary>
        Func<DateTime> Clock { get; set; }
        /// <summary>Names a radio ID in status messages (aliases); the bare number by default.</summary>
        Func<uint, string> DescribeSource { get; set; }

        /// <summary>Ends the call in progress when nothing has arrived for it for <paramref name="after"/> (its end was lost).</summary>
        bool EndIfIdle(TimeSpan after);
    } // public interface IVoiceReceiver
}
