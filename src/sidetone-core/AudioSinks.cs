// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Audio outputs for decoded 8 kHz mono 16-bit PCM:
*  - PipeWireSink streams to a PipeWire output (the default, or --output) through pw-play (or pw-cat).
*  - WavSink records to a .wav file.
*  - OpusFileSink records to an Ogg Opus (.opus) file, with Concentus (a C# port of libopus).
*/

using System.Diagnostics;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace Sidetone.Core
{
    /// <summary>
    /// Destination for decoded 8 kHz mono 16-bit PCM.
    /// </summary>
    public interface IAudioSink : IDisposable
    {
        /// <summary>Writes one block of samples.</summary>
        void Write(short[] samples);
    }

    /// <summary>
    /// Plays PCM on a PipeWire output by piping it into pw-play.
    /// </summary>
    public sealed class PipeWireSink : IAudioOutput
    {
        public const int SampleRate = 8000;

        private readonly Process player;
        private readonly Stream stdin;
        private bool failed;

        /// <summary>Command used for playback (pw-play or pw-cat).</summary>
        public string Command { get; }

        /// <summary>
        /// Starts the player process.
        /// </summary>
        /// <param name="command">Explicit player command, or null to find pw-play / pw-cat on PATH.</param>
        /// <param name="target">PipeWire node name to play to, or null for the default output.</param>
        public PipeWireSink(string command = null, string target = null)
        {
            Command = command ?? FindPlayer() ??
                throw new InvalidOperationException("neither pw-play nor pw-cat was found; install pipewire (pacman -S pipewire) or use --no-play");

            ProcessStartInfo psi = new ProcessStartInfo(Command)
            {
                UseShellExecute = false,
                // all three redirected: the child never touches the terminal (the TUI owns it in raw mode)
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // pw-cat needs --playback; pw-play is pw-cat in playback mode already
            if (Path.GetFileName(Command) == "pw-cat")
                psi.ArgumentList.Add("--playback");
            // --raw: stdin carries bare PCM samples (without it pw-cat tries to open "-" as a sound file)
            foreach (string arg in new[] { "--raw", "--rate", SampleRate.ToString(), "--channels", "1", "--format", "s16",
                                           "--media-role", "Communication" })
                psi.ArgumentList.Add(arg);
            if (!string.IsNullOrEmpty(target))
            {
                psi.ArgumentList.Add("--target");
                psi.ArgumentList.Add(target);
            }
            psi.ArgumentList.Add("-");

            player = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {Command}");
            stdin = player.StandardInput.BaseStream;
            player.OutputDataReceived += (_, _) => { };
            player.ErrorDataReceived += (_, _) => { };
            player.BeginOutputReadLine();
            player.BeginErrorReadLine();
        }

        /// <summary>Raised once if the player stops unexpectedly.</summary>
        public event Action<string> Failed;

        /// <summary>
        /// Finds pw-play or pw-cat on PATH.
        /// </summary>
        public static string FindPlayer()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string name in new[] { "pw-play", "pw-cat" })
            {
                foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            return null;
        }

        /// <inheritdoc/>
        public void Write(short[] samples)
        {
            if (failed)
                return;

            byte[] pcm = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
            try
            {
                stdin.Write(pcm, 0, pcm.Length);
                stdin.Flush();
            }
            catch (IOException)
            {
                failed = true;
                string msg = $"audio: {Command} stopped (exit code {(player.HasExited ? player.ExitCode : -1)}); playback disabled";
                if (Failed != null) Failed(msg); else Console.Error.WriteLine(msg);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            try { stdin.Dispose(); } catch { /* ignore */ }
            try
            {
                if (!player.WaitForExit(2000))
                    player.Kill();
            }
            catch { /* ignore */ }
            player.Dispose();
        }
    } // public sealed class PipeWireSink

    /// <summary>
    /// Records PCM to an 8 kHz mono 16-bit WAV file.
    /// </summary>
    public sealed class WavSink : IAudioSink
    {
        private readonly FileStream file;
        private long dataBytes;

        /// <summary>
        /// Creates the WAV file (overwriting any existing file).
        /// </summary>
        public WavSink(string path)
        {
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteHeader(0);
        }

        /// <inheritdoc/>
        public void Write(short[] samples)
        {
            byte[] pcm = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
            file.Write(pcm, 0, pcm.Length);
            dataBytes += pcm.Length;
        }

        private void WriteHeader(long dataLength)
        {
            const int rate = PipeWireSink.SampleRate, channels = 1, bits = 16;
            using BinaryWriter w = new BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true);
            file.Seek(0, SeekOrigin.Begin);
            w.Write("RIFF"u8.ToArray());
            w.Write((uint)(36 + dataLength));
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16u);
            w.Write((ushort)1);                          // PCM
            w.Write((ushort)channels);
            w.Write((uint)rate);
            w.Write((uint)(rate * channels * bits / 8)); // byte rate
            w.Write((ushort)(channels * bits / 8));      // block align
            w.Write((ushort)bits);
            w.Write("data"u8.ToArray());
            w.Write((uint)dataLength);
            file.Seek(0, SeekOrigin.End);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            WriteHeader(dataBytes);
            file.Dispose();
        }
    } // public sealed class WavSink

    /// <summary>
    /// Records 8 kHz mono voice to an Ogg Opus (.opus) file: about 16 kbit/s, an eighth of the
    /// size of the same call as WAV, and played by any media player. Always the C# encoder (never
    /// a system libopus), so a recording comes out the same on every machine.
    ///
    /// Concentus.OggFile's own ending isn't quite right for strict players (ffmpeg calls it invalid
    /// data): it ends with an extra page holding an empty packet, and leaves the encoder's start-up
    /// delay (pre-skip) at 0. So when the file is finished, those pages are mended: pre-skip is set
    /// from the encoder, the empty last page is dropped, the last page with audio is marked as the
    /// end of the stream with the exact end position, and their checksums are recomputed.
    /// </summary>
    public sealed class OpusFileSink : IAudioSink
    {
        /// <summary>Bits per second: plenty for 8 kHz voice that has been through IMBE already.</summary>
        public const int Bitrate = 16000;

        private const int To48k = 48000 / PipeWireSink.SampleRate;

        private readonly string path;
        private readonly IOpusEncoder encoder;
        private readonly OpusOggWriteStream ogg;
        private long samplesIn;
        private bool finished;

        /// <summary>Creates the file (overwriting any existing file). Tags go into the file's metadata.</summary>
        public OpusFileSink(string path, string title = null, string artist = null, DateTime? when = null)
        {
            this.path = path;
            OpusCodecFactory.AttemptToUseNativeLibrary = false;
            encoder = OpusCodecFactory.CreateEncoder(PipeWireSink.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = Bitrate;
            encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;

            OpusTags tags = new OpusTags();
            if (!string.IsNullOrWhiteSpace(title))
                tags.Fields[OpusTagName.Title] = title;
            if (!string.IsNullOrWhiteSpace(artist))
                tags.Fields[OpusTagName.Artist] = artist;
            if (when.HasValue)
                tags.Fields["date"] = when.Value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

            FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            ogg = new OpusOggWriteStream(encoder, file, tags, PipeWireSink.SampleRate);
        }

        /// <inheritdoc/>
        public void Write(short[] samples)
        {
            ogg.WriteSamples(samples, 0, samples.Length);
            samplesIn += samples.Length;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (finished)
                return;
            finished = true;
            ogg.Finish();       // writes the last pages and closes the file
            int preSkip = encoder.Lookahead * To48k;
            File.WriteAllBytes(path, Mend(File.ReadAllBytes(path), preSkip, preSkip + samplesIn * To48k));
        }

        /// <summary>One Ogg page: where it starts, its header length and its body length.</summary>
        internal readonly record struct OggPage(int Offset, int HeaderLength, int BodyLength)
        {
            public int Length => HeaderLength + BodyLength;
        }

        /// <summary>The pages of an Ogg file; stops at anything that isn't one.</summary>
        internal static List<OggPage> Pages(byte[] b)
        {
            List<OggPage> pages = new List<OggPage>();
            int pos = 0;
            while (pos + 27 <= b.Length && b[pos] == 'O' && b[pos + 1] == 'g' && b[pos + 2] == 'g' && b[pos + 3] == 'S')
            {
                int segments = b[pos + 26], header = 27 + segments;
                if (pos + header > b.Length)
                    break;
                int body = 0;
                for (int i = 0; i < segments; i++)
                    body += b[pos + 27 + i];
                if (pos + header + body > b.Length)
                    break;
                pages.Add(new OggPage(pos, header, body));
                pos += header + body;
            }
            return pages;
        }

        /// <summary>
        /// Mends a finished Ogg Opus file (see the class notes): pre-skip, the end of the stream and its
        /// exact end position (in 48 kHz samples, pre-skip included). Returns the new bytes.
        /// </summary>
        internal static byte[] Mend(byte[] b, int preSkip, long endGranule)
        {
            List<OggPage> pages = Pages(b);
            if (pages.Count < 3)
                return b;
            HashSet<int> changed = new HashSet<int>();

            // pre-skip: in OpusHead, 10 bytes into the first page's body
            OggPage head = pages[0];
            int preSkipAt = head.Offset + head.HeaderLength + 10;
            b[preSkipAt] = (byte)preSkip;
            b[preSkipAt + 1] = (byte)(preSkip >> 8);
            changed.Add(0);

            // an empty last page (one zero-length packet): drop it, the page before ends the stream
            OggPage last = pages[^1];
            if (pages.Count > 3 && last.BodyLength == 0)
            {
                pages.RemoveAt(pages.Count - 1);
                last = pages[^1];
            }
            int lastIndex = pages.Count - 1;
            b[last.Offset + 5] |= 0x04;     // end of stream
            long granule = BitConverter.ToInt64(b, last.Offset + 6);
            long before = lastIndex >= 3 ? BitConverter.ToInt64(b, pages[lastIndex - 1].Offset + 6) : 0;
            if (endGranule > before && endGranule <= granule)
                BitConverter.TryWriteBytes(new Span<byte>(b, last.Offset + 6, 8), endGranule);   // players trim the padding after it
            changed.Add(lastIndex);

            foreach (int i in changed)
            {
                OggPage page = pages[i];
                Array.Clear(b, page.Offset + 22, 4);
                uint crc = OggCrc(b, page.Offset, page.Length);
                BitConverter.TryWriteBytes(new Span<byte>(b, page.Offset + 22, 4), crc);
            }
            return b.AsSpan(0, last.Offset + last.Length).ToArray();
        }

        private static readonly uint[] CrcTable = MakeCrcTable();

        private static uint[] MakeCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint r = i << 24;
                for (int k = 0; k < 8; k++)
                    r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
                table[i] = r;
            }
            return table;
        }

        /// <summary>Ogg's page checksum (CRC-32, polynomial 0x04C11DB7, not reflected), over a page whose checksum field is zero.</summary>
        internal static uint OggCrc(byte[] b, int offset, int length)
        {
            uint crc = 0;
            for (int i = offset; i < offset + length; i++)
                crc = (crc << 8) ^ CrcTable[((crc >> 24) & 0xFF) ^ b[i]];
            return crc;
        }
    } // public sealed class OpusFileSink
}
