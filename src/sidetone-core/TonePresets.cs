// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Tone and DTMF presets, as the Windows console has them (SettingsManager.TonePresetConfig, DtmfPresetConfig):
* the operator's own tone sequences and DTMF digit strings, kept in the settings file and sent like an alert
* tone on every listening P25 channel. Tone presets go out as IMBE tone frames (TonePlan.Sequence, as alert
* tones and QCII pages); DTMF as audio through the voice encoder (TonePlan.Dtmf), since a tone frame holds
* one frequency. MIDI files import through the Windows console's own TonePresetFileImporter.
*/

using System.Globalization;
using System.Text.Json.Serialization;
using dvmconsole;

namespace Sidetone.Core
{
    /// <summary>One step of a tone preset: a tone, or silence when <see cref="Hz"/> is 0.</summary>
    public sealed class ToneStep
    {
        public double Hz { get; set; }
        public double Seconds { get; set; }
    }

    /// <summary>A named tone sequence.</summary>
    public sealed class TonePreset
    {
        public string Name { get; set; } = string.Empty;
        public List<ToneStep> Steps { get; set; } = new List<ToneStep>();

        /// <summary>"4 steps, 3.2 s".</summary>
        [JsonIgnore]
        public string Summary => $"{Steps.Count} step{(Steps.Count == 1 ? string.Empty : "s")}, {Steps.Sum(s => s.Seconds).ToString("0.#", CultureInfo.InvariantCulture)} s";
    }

    /// <summary>A named DTMF string: digits 0-9, *, #, A-D, and ',' for a pause.</summary>
    public sealed class DtmfPreset
    {
        public string Name { get; set; } = string.Empty;
        public string Digits { get; set; } = string.Empty;
        /// <summary>How long each digit sounds.</summary>
        public int DigitMs { get; set; } = 100;
        /// <summary>Silence after each digit.</summary>
        public int GapMs { get; set; } = 100;
    }

    public static class TonePresets
    {
        /// <summary>
        /// The tones IMBE can send cleanly: the Windows console's table of pre-encoded tone codewords
        /// (VocoderToneLookupTable) runs from 281 to 2500 Hz in about 31 Hz steps; outside it a tone would snap to the end.
        /// </summary>
        public const double MinHz = 281, MaxHz = 2500;
        /// <summary>A step's length: at least one 20 ms frame, at most a minute.</summary>
        public const double MinSeconds = 0.02, MaxSeconds = 60;
        /// <summary>A ',' in a DTMF preset.</summary>
        public const int PauseMs = 500;
        public const int MinDigitMs = 40, MaxDigitMs = 2000;
        public const string DtmfDigits = "0123456789*#ABCD";

        /// <summary>
        /// Reads the steps editor's text: "1000:0.5, 0:0.2, 1500:1" (Hz:seconds, 0 for silence). Throws
        /// ArgumentException with a plain reason for anything it can't use.
        /// </summary>
        public static List<ToneStep> ParseSteps(string text)
        {
            List<ToneStep> steps = new List<ToneStep>();
            foreach (string raw in (text ?? string.Empty).Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();
                if (part.Length == 0)
                    continue;
                string[] hs = part.Split(':');
                if (hs.Length != 2 || !double.TryParse(hs[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double hz) ||
                    !double.TryParse(hs[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
                    throw new ArgumentException($"\"{part}\" isn't Hz:seconds (like 1000:0.5, or 0:0.2 for silence)");
                if (hz != 0 && (hz < MinHz || hz > MaxHz))
                    throw new ArgumentException($"{hz.ToString("0.#", CultureInfo.InvariantCulture)} Hz is outside {MinHz:0}–{MaxHz:0} Hz (0 is silence)");
                if (secs < MinSeconds || secs > MaxSeconds)
                    throw new ArgumentException($"{secs.ToString("0.###", CultureInfo.InvariantCulture)} s is outside {MinSeconds}–{MaxSeconds:0} s");
                steps.Add(new ToneStep { Hz = hz, Seconds = secs });
            }
            if (steps.Count == 0)
                throw new ArgumentException("no steps (write them as Hz:seconds, separated by commas)");
            return steps;
        }

        /// <summary>The steps as the editor shows them (what <see cref="ParseSteps"/> reads back).</summary>
        public static string FormatSteps(IEnumerable<ToneStep> steps) =>
            string.Join(", ", (steps ?? Enumerable.Empty<ToneStep>()).Select(s =>
                $"{s.Hz.ToString("0.#", CultureInfo.InvariantCulture)}:{s.Seconds.ToString("0.###", CultureInfo.InvariantCulture)}"));

        /// <summary>Checks a DTMF preset's digits and lengths; returns the digits cleaned up (upper case, no spaces).</summary>
        public static string CheckDtmf(string digits, int digitMs, int gapMs)
        {
            string clean = new string((digits ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch)).Select(char.ToUpperInvariant).ToArray());
            if (clean.Trim(',').Length == 0)
                throw new ArgumentException("no digits (0-9, *, #, A-D; a comma pauses)");
            char bad = clean.FirstOrDefault(ch => ch != ',' && !DtmfDigits.Contains(ch));
            if (bad != default)
                throw new ArgumentException($"'{bad}' isn't a DTMF digit (0-9, *, #, A-D; a comma pauses)");
            if (digitMs < MinDigitMs || digitMs > MaxDigitMs || gapMs < MinDigitMs || gapMs > MaxDigitMs)
                throw new ArgumentException($"digit and gap lengths are {MinDigitMs} to {MaxDigitMs} ms");
            return clean;
        }

        /// <summary>
        /// A tone preset from a MIDI file (.mid, .midi), read by the Windows console's importer: one note at a time,
        /// rests as silence. Notes outside the tone range move by octaves into it, so the tune keeps its shape.
        /// Throws with a plain reason when the file can't be used.
        /// </summary>
        public static TonePreset ImportMidi(string path)
        {
            ImportedTonePreset imported;
            try { imported = TonePresetFileImporter.Import(path); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is NotSupportedException || ex is EndOfStreamException)
            {
                throw new ArgumentException(ex.Message);
            }
            TonePreset preset = new TonePreset { Name = imported.DisplayName };
            foreach (ImportedTonePresetStep s in imported.Steps)
            {
                double hz = s.IsHold ? 0 : s.FrequencyHz;
                while (hz > 0 && hz < MinHz)
                    hz *= 2;
                while (hz > MaxHz)
                    hz /= 2;
                preset.Steps.Add(new ToneStep { Hz = Math.Round(hz, 1), Seconds = Math.Clamp(s.DurationSeconds, MinSeconds, MaxSeconds) });
            }
            return preset;
        }
    } // public static class TonePresets
}

namespace dvmconsole
{
    /// <summary>The SettingsManager constants the Windows console's TonePresetFileImporter uses (the rest of that class is WPF).</summary>
    internal static class SettingsManager
    {
        /// <summary>The shortest step the importer makes (its rests shorter than this are dropped, tones stretched).</summary>
        public const double TONE_PRESET_MIN_DURATION_SECONDS = 0.25;
        /// <summary>The longest step the importer makes (longer notes become several).</summary>
        public const double TONE_PRESET_MAX_DURATION_SECONDS = 10.0;
    }
}
