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
