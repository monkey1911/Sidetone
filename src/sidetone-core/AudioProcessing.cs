// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Shared core of both front ends.
*
* Voice processing for the microphone (before the vocoder) and, optionally, for received audio.
* Plain C# with no platform audio APIs, so it behaves the same whatever plays and records the
* sound: a low-cut filter, a noise gate, automatic level and a peak limiter, in that order, on
* 8 kHz 16-bit mono blocks.
*
* The settings are deliberately gentle. IMBE codes speech best when it arrives at a steady,
* moderate level without rumble or clipping; it does not need the audio made loud.
*/

namespace Sidetone.Core
{
    /// <summary>Which stages of <see cref="VoiceProcessor"/> are on.</summary>
    public sealed record VoiceProcessing(bool LowCut, bool AutoLevel, bool NoiseGate, bool Limiter)
    {
        /// <summary>The microphone's defaults: everything but the noise gate.</summary>
        public static readonly VoiceProcessing MicDefault = new VoiceProcessing(true, true, false, true);
        /// <summary>For received audio: level and limiter only.</summary>
        public static readonly VoiceProcessing Receive = new VoiceProcessing(false, true, false, true);
        public static readonly VoiceProcessing Off = new VoiceProcessing(false, false, false, false);

        public bool Any => LowCut || AutoLevel || NoiseGate || Limiter;

        public override string ToString()
        {
            if (!Any)
                return "off";
            List<string> on = new List<string>();
            if (LowCut) on.Add("low cut");
            if (NoiseGate) on.Add("noise gate");
            if (AutoLevel) on.Add("auto level");
            if (Limiter) on.Add("limiter");
            return string.Join(", ", on);
        }
    } // public sealed record VoiceProcessing

    /// <summary>
    /// A second-order (biquad) filter, as in the Audio EQ Cookbook.
    /// </summary>
    public sealed class Biquad
    {
        private readonly double b0, b1, b2, a1, a2;
        private double x1, x2, y1, y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            this.b0 = b0 / a0; this.b1 = b1 / a0; this.b2 = b2 / a0;
            this.a1 = a1 / a0; this.a2 = a2 / a0;
        }

        /// <summary>A Butterworth high-pass filter.</summary>
        public static Biquad HighPass(double cutoffHz, double sampleRate, double q = 0.7071)
        {
            double w = 2 * Math.PI * cutoffHz / sampleRate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
            return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public double Process(double x)
        {
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x;
            y2 = y1; y1 = y;
            return y;
        }

        public void Reset() => x1 = x2 = y1 = y2 = 0;
    } // public sealed class Biquad

    /// <summary>
    /// Processes voice in 20 ms blocks. Not thread-safe: one instance per audio stream.
    /// </summary>
    public sealed class VoiceProcessor
    {
        public const int SampleRate = 8000;

        /// <summary>Low-cut corner: removes rumble, hum and handling noise, keeps the voice.</summary>
        public const double LowCutHz = 200;
        /// <summary>Automatic level aims speech at this RMS level.</summary>
        public const double TargetDb = -20;
        /// <summary>Automatic level never adds more than this, so quiet rooms don't become hiss.</summary>
        public const double MaxBoostDb = 18;
        /// <summary>…nor takes away more than this.</summary>
        public const double MaxCutDb = -10;
        /// <summary>Below this, a block isn't speech: the level is left alone (and the gate may close).</summary>
        public const double SpeechDb = -45;
        /// <summary>The gate closes below this.</summary>
        public const double GateDb = -52;
        /// <summary>How far a closed gate turns the sound down (not to silence, which sounds unnatural).</summary>
        public const double GateFloorDb = -24;
        /// <summary>The limiter keeps peaks below this.</summary>
        public const double CeilingDb = -1;

        private readonly VoiceProcessing on;
        private readonly Biquad lowCut;

        private double levelDb = double.NaN;       // smoothed speech level
        private double agcDb;                      // current automatic level gain
        private double gateGain = 1, gateTarget = 1;
        private int gateHoldBlocks;
        private double limitEnv;
        private double lastGain = 1;               // gain at the end of the previous block, for smooth changes

        public VoiceProcessor(VoiceProcessing stages)
        {
            on = stages ?? VoiceProcessing.Off;
            lowCut = Biquad.HighPass(LowCutHz, SampleRate);
        }

        public VoiceProcessing Stages => on;
        /// <summary>The automatic level's gain right now, in dB.</summary>
        public double AutoLevelDb => agcDb;
        /// <summary>The noise gate is closed.</summary>
        public bool GateClosed => gateTarget < 1;

        public void Reset()
        {
            lowCut.Reset();
            levelDb = double.NaN;
            agcDb = 0;
            gateGain = gateTarget = 1;
            gateHoldBlocks = 0;
            limitEnv = 0;
            lastGain = 1;
        }

        /// <summary>Processes one block of samples in place.</summary>
        public void Process(short[] block)
        {
            if (block == null || block.Length == 0 || !on.Any)
                return;

            int n = block.Length;
            double[] x = new double[n];
            for (int i = 0; i < n; i++)
                x[i] = block[i] / 32768.0;

            if (on.LowCut)
            {
                for (int i = 0; i < n; i++)
                    x[i] = lowCut.Process(x[i]);
            }

            double rmsDb = RmsDb(x);
            bool speech = rmsDb > SpeechDb;

            // noise gate: closes after 200 ms below the threshold, opens at once
            if (on.NoiseGate)
            {
                if (rmsDb > GateDb)
                {
                    gateTarget = 1;
                    gateHoldBlocks = 10;
                }
                else if (gateHoldBlocks > 0)
                    gateHoldBlocks--;
                else
                    gateTarget = Math.Pow(10, GateFloorDb / 20);
            }

            // automatic level: follows speech only, falls quickly when too loud and rises slowly
            if (on.AutoLevel && speech)
            {
                levelDb = double.IsNaN(levelDb) ? rmsDb : levelDb + 0.3 * (rmsDb - levelDb);
                double want = Math.Clamp(TargetDb - levelDb, MaxCutDb, MaxBoostDb);
                double rate = want < agcDb ? 0.35 : 0.05;
                agcDb += rate * (want - agcDb);
            }

            // the gate opens fast and closes over about 100 ms
            gateGain += (gateTarget > gateGain ? 0.8 : 0.2) * (gateTarget - gateGain);
            double gain = (on.AutoLevel ? Math.Pow(10, agcDb / 20) : 1) * (on.NoiseGate ? gateGain : 1);

            double ceiling = Math.Pow(10, CeilingDb / 20);
            double release = Math.Exp(-1.0 / (0.05 * SampleRate));
            for (int i = 0; i < n; i++)
            {
                double g = lastGain + (gain - lastGain) * (i + 1) / n;     // no steps between blocks
                double y = x[i] * g;
                if (on.Limiter)
                {
                    double a = Math.Abs(y);
                    limitEnv = a > limitEnv ? a : limitEnv * release + a * (1 - release);
                    if (limitEnv > ceiling)
                        y *= ceiling / limitEnv;
                }
                block[i] = (short)Math.Clamp(Math.Round(y * 32768.0), short.MinValue, short.MaxValue);
            }
            lastGain = gain;
        }

        private static double RmsDb(double[] x)
        {
            double sum = 0;
            foreach (double v in x)
                sum += v * v;
            double rms = Math.Sqrt(sum / x.Length);
            return rms <= 1e-9 ? -120 : 20 * Math.Log10(rms);
        }
    } // public sealed class VoiceProcessor
}

namespace Sidetone.Core
{
    /// <summary>Reading WAV files of voice (8 kHz, 16-bit, mono) and describing their levels.</summary>
    public static class WavFile
    {
        /// <summary>The samples of an 8 kHz 16-bit mono PCM WAV file. Anything else is refused with a plain message.</summary>
        public static short[] ReadVoice(string path)
        {
            if (!File.Exists(path))
                throw new ArgumentException($"not found: {path}");
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 12 || System.Text.Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(d, 8, 4) != "WAVE")
                throw new ArgumentException($"{Path.GetFileName(path)} isn't a WAV file");

            int pos = 12, rate = 0, channels = 0, bits = 0, format = 0;
            while (pos + 8 <= d.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(d, pos, 4);
                int size = BitConverter.ToInt32(d, pos + 4);
                int body = pos + 8;
                if (id == "fmt " && size >= 16 && body + 16 <= d.Length)
                {
                    format = BitConverter.ToUInt16(d, body);
                    channels = BitConverter.ToUInt16(d, body + 2);
                    rate = BitConverter.ToInt32(d, body + 4);
                    bits = BitConverter.ToUInt16(d, body + 14);
                }
                else if (id == "data")
                {
                    if (format != 1 || channels != 1 || rate != VoiceProcessor.SampleRate || bits != 16)
                        throw new ArgumentException($"{Path.GetFileName(path)} is {rate} Hz, {channels} channel(s), {bits}-bit; it needs to be 8000 Hz mono 16-bit PCM " +
                                                    "(for example: ffmpeg -i IN -ar 8000 -ac 1 -sample_fmt s16 OUT.wav)");
                    int length = Math.Min(size < 0 ? int.MaxValue : size, d.Length - body) / 2;
                    short[] samples = new short[length];
                    Buffer.BlockCopy(d, body, samples, 0, length * 2);
                    return samples;
                }
                pos = body + size + (size & 1);
            }
            throw new ArgumentException($"{Path.GetFileName(path)} has no audio data");
        }

        /// <summary>Peak, and the RMS level of the louder 20 ms blocks (roughly, the speech), in dBFS.</summary>
        public static string Describe(short[] samples)
        {
            int peak = 0;
            List<double> blocks = new List<double>();
            for (int i = 0; i + 160 <= samples.Length; i += 160)
            {
                double sum = 0;
                for (int j = i; j < i + 160; j++)
                {
                    peak = Math.Max(peak, Math.Abs((int)samples[j]));
                    sum += (double)samples[j] * samples[j];
                }
                blocks.Add(Math.Sqrt(sum / 160) / 32768.0);
            }
            double Db(double v) => v <= 1e-9 ? -120 : 20 * Math.Log10(v);
            List<double> loud = blocks.Where(b => Db(b) > VoiceProcessor.SpeechDb).ToList();
            string speech = loud.Count == 0 ? "no speech found" : $"speech {Db(Math.Sqrt(loud.Average(b => b * b))):F1} dBFS";
            int clipped = samples.Count(s => s >= 32700 || s <= -32700);
            return $"{speech}, peak {Db(peak / 32768.0):F1} dBFS{(clipped > 0 ? $", {clipped} samples at full scale" : string.Empty)}";
        }
    } // public static class WavFile
}
