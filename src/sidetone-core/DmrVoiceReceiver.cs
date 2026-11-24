// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* DMR receive for one listening channel: group calls on its talkgroup and timeslot, read through fnecore's
* DMRFrame (the owner's dvmconsole fork does the same), each voice burst's three AMBE+2 codewords decoded by
* libvocoder. Encrypted DMR isn't decoded (nor by the Windows console): the call shows and is muted.
*/

using dvmconsole;
using fnecore;
using fnecore.DMR;

namespace Sidetone.Core
{
    public sealed class DmrVoiceReceiver : IVoiceReceiver
    {
        /// <summary>Length of one AMBE codeword (20 ms).</summary>
        public const int CodewordLength = DMRFrame.CodewordBytes;

        private readonly uint talkgroup;
        private readonly byte slot;
        private readonly object sync = new object();
        private MBEDecoder decoder;
        private DMRCallData call;

        private uint streamId;
        private uint sourceId;
        private DateTime callStarted;
        private DateTime lastFrame;
        private int callFrames;
        private int callMuted;
        private bool encryptedAnnounced;

        public event Action<short[]> Audio;
        public event Action<string> Status;
        public event Action<byte, ushort> KeyNeeded { add { } remove { } }     // no DMR keys yet
        public event Action<byte[]> Codeword;
        public event Action<uint> CallStarted;
        public event Action<uint, double, int, int, string> CallEnded;

        public VoiceCodec Codec => VoiceCodec.Ambe;
        public bool InCall => streamId != 0;
        public uint CurrentSource => sourceId;
        public uint CurrentStreamId => streamId;
        public byte CurrentAlgorithm => call != null && call.IsEncrypted ? (call.AlgorithmId != 0 ? call.AlgorithmId : (byte)0xFF) : (byte)0;
        public ushort CurrentKeyId => call?.KeyId ?? 0;
        public DateTime CallStartedAt => callStarted;
        public bool CallHasMuted => callMuted > 0;
        /// <summary>The current call is encrypted (muted).</summary>
        public bool CallEncrypted => call?.IsEncrypted == true && InCall;
        public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
        public Func<uint, string> DescribeSource { get; set; }

        /// <summary>AMBE codewords decoded since start.</summary>
        public long FramesDecoded { get; private set; }

        private string Src(uint rid) => DescribeSource?.Invoke(rid) ?? rid.ToString();

        /// <param name="talkgroup">The channel's talkgroup.</param>
        /// <param name="slot">The channel's timeslot, 1 or 2.</param>
        public DmrVoiceReceiver(uint talkgroup, byte slot)
        {
            this.talkgroup = talkgroup;
            this.slot = slot is 1 or 2 ? slot : (byte)1;
            decoder = new MBEDecoder(MBE_MODE.DMR_AMBE);
        }

        /// <summary>One DMR packet from the network (its slot as fnecore gives it: 0 for TS1, 1 for TS2).</summary>
        public void Handle(DMRDataReceivedEvent e) => Handle(e.SrcId, e.DstId, e.Slot, e.CallType, e.StreamId, e.Data);

        /// <summary>One DMR packet (self-test entry: the same fields as the network event).</summary>
        public void Handle(uint srcId, uint dstId, byte slotIndex, CallType callType, uint stream, byte[] packet)
        {
            if (callType != CallType.GROUP || dstId != talkgroup || slotIndex + 1 != slot || stream == 0 || packet == null || !DMRFrame.IsVoicePacket(packet))
                return;
            byte type = (byte)(packet[15] & 0x3F);
            bool control = (type & 0x20) != 0;
            lock (sync)
            {
                DMRCallData state = call;
                bool isNew = stream != streamId;
                if (isNew)
                {
                    // a late or repeated terminator must not start a call
                    if (control && (DMRDataType)(type & 0x0F) == DMRDataType.TERMINATOR_WITH_LC)
                        return;
                    state = new DMRCallData { SrcId = srcId, DstId = dstId, TxStreamID = stream, Slot = slot };
                }

                byte[] voice = new byte[DMRFrame.VoiceBytes];
                int got;
                try { got = DMRFrame.Decode(state, packet, voice); }
                catch (InvalidOperationException) { return; }     // a packet after the call ended
                if (!state.LastPacketAccepted)
                    return;

                if (isNew)
                {
                    if (streamId != 0)
                        EndCall();      // another call took the slot without its end arriving
                    StartCall(state, srcId, stream);
                }
                lastFrame = Clock();

                if (state.IsEncrypted && !encryptedAnnounced)
                {
                    encryptedAnnounced = true;
                    Status?.Invoke($"call from {Src(sourceId)} is encrypted DMR, which can't be played yet; muted");
                }

                if (!control)
                {
                    if (got == DMRFrame.VoiceBytes)
                    {
                        for (int n = 0; n < 3; n++)
                        {
                            byte[] cw = new byte[CodewordLength];
                            Buffer.BlockCopy(voice, n * CodewordLength, cw, 0, CodewordLength);
                            Codeword?.Invoke((byte[])cw.Clone());
                            short[] samples = new short[P25VoiceReceiver.FrameSamples];
                            decoder.decode(cw, samples);
                            FramesDecoded++;
                            callFrames++;
                            Audio?.Invoke(samples);
                        }
                    }
                    else if (state.IsEncrypted)
                        callMuted += 3;
                }

                if (state.IsReleased)
                    EndCall();
            }
        }

        public bool EndIfIdle(TimeSpan after)
        {
            lock (sync)
            {
                TimeSpan quiet = Clock() - lastFrame;
                if (streamId == 0 || quiet <= after)
                    return false;
                Status?.Invoke($"call from {Src(sourceId)}: its end didn't arrive (lost on the way), so it was ended after {quiet.TotalSeconds:F0} s without frames");
                EndCall(quiet);
                return true;
            }
        }

        private void StartCall(DMRCallData state, uint srcId, uint stream)
        {
            call = state;
            streamId = stream;
            sourceId = srcId;
            callStarted = DateTime.Now;
            callFrames = 0;
            callMuted = 0;
            encryptedAnnounced = false;
            decoder = new MBEDecoder(MBE_MODE.DMR_AMBE);     // fresh decoder state per call, as a radio would start
            Status?.Invoke($"CALL START  src {Src(srcId)}  tg {talkgroup} TS{slot}  stream {stream}");
            CallStarted?.Invoke(srcId);
        }

        private void EndCall(TimeSpan quiet = default)
        {
            double secs = Math.Max(0, (DateTime.Now - callStarted - quiet).TotalSeconds);
            string crypt = call?.IsEncrypted == true ? "encrypted DMR (muted)" : "clear";
            string muted = callMuted > 0 ? $", {callMuted} muted" : string.Empty;
            Status?.Invoke($"CALL END    src {Src(sourceId)}  tg {talkgroup} TS{slot}  {secs:F1} s, {crypt}, {callFrames} voice frames played{muted}");
            streamId = 0;
            call?.Dispose();
            CallEnded?.Invoke(sourceId, secs, callFrames, callMuted, crypt);
        }

        public void Dispose()
        {
            lock (sync)
            {
                call?.Dispose();
                decoder = null;
            }
        }
    } // public sealed class DmrVoiceReceiver
}
