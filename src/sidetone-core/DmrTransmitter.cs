// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* DMR transmit for the console's PTT: clear group voice on a talkgroup and timeslot. The microphone's 20 ms
* blocks are AMBE+2 encoded by libvocoder and handed to the owner's dvmconsole fork's DmrTxCall, which packs
* three codewords a burst, paces the bursts 60 ms apart on its own task, and at the end completes the
* superframe with silence and sends the terminator. Encrypted DMR is refused before this is made.
*/

using dvmconsole;
using dvmconsole.DMR;
using fnecore.DMR;

namespace Sidetone.Core
{
    public sealed class DmrTransmitter : IVoiceTransmitter
    {
        private readonly FneLink link;
        private readonly byte slot;
        private readonly object sync = new object();
        private MBEEncoder encoder;
        private DmrTxCall call;
        private volatile int framesSent;

        public VoiceCodec Codec => VoiceCodec.Ambe;
        public uint SourceId { get; }
        public uint Talkgroup { get; }
        public byte AlgorithmId => 0;
        public ushort KeyId => 0;
        public bool Encrypted => false;
        public bool IsTransmitting { get; private set; }
        public uint StreamId { get; private set; }
        public DateTime StartedAt { get; private set; }
        public int FramesSent => framesSent;
        /// <summary>The timeslot (1 or 2).</summary>
        public byte Slot => slot;
        /// <summary>Why the transmission stopped by itself (the network failed), or null.</summary>
        public string Failure { get; private set; }

        public event Action<byte[]> ClearFrame;

        public DmrTransmitter(FneLink link, uint sourceId, uint talkgroup, byte slot)
        {
            this.link = link;
            SourceId = sourceId;
            Talkgroup = talkgroup;
            this.slot = slot is 1 or 2 ? slot : (byte)1;
        }

        public bool Start(out string error)
        {
            lock (sync)
            {
                error = null;
                if (IsTransmitting)
                    return true;
                if (SourceId == 0 || SourceId > 0xFFFFFF)
                {
                    error = "the system has no valid 'rid' in the codeplug (DMR needs 1 to 16777215)";
                    return false;
                }
                if (Talkgroup == 0 || Talkgroup > 0xFFFFFF)
                {
                    error = "DMR talkgroups are 1 to 16777215";
                    return false;
                }
                if (link.DmrFactory == null)
                {
                    error = $"not connected to {link.Name}";
                    return false;
                }

                encoder = new MBEEncoder(MBE_MODE.DMR_AMBE);
                StreamId = P25Transmitter.NewStreamId();
                DMRCallData data = new DMRCallData { SrcId = SourceId, DstId = Talkgroup, Slot = slot, TxStreamID = StreamId };
                try
                {
                    call = new DmrTxCall(link.DmrFactory, data, Encode, (packet, sequence) => link.SendDmr(data, packet, sequence));
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                {
                    error = ex.Message;
                    return false;
                }
                framesSent = 0;
                Failure = null;
                StartedAt = DateTime.UtcNow;
                IsTransmitting = true;
                return true;
            }
        }

        private byte[] Encode(short[] samples)
        {
            byte[] ambe = new byte[DMRFrame.CodewordBytes];
            encoder.encode((short[])samples.Clone(), ambe);
            framesSent++;
            ClearFrame?.Invoke((byte[])ambe.Clone());
            return ambe;
        }

        public void PushAudio(short[] samples)
        {
            DmrTxCall c;
            lock (sync)
            {
                if (!IsTransmitting)
                    return;
                c = call;
            }
            try { c.Process(samples); }
            catch (InvalidOperationException ex)
            {
                // the sender stopped (the network went away) or fell too far behind: stop rather than send stale audio
                lock (sync)
                    Failure ??= ex.Message;
                Stop();
            }
        }

        public void Stop()
        {
            DmrTxCall c;
            lock (sync)
            {
                if (!IsTransmitting)
                    return;
                IsTransmitting = false;
                c = call;
                call = null;
                lastEnded = c;
            }
            // what's queued still goes out at its pace, then the superframe is completed and the terminator sent
            try { c.End(); } catch (InvalidOperationException) { }
            _ = c.Completion.ContinueWith(_ => c.Dispose(), TaskScheduler.Default);
        }

        /// <summary>Waits until everything queued has gone out and the call has ended (self-test).</summary>
        internal bool WaitSentForTest(TimeSpan timeout)
        {
            DmrTxCall c = call ?? lastEnded;
            try { return c == null || c.Completion.Wait(timeout); }
            catch (AggregateException) { return true; }     // it ended, by failing
        }
        private DmrTxCall lastEnded;

        public void Dispose() => Stop();
    } // public sealed class DmrTransmitter
}
