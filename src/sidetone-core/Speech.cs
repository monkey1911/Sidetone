// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Text to speech, through an engine installed separately: Piper (a neural voice, natural sounding)
* or espeak-ng (robotic but clear, in every distribution). Sidetone runs the engine's program with
* the text on its standard input (never through a shell), reads the WAV it writes, and from there
* the words are an announcement like any other. Neither engine is bundled.
*
* Also the wording of time announcements ("The time is two twenty P M, P S T"), spelled out in
* words here so every engine says it the same way.
*/

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Sidetone.Core
{
    public enum SpeechEngineKind { Piper, EspeakNg, Platform }

    /// <summary>
    /// A platform's own text-to-speech engine (Android's), used instead of Piper and espeak-ng when one
    /// is set. Called on a worker thread; it may take a few seconds.
    /// </summary>
    public interface IPlatformSpeech
    {
        /// <summary>The engine's name, as people know it.</summary>
        string Name { get; }

        /// <summary>The voice it will use, as people know it.</summary>
        string VoiceName { get; }

        /// <summary>Speaks the text into a WAV file at the speed (1 is the voice's own pace). Throws ArgumentException with a plain reason when it can't.</summary>
        void SpeakToWav(string text, double rate, string wavPath);
    }

    /// <summary>An engine that was found, and the voice it will use.</summary>
    public sealed class SpeechEngine
    {
        public SpeechEngineKind Kind;
        /// <summary>The engine's program (a full path).</summary>
        public string Program;
        /// <summary>Piper: the voice model (.onnx, full path). espeak-ng: the voice name (like en-us).</summary>
        public string Voice;
        /// <summary>Speaking speed: 1 is the voice's own pace, 0.9 is 10% slower.</summary>
        public double Rate = 1;

        public string Name => Kind switch
        {
            SpeechEngineKind.Piper => "Piper",
            SpeechEngineKind.EspeakNg => "espeak-ng",
            _ => Speech.Platform?.Name ?? "text to speech"
        };

        /// <summary>Like "Piper, voice en_US-lessac-medium".</summary>
        public string Label => Kind == SpeechEngineKind.Platform ? $"{Name}, voice {Voice}" : $"{Name}, voice {Speech.VoiceName(Voice)}";

        /// <summary>Tells cached audio apart: the same text in another voice is different audio.</summary>
        internal string CacheKey => $"{Kind}|{Program}|{Voice}|{Rate.ToString(CultureInfo.InvariantCulture)}";
    }

    public static class Speech
    {
        /// <summary>Settings values for the engine ("automatic" is null).</summary>
        public const string PiperSetting = "piper", EspeakSetting = "espeak-ng";

        public const string DefaultEspeakVoice = "en-us";

        /// <summary>The platform's own engine (Android's), or null to look for Piper and espeak-ng. When set, it's always the one used.</summary>
        public static IPlatformSpeech Platform { get; set; }

        /// <summary>espeak-ng's own speed, in words per minute.</summary>
        public const int EspeakWordsPerMinute = 175;

        /// <summary>The speed choices (1 is the voice's own pace), slowest first, and their names.</summary>
        public static readonly double[] Rates = { 0.8, 0.9, 0.95, 1.0, 1.1 };

        public static string RateName(double rate) => rate switch
        {
            0.8 => "Much slower (80%)",
            0.9 => "Slower (90%)",
            0.95 => "A little slower (95%)",
            1.0 => "Normal",
            1.1 => "Faster (110%)",
            _ => $"{rate * 100:F0}%"
        };

        /// <summary>A Piper voice's own pace (its length_scale, from the .onnx.json beside it), or 1.</summary>
        internal static double VoiceLengthScale(string model)
        {
            try
            {
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(model + ".json"));
                if (doc.RootElement.TryGetProperty("inference", out System.Text.Json.JsonElement inference) &&
                    inference.TryGetProperty("length_scale", out System.Text.Json.JsonElement scale) &&
                    scale.TryGetDouble(out double value) && value > 0)
                    return value;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Text.Json.JsonException) { }
            return 1;
        }

        /// <summary>How long an engine may take before it's stopped.</summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        /// <summary>Where programs are looked for, instead of PATH and the usual folders (self-test).</summary>
        internal static string[] programFoldersForTest;

        /// <summary>Where Piper voices are looked for, instead of the usual folders (self-test).</summary>
        internal static string[] voiceFoldersForTest;

        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private static string DataHome
        {
            get
            {
                string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                return !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(Home, ".local", "share");
            }
        }

        /// <summary>
        /// The folders searched for programs: PATH, then ~/.local/bin and the system folders (an app
        /// launcher's PATH often leaves out ~/.local/bin, where uv and pipx put Piper).
        /// </summary>
        public static IEnumerable<string> ProgramFolders()
        {
            if (programFoldersForTest != null)
                return programFoldersForTest;
            List<string> folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
            folders.AddRange(new[] { Path.Combine(Home, ".local", "bin"), "/usr/local/bin", "/usr/bin" });
            if (OperatingSystem.IsMacOS())
                folders.Add("/opt/homebrew/bin");   // an app opened from Finder gets a short PATH without Homebrew's
            return folders.Distinct();
        }

        /// <summary>A program's full path, or null when it isn't installed.</summary>
        public static string FindProgram(params string[] names)
        {
            foreach (string folder in ProgramFolders())
                foreach (string name in names)
                {
                    string path = Path.Combine(folder, name);
                    if (File.Exists(path))
                        return path;
                    if (OperatingSystem.IsWindows() && File.Exists(path + ".exe"))
                        return path + ".exe";
                }
            return null;
        }

        /// <summary>The folders searched for Piper voices, most likely first.</summary>
        public static IEnumerable<string> VoiceFolders() =>
            voiceFoldersForTest ?? new[] { Path.Combine(DataHome, "piper-voices"), Path.Combine(DataHome, "piper"), "/usr/share/piper-voices" };

        /// <summary>The folder voices should be downloaded into, for messages and the README.</summary>
        public static string VoiceFolder => VoiceFolders().First();

        /// <summary>Piper voices found: .onnx files with their .onnx.json beside them, sorted by name.</summary>
        public static List<string> PiperVoices()
        {
            List<string> found = new List<string>();
            foreach (string folder in VoiceFolders())
            {
                if (!Directory.Exists(folder))
                    continue;
                try
                {
                    // the AUR's voice packages go a few folders deep (lang/name/quality/)
                    foreach (string onnx in Directory.EnumerateFiles(folder, "*.onnx", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }))
                        if (File.Exists(onnx + ".json") && !found.Contains(onnx))
                            found.Add(onnx);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return found.OrderBy(VoiceName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>A voice's short name: a Piper model's file name without .onnx, or an espeak-ng voice as it is.</summary>
        public static string VoiceName(string voice) =>
            voice != null && voice.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(voice) : voice ?? string.Empty;

        /// <summary>
        /// Picks the engine: the one chosen (engine "piper" or "espeak-ng"), or automatically Piper when
        /// it's installed with a voice, else espeak-ng. Null, with why, when there's nothing to use.
        /// </summary>
        public static SpeechEngine Choose(string engine, string voice, out string why)
        {
            why = null;
            // the system's own voice (Android's, macOS's say, Windows's SAPI), unless Piper or espeak-ng was chosen and is installed
            bool chosenThere = engine == PiperSetting ? FindProgram("piper", "piper-tts") != null : engine == EspeakSetting && FindProgram("espeak-ng") != null;
            if (Platform != null && !chosenThere)
                return new SpeechEngine { Kind = SpeechEngineKind.Platform, Voice = Platform.VoiceName };
            bool auto = string.IsNullOrEmpty(engine);
            string piper = auto || engine == PiperSetting ? FindProgram("piper", "piper-tts") : null;
            string espeak = auto || engine == EspeakSetting ? FindProgram("espeak-ng") : null;

            if (piper != null)
            {
                List<string> voices = PiperVoices();
                string model = voice != null && voice.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) && File.Exists(voice) ? voice : voices.FirstOrDefault();
                if (model != null)
                    return new SpeechEngine { Kind = SpeechEngineKind.Piper, Program = piper, Voice = model };
                if (!auto || espeak == null)
                {
                    why = $"Piper is installed but has no voice: download one into {VoiceFolder}";
                    return null;
                }
            }
            if (espeak != null)
            {
                string name = !string.IsNullOrWhiteSpace(voice) && !voice.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ? voice.Trim() : DefaultEspeakVoice;
                return new SpeechEngine { Kind = SpeechEngineKind.EspeakNg, Program = espeak, Voice = name };
            }
            why = engine == PiperSetting ? "Piper isn't installed (no piper or piper-tts program found)"
                : engine == EspeakSetting ? "espeak-ng isn't installed"
                : "no text-to-speech engine found: install Piper (and a voice) or espeak-ng";
            return null;
        }

        /// <summary>
        /// Speaks the text into 8 kHz mono audio, levelled like any announcement. Slow (an engine runs):
        /// not on the UI thread. Throws ArgumentException with a plain reason when it can't.
        /// </summary>
        public static short[] Synthesize(SpeechEngine engine, string text)
        {
            if (engine == null)
                throw new ArgumentException("no text-to-speech engine");
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("there's nothing to say");
            string wav = Path.Combine(Path.GetTempPath(), $"sidetone-speech-{Environment.ProcessId}-{Guid.NewGuid():N}.wav");
            try
            {
                if (engine.Kind == SpeechEngineKind.Platform)
                {
                    IPlatformSpeech platform = Platform ?? throw new ArgumentException("the platform's text to speech is gone");
                    platform.SpeakToWav(text.Replace('\n', ' ').Trim(), engine.Rate, wav);
                    if (!File.Exists(wav) || new FileInfo(wav).Length <= 44)
                        throw new ArgumentException($"{engine.Name} made no audio");
                    return WavFile.ReadAnnouncement(wav);
                }

                ProcessStartInfo psi = new ProcessStartInfo(engine.Program)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                if (engine.Kind == SpeechEngineKind.Piper)
                {
                    psi.ArgumentList.Add("-m");
                    psi.ArgumentList.Add(engine.Voice);
                    if (engine.Rate != 1)
                    {
                        // longer phonemes are slower speech; scale the voice's own pace (old and new Piper both take --length_scale)
                        psi.ArgumentList.Add("--length_scale");
                        psi.ArgumentList.Add((VoiceLengthScale(engine.Voice) / engine.Rate).ToString("0.###", CultureInfo.InvariantCulture));
                    }
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add(wav);
                }
                else
                {
                    psi.ArgumentList.Add("--stdin");
                    psi.ArgumentList.Add("-v");
                    psi.ArgumentList.Add(engine.Voice);
                    if (engine.Rate != 1)
                    {
                        psi.ArgumentList.Add("-s");
                        psi.ArgumentList.Add(((int)Math.Round(EspeakWordsPerMinute * engine.Rate)).ToString(CultureInfo.InvariantCulture));
                    }
                    psi.ArgumentList.Add("-w");
                    psi.ArgumentList.Add(wav);
                }

                using Process p = Process.Start(psi) ?? throw new ArgumentException($"{engine.Name} didn't start");
                Task<string> errors = p.StandardError.ReadToEndAsync();
                Task<string> output = p.StandardOutput.ReadToEndAsync();
                p.StandardInput.Write(text.Replace('\n', ' ').Trim());
                p.StandardInput.Write('\n');
                p.StandardInput.Close();
                if (!p.WaitForExit((int)Timeout.TotalMilliseconds))
                {
                    try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    throw new ArgumentException($"{engine.Name} took longer than {Timeout.TotalSeconds:F0} s and was stopped");
                }
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(wav) || new FileInfo(wav).Length <= 44)
                {
                    string said = LastLine(errors.Result) ?? LastLine(output.Result);
                    throw new ArgumentException($"{engine.Name} failed (exit code {p.ExitCode}{(said != null ? $": {said}" : string.Empty)})");
                }
                return WavFile.ReadAnnouncement(wav);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new ArgumentException($"{engine.Name} couldn't be run ({ex.Message})");
            }
            catch (IOException ex)
            {
                throw new ArgumentException($"{engine.Name}: {ex.Message}");
            }
            finally
            {
                try { File.Delete(wav); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static string LastLine(string text)
        {
            string line = (text ?? string.Empty).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            return line == null ? null : line.Length > 160 ? line.Substring(0, 160) + "…" : line;
        }

        /*
        ** Time announcements
        */

        private static readonly string[] Ones =
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven",
            "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"
        };
        private static readonly string[] Tens = { "", "", "twenty", "thirty", "forty", "fifty" };

        /// <summary>0 to 59 in words.</summary>
        public static string NumberWords(int n)
        {
            if (n < 0 || n > 59)
                throw new ArgumentOutOfRangeException(nameof(n));
            if (n < 20)
                return Ones[n];
            return n % 10 == 0 ? Tens[n / 10] : $"{Tens[n / 10]} {Ones[n % 10]}";
        }

        /// <summary>
        /// The time in words. 12-hour: "two P M", "two oh five P M", "two twenty P M", "twelve A M" (midnight).
        /// 24-hour, the way it's said on the radio: "fourteen hundred", "fourteen oh five", "zero nine thirty", "zero zero fifteen".
        /// </summary>
        public static string TimeWords(int hour, int minute, bool hour24)
        {
            string Minutes() => minute < 10 ? $"oh {Ones[minute]}" : NumberWords(minute);
            if (hour24)
            {
                string h = hour < 10 ? $"zero {Ones[hour]}" : NumberWords(hour);
                return minute == 0 ? (hour == 0 ? "zero zero hundred" : $"{h} hundred") : $"{h} {Minutes()}";
            }
            int h12 = hour % 12 == 0 ? 12 : hour % 12;
            string half = hour < 12 ? "A M" : "P M";
            return minute == 0 ? $"{NumberWords(h12)} {half}" : $"{NumberWords(h12)} {Minutes()} {half}";
        }

        /// <summary>The time zone (an IANA id like America/Chicago), or the computer's own when null or unknown.</summary>
        public static TimeZoneInfo Zone(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return TimeZoneInfo.Local;
            try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
            catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
        }

        /// <summary>True when the id names a time zone this computer knows (empty is the local one, which is fine).</summary>
        public static bool KnownZone(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return true;
            try { TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); return true; }
            catch (TimeZoneNotFoundException) { return false; }
            catch (InvalidTimeZoneException) { return false; }
        }

        /// <summary>
        /// A zone's abbreviation at that moment (PST in winter, PDT in summer), or its offset ("UTC+4")
        /// where tzdata has no letters for it.
        /// </summary>
        public static string ZoneAbbreviation(TimeZoneInfo zone, DateTime utc)
        {
            string name = zone.IsDaylightSavingTime(utc) ? zone.DaylightName : zone.StandardName;
            if (zone.Id == "UTC" || zone.Id == "Etc/UTC" || zone.Id == "Etc/UCT" || name == "Coordinated Universal Time")
                return "UTC";
            if (!string.IsNullOrEmpty(name) && name.Length <= 6 && name.All(char.IsLetter))
                return name.ToUpperInvariant();
            TimeSpan off = zone.GetUtcOffset(utc);
            string sign = off < TimeSpan.Zero ? "-" : "+";
            off = off.Duration();
            return off.Minutes == 0 ? $"UTC{sign}{off.Hours}" : $"UTC{sign}{off.Hours}:{off.Minutes:00}";
        }

        /// <summary>An abbreviation said out loud: letters one by one ("P S T"), an offset in words ("U T C minus five").</summary>
        public static string SayAbbreviation(string abbreviation)
        {
            if (string.IsNullOrEmpty(abbreviation))
                return string.Empty;
            StringBuilder said = new StringBuilder();
            int i = 0;
            while (i < abbreviation.Length && char.IsLetter(abbreviation[i]))
            {
                if (said.Length > 0)
                    said.Append(' ');
                said.Append(char.ToUpperInvariant(abbreviation[i++]));
            }
            if (i < abbreviation.Length)
            {
                string rest = abbreviation.Substring(i);
                said.Append(rest[0] == '-' ? " minus " : " plus ");
                string[] parts = rest.Substring(1).Split(':');
                said.Append(int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) && h <= 59 ? NumberWords(h) : parts[0]);
                if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) && m > 0)
                    said.Append(' ').Append(NumberWords(m));
            }
            return said.ToString();
        }

        /// <summary>
        /// What a time announcement says at that moment: the words before, the time, the zone (its
        /// abbreviation spelled out, or the name given), then the words after.
        /// </summary>
        public static string TimeAnnouncementText(TimeAnnouncementSettings t, DateTime utc)
        {
            t ??= new TimeAnnouncementSettings();
            TimeZoneInfo zone = Zone(t.TimeZone);
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
            StringBuilder s = new StringBuilder();
            void Add(string part)
            {
                part = part?.Trim();
                if (string.IsNullOrEmpty(part))
                    return;
                if (s.Length > 0)
                    s.Append(' ');
                s.Append(part);
            }
            Add(t.Before);
            Add(TimeWords(local.Hour, local.Minute, t.Hour24));
            if (t.SayZone)
                Add(string.IsNullOrWhiteSpace(t.ZoneName) ? SayAbbreviation(ZoneAbbreviation(zone, utc)) : t.ZoneName);
            string text = s.ToString();
            string after = t.After?.Trim();
            if (!string.IsNullOrEmpty(after))
                text = text.Length == 0 ? after : $"{text}. {after}";
            return text;
        }

        /// <summary>The same announcement written the way people read it ("The time is 2:20 PM PST"), for the screen.</summary>
        public static string TimeAnnouncementPreview(TimeAnnouncementSettings t, DateTime utc)
        {
            t ??= new TimeAnnouncementSettings();
            TimeZoneInfo zone = Zone(t.TimeZone);
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
            string time = t.Hour24 ? local.ToString("HHmm", CultureInfo.InvariantCulture) : local.ToString("h:mm tt", CultureInfo.InvariantCulture);
            string zoneText = !t.SayZone ? null : string.IsNullOrWhiteSpace(t.ZoneName) ? ZoneAbbreviation(zone, utc) : t.ZoneName.Trim();
            string main = string.Join(" ", new[] { t.Before?.Trim(), time, zoneText }.Where(x => !string.IsNullOrEmpty(x)));
            string after = t.After?.Trim();
            return string.IsNullOrEmpty(after) ? main : $"{main}. {after}";
        }

        /// <summary>The time zone ids this computer knows, for the lists (UTC first, then by id).</summary>
        public static List<string> ZoneIds() =>
            new[] { "UTC" }.Concat(TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Where(id => id != "UTC" && id.Contains('/') && !id.StartsWith("Etc/", StringComparison.Ordinal))
                                                             .OrderBy(id => id, StringComparer.Ordinal)).Distinct().ToList();

        /// <summary>The schedule choices, in minutes (0 is off).</summary>
        public static readonly int[] Intervals = { 0, 15, 30, 60 };
    } // public static class Speech
}
