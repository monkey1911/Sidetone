// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Alert tones 1-6, QuickCall II pages and the channel marker, matching the Windows console's built-in
* tones (MainWindow.BuildLegacyAlertTonePayload / BuildChannelHoldTonePayload), and a
* job that transmits one of them on several talkgroups at once in real time.
*/

using System.Diagnostics;

namespace Sidetone.Core
{
    /// <summary>
    /// A tone as a sequence of 20 ms frames: a frequency, or null for silence.
    /// </summary>
    public sealed class TonePlan
    {
        // same values as dvmconsole's MainWindow
        public const int LeadInMs = 750;
        public const int TailMs = 750;
        public const double AlertDurationMs = 5000;
        public const double StepMs = 250;
        public const double FastStepMs = 125;
        public const double MarkerHz = 650;
        public const double MarkerMs = 500;
        public const double MonitorAmplitude = 1845.0 / short.MaxValue;

        private const int FrameMs = 20;

        /// <summary>Name for logs and cards ("alert 3", "marker").</summary>
        public string Name { get; }

        /// <summary>Tone per 20 ms frame; null = silence. Length is a whole number of LDUs (multiple of 9).</summary>
        public ushort?[] Frames { get; }

        /// <summary>For a recording (an announcement): its audio, one 160-sample frame per entry in Frames; null for tones.</summary>
        private readonly List<short[]> audio;

        /// <summary>
        /// An announcement: recorded 8 kHz audio, sent through the voice encoder after the same lead-in
        /// silence as the tones (time for the system to grant the talkgroup), with a short tail, in whole LDUs.
        /// </summary>
        public static TonePlan FromAudio(string name, short[] samples) => new TonePlan(name, samples);

        private TonePlan(string name, short[] samples)
        {
            Name = name;
            const int samplesPerFrame = 160;
            int lead = LeadInMs / FrameMs, tail = 250 / FrameMs;
            int body = (samples.Length + samplesPerFrame - 1) / samplesPerFrame;
            int total = (lead + body + tail + 8) / 9 * 9;
            audio = new List<short[]>(total);
            for (int f = 0; f < total; f++)
            {
                short[] frame = new short[samplesPerFrame];
                int from = (f - lead) * samplesPerFrame;
                if (f >= lead && from < samples.Length)
                    Array.Copy(samples, from, frame, 0, Math.Min(samplesPerFrame, samples.Length - from));
                audio.Add(frame);
            }
            Frames = new ushort?[total];       // all voice frames: the encoder takes each one's audio
        }

        private TonePlan(string name, List<(double Hz, double Ms)> content)
        {
            Name = name;
            List<(double Hz, double Ms)> steps = new List<(double, double)> { (0, LeadInMs) };
            steps.AddRange(content);
            steps.Add((0, TailMs));

            double total = steps.Sum(s => s.Ms);
            int frames = (int)Math.Ceiling(total / FrameMs);
            frames = (frames + 8) / 9 * 9;     // whole LDUs, so the transmitter never drops the end

            // like the console: each frame takes the step at its centre
            Frames = new ushort?[frames];
            for (int f = 0; f < frames; f++)
            {
                double centre = f * FrameMs + FrameMs / 2.0, elapsed = 0;
                double hz = 0;
                foreach ((double stepHz, double ms) in steps)
                {
                    elapsed += ms;
                    if (centre < elapsed)
                    {
                        hz = stepHz;
                        break;
                    }
                }
                Frames[f] = hz > 0 ? (ushort)Math.Round(hz) : null;
            }
        }

        /// <summary>Alert tone 1-6.</summary>
        public static TonePlan Alert(int number)
        {
            List<(double, double)> c = new List<(double, double)>();
            switch (number)
            {
                case 1: c.Add((1000, AlertDurationMs)); break;
                case 2: Alternate(c, 1500, 800, StepMs); break;
                case 3: Pulse(c, 1000, StepMs); break;
                case 4: Alternate(c, 1500, 1000, FastStepMs); break;
                case 5: Alternate(c, 940, 1640, StepMs); break;
                case 6: Alternate(c, 460, 860, FastStepMs); break;     // warble
                default: throw new ArgumentOutOfRangeException(nameof(number), "alert tones are 1 to 6");
            }
            return new TonePlan($"alert {number}", c);
        }

        /// <summary>QuickCall II tone A length (ms).</summary>
        public const double PageAMs = 1000;

        /// <summary>QuickCall II tone B length (ms).</summary>
        public const double PageBMs = 3000;

        /// <summary>Lowest and highest page tone accepted (the console's tone detector range).</summary>
        public const double MinPageHz = 250, MaxPageHz = 3000;

        /// <summary>
        /// A QuickCall II two-tone page: tone A for 1 s, then tone B for 3 s (as the Windows console's manual page).
        /// </summary>
        public static TonePlan Page(double aHz, double bHz)
        {
            if (aHz < MinPageHz || aHz > MaxPageHz || bHz < MinPageHz || bHz > MaxPageHz)
                throw new ArgumentOutOfRangeException(nameof(aHz), $"page tones must be {MinPageHz:F0}-{MaxPageHz:F0} Hz");
            return new TonePlan($"page {aHz:0.#}/{bHz:0.#}", new List<(double, double)> { (aHz, PageAMs), (bHz, PageBMs) });
        }

        /// <summary>The channel marker (the console's "channel hold" beep).</summary>
        public static TonePlan Marker() => new TonePlan("marker", new List<(double, double)> { (MarkerHz, MarkerMs) });

        /// <summary>A tone preset: its steps as IMBE tone frames (0 Hz is silence), with the alert tones' lead-in and tail.</summary>
        public static TonePlan Sequence(string name, IEnumerable<ToneStep> steps) =>
            new TonePlan(name, steps.Select(st => (st.Hz, st.Seconds * 1000)).ToList());

        /// <summary>The DTMF pairs (low, high Hz), as the Windows console's table.</summary>
        public static (double Low, double High) DtmfPair(char digit) => char.ToUpperInvariant(digit) switch
        {
            '1' => (697, 1209), '2' => (697, 1336), '3' => (697, 1477), 'A' => (697, 1633),
            '4' => (770, 1209), '5' => (770, 1336), '6' => (770, 1477), 'B' => (770, 1633),
            '7' => (852, 1209), '8' => (852, 1336), '9' => (852, 1477), 'C' => (852, 1633),
            '*' => (941, 1209), '0' => (941, 1336), '#' => (941, 1477), 'D' => (941, 1633),
            _ => throw new ArgumentException($"'{digit}' isn't a DTMF digit")
        };

        /// <summary>
        /// A DTMF preset: each digit as its two tones (each at -20 dBFS, 5 ms fades), then the gap; ',' is a pause.
        /// Sent as audio through the voice encoder (a tone frame carries one frequency), as the Windows console does.
        /// </summary>
        public static TonePlan Dtmf(string name, string digits, int digitMs, int gapMs)
        {
            const int rate = 8000, fade = 40;
            const double level = 0.1 * short.MaxValue;
            List<short> pcm = new List<short>();
            foreach (char ch in digits)
            {
                if (ch == ',')
                {
                    pcm.AddRange(new short[TonePresets.PauseMs * rate / 1000]);
                    continue;
                }
                (double low, double high) = DtmfPair(ch);
                int n = digitMs * rate / 1000;
                for (int i = 0; i < n; i++)
                {
                    double env = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)fade);
                    double v = (Math.Sin(2 * Math.PI * low * i / rate) + Math.Sin(2 * Math.PI * high * i / rate)) * level * env;
                    pcm.Add((short)Math.Round(v));
                }
                pcm.AddRange(new short[gapMs * rate / 1000]);
            }
            return FromAudio(name, pcm.ToArray());
        }

        /// <summary>How long the plan takes to send, lead-in and tail included.</summary>
        public TimeSpan Length => TimeSpan.FromMilliseconds(Frames.Length * FrameMs);

        private static void Alternate(List<(double, double)> c, double a, double b, double step)
        {
            bool first = true;
            for (double left = AlertDurationMs; left > 0; left -= step, first = !first)
                c.Add((first ? a : b, Math.Min(step, left)));
        }

        private static void Pulse(List<(double, double)> c, double hz, double step)
        {
            bool on = true;
            for (double left = AlertDurationMs; left > 0; left -= step, on = !on)
                c.Add((on ? hz : 0, Math.Min(step, left)));
        }

        /// <summary>
        /// Local monitor audio for the plan (phase-continuous sine at the console's alert level).
        /// </summary>
        public IEnumerable<short[]> MonitorPcm()
        {
            if (audio != null)
            {
                foreach (short[] frame in audio)
                    yield return frame;
                yield break;
            }
            double phase = 0;
            foreach (ushort? hz in Frames)
            {
                short[] frame = new short[P25VoiceReceiver.FrameSamples];
                if (hz.HasValue)
                {
                    double step = 2 * Math.PI * hz.Value / 8000.0;   // frame value is rounded to 1 Hz; fine for monitoring
                    for (int i = 0; i < frame.Length; i++, phase += step)
                        frame[i] = (short)(Math.Sin(phase) * MonitorAmplitude * short.MaxValue);
                    phase %= 2 * Math.PI;
                }
                yield return frame;
            }
        }
    } // public sealed class TonePlan

    /// <summary>
    /// Transmits a tone plan on one or more talkgroups at once, one call per talkgroup, paced at 20 ms per frame.
    /// </summary>
    public sealed class ToneJob
    {
        private readonly TonePlan plan;
        private readonly List<(string Name, P25Transmitter Tx)> targets;
        private readonly Action<short[]> monitor;
        private readonly CancellationTokenSource cancel = new CancellationTokenSource();
        private Thread thread;

        /// <summary>The plan being sent.</summary>
        public TonePlan Plan => plan;

        /// <summary>Names of the channels it is sent on (started successfully).</summary>
        public IReadOnlyList<string> Channels => targets.Select(t => t.Name).ToList();

        /// <summary>True until the tone has finished or been cancelled.</summary>
        public bool Running => thread != null && thread.IsAlive;

        /// <summary>True if <see cref="Cancel"/> was called.</summary>
        public bool Cancelled => cancel.IsCancellationRequested;

        /// <summary>Raised on the job thread when it ends.</summary>
        public event Action<ToneJob> Finished;

        /// <summary>
        /// Creates a job. Transmitters must already be started (Start returned true).
        /// </summary>
        public ToneJob(TonePlan plan, List<(string Name, P25Transmitter Tx)> started, Action<short[]> monitor)
        {
            this.plan = plan;
            targets = started;
            this.monitor = monitor;
        }

        /// <summary>
        /// Starts sending. With <paramref name="realTime"/> false (self-test) the frames go out as fast as possible
        /// and the call returns when done.
        /// </summary>
        public void Start(bool realTime = true)
        {
            if (!realTime)
            {
                Run(false);
                return;
            }
            thread = new Thread(() => Run(true)) { IsBackground = true, Name = $"tone {plan.Name}" };
            thread.Start();
        }

        /// <summary>Stops the tone early (the calls end cleanly with TDUs).</summary>
        public void Cancel() => cancel.Cancel();

        /// <summary>True if this job is sending on the named channel.</summary>
        public bool Covers(string name) => targets.Any(t => t.Name == name);

        private void Run(bool realTime)
        {
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                int i = 0;
                using IEnumerator<short[]> pcm = plan.MonitorPcm().GetEnumerator();
                foreach (ushort? hz in plan.Frames)
                {
                    if (cancel.IsCancellationRequested)
                        break;
                    pcm.MoveNext();

                    // silence frames go through the voice encoder (as the console does); tone frames use the table
                    foreach ((_, P25Transmitter tx) in targets)
                        tx.PushFrame(hz.HasValue ? null : pcm.Current, hz);
                    monitor?.Invoke((short[])pcm.Current.Clone());

                    i++;
                    if (realTime)
                    {
                        long wait = i * 20 - clock.ElapsedMilliseconds;
                        if (wait > 0)
                            cancel.Token.WaitHandle.WaitOne((int)wait);
                    }
                }
            }
            finally
            {
                foreach ((_, P25Transmitter tx) in targets)
                    tx.Stop();
                Finished?.Invoke(this);
            }
        }
    } // public sealed class ToneJob
}
