// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Shared core of both front ends.
*
* Small helpers both front ends need: loading a codeplug and applying receive gain.
*/

using dvmconsole;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// Linux (PipeWire, Unix sockets and file modes, evdev) and Android (which brings its own audio, speech and keystore)
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("android")]

namespace Sidetone.Core
{
    /// <summary>
    /// Loads dvmconsole codeplug files.
    /// </summary>
    public static class CodeplugLoader
    {
        /// <summary>
        /// Loads a codeplug. Throws ArgumentException with a plain message when it can't be used.
        /// </summary>
        public static Codeplug Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("--codeplug is required");

            path = Path.GetFullPath(path);
            if (!File.Exists(path))
                throw new ArgumentException($"codeplug not found: {path}");

            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            Codeplug codeplug = deserializer.Deserialize<Codeplug>(File.ReadAllText(path))
                ?? throw new ArgumentException($"codeplug is empty: {path}");
            codeplug.SourcePath = path;

            if (codeplug.Systems == null || codeplug.Systems.Count == 0)
                throw new ArgumentException("codeplug has no systems");

            return codeplug;
        }

        /// <summary>
        /// The system with this name (case ignored), or the first one when no name is given.
        /// </summary>
        public static Codeplug.System FindSystem(Codeplug codeplug, string name) =>
            name == null
                ? codeplug.Systems[0]
                : codeplug.Systems.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"no system named '{name}'");

        /// <summary>The systems that have channels in the codeplug (a channel with no system is on the first one).</summary>
        public static List<Codeplug.System> SystemsWithChannels(Codeplug codeplug)
        {
            List<Codeplug.Channel> all = (codeplug.Zones ?? new List<Codeplug.Zone>()).Where(z => z?.Channels != null).SelectMany(z => z.Channels).Where(c => c != null).ToList();
            return codeplug.Systems.Where((s, i) => all.Any(c => string.IsNullOrWhiteSpace(c.System) ? i == 0 : string.Equals(c.System.Trim(), s.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        /// <summary>
        /// The systems a console connects to: only <paramref name="only"/> when given (--system), otherwise every
        /// system with channels that isn't turned off in Settings. When all of them are off, the first still
        /// connects (a console needs one), with a note saying so.
        /// </summary>
        public static List<Codeplug.System> SystemsToConnect(Codeplug codeplug, AppSettings settings, string only, List<string> notes = null)
        {
            if (only != null)
                return new List<Codeplug.System> { FindSystem(codeplug, only) };
            List<Codeplug.System> with = SystemsWithChannels(codeplug);
            if (with.Count == 0)
                return new List<Codeplug.System> { codeplug.Systems[0] };    // the console says there are no channels
            List<Codeplug.System> on = with.Where(s => !(settings != null && settings.Systems.TryGetValue(s.Name, out SystemSettings ss) && ss.Off == true)).ToList();
            if (on.Count == 0)
            {
                notes?.Add($"every system is turned off in Settings, so {with[0].Name} connects anyway");
                on.Add(with[0]);
            }
            else if (on.Count < with.Count)
                notes?.Add($"turned off in Settings: {string.Join(", ", with.Except(on).Select(s => s.Name))}");
            return on;
        }
    } // public static class CodeplugLoader

    /// <summary>
    /// PCM helpers.
    /// </summary>
    public static class AudioMath
    {
        /// <summary>Scales 16-bit samples in place, clamping at full scale.</summary>
        public static void ApplyGain(short[] samples, double gain)
        {
            if (gain == 1.0)
                return;
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);
        }
    } // public static class AudioMath
}
