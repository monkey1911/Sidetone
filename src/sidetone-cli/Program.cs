// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
*   sidetone-cli list     --codeplug FILE
*   sidetone-cli listen   --codeplug FILE (--channel NAME | --tg TGID) [--system NAME]
*                   [--wav FILE] [--no-play] [--player CMD] [--output DEVICE] [--verbose]
*   sidetone-cli tui      --codeplug FILE [--output DEVICE] [--input DEVICE] ...
*   sidetone-cli devices
*   sidetone-cli ptt      down | up | toggle | hold | status [--socket PATH]
*   sidetone-cli selftest
*/

using System.Reflection;
using System.Runtime.InteropServices;
using dvmconsole;

// Linux only (PipeWire, Unix sockets and file modes)
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Sidetone.Cli
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            if (args[0] is "--version" or "version")
            {
                Console.WriteLine($"sidetone-cli {Assembly.GetExecutingAssembly().GetName().Version}");
                return 0;
            }

            if (args[0] == "ptt")
            {
                try { return Ptt(args.Skip(1).ToArray()); }
                catch (ArgumentException ex) { return Usage(ex.Message); }
            }

            try
            {
                Options opt = Options.Parse(args.Skip(1).ToArray());
                return args[0] switch
                {
                    "list" => List(opt),
                    "listen" => Listen(opt),
                    "tui" => RunTui(opt),
                    "devices" => Devices(),
                    "selftest" => SelfTest.Run(),
                    _ => Usage($"unknown command '{args[0]}'")
                };
            }
            catch (ArgumentException ex)
            {
                return Usage(ex.Message);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                Console.Error.WriteLine($"error: could not load libvocoder ({ex.Message}). It must be next to sidetone-cli.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("sidetone-cli - Sidetone, an IP dispatch console for DVMProject FNE networks (terminal)");
            Console.WriteLine();
            Console.WriteLine("usage:");
            Console.WriteLine("  sidetone-cli list     --codeplug FILE");
            Console.WriteLine("  sidetone-cli listen   --codeplug FILE (--channel NAME | --tg TGID) [--system NAME]");
            Console.WriteLine("                  [--wav FILE] [--no-play] [--player CMD] [--output DEVICE] [--verbose]");
            Console.WriteLine("  sidetone-cli tui      --codeplug FILE [--system NAME] [--tx-limit SECONDS] [--log FILE]");
            Console.WriteLine("                  [--marker-interval SECONDS] [--settings FILE | --no-settings]");
            Console.WriteLine("                  [--output DEVICE] [--input DEVICE] [--socket PATH | --no-socket]");
            Console.WriteLine("                  [--ptt-device NAME [--ptt-button BUTTON] [--mute-button BUTTON|none]]");
            Console.WriteLine("  sidetone-cli devices");
            Console.WriteLine("  sidetone-cli ptt      down | up | toggle | hold | status [--socket PATH]");
            Console.WriteLine("  sidetone-cli selftest");
            Console.WriteLine();
            Console.WriteLine("listen follows one P25 talkgroup and plays clear calls through PipeWire (pw-play).");
            Console.WriteLine("devices lists the audio outputs and inputs. --output / --input pick one by any unique");
            Console.WriteLine("part of its name (or its ID); without them the system default is used.");
            Console.WriteLine("ptt controls a running console (sidetone or 'sidetone-cli tui') from a hotkey or script: bind 'ptt down' to a key's");
            Console.WriteLine("press and 'ptt up' to its release, or run 'ptt hold' to transmit until it exits.");
            Console.WriteLine("Press Ctrl+C to stop.");
        }

        private static int Usage(string message)
        {
            Console.Error.WriteLine($"error: {message}");
            Console.Error.WriteLine("run 'sidetone-cli --help' for usage");
            return 2;
        }

        /// <summary>
        /// Loads a codeplug the same way the console does.
        /// </summary>
        public static Codeplug LoadCodeplug(string path) => CodeplugLoader.Load(path);

        private static IEnumerable<Codeplug.Channel> AllChannels(Codeplug cp) =>
            (cp.Zones ?? new List<Codeplug.Zone>()).SelectMany(z => z.Channels ?? new List<Codeplug.Channel>());

        private static int List(Options opt)
        {
            Codeplug cp = LoadCodeplug(opt.Codeplug);

            Console.WriteLine("Systems:");
            foreach (Codeplug.System s in cp.Systems)
                Console.WriteLine($"  {s.Name}: {s.Address}:{s.Port} peer {s.PeerId} ({s.Identity})");

            Console.WriteLine();
            Console.WriteLine($"{"Channel",-24} {"TG",8}  {"Mode",-6} {"Crypto",-8} System");
            foreach (Codeplug.Channel c in AllChannels(cp))
            {
                string crypto = string.IsNullOrWhiteSpace(c.Algo) || c.Algo.Equals("none", StringComparison.OrdinalIgnoreCase) ? "clear" : c.Algo;
                string note = c.GetChannelMode() == Codeplug.ChannelMode.P25 ? string.Empty : "  (not P25; not supported yet)";
                Console.WriteLine($"{c.Name,-24} {c.Tgid,8}  {c.Mode,-6} {crypto,-8} {c.System}{note}");
            }

            return 0;
        }

        private static int Listen(Options opt)
        {
            Codeplug cp = LoadCodeplug(opt.Codeplug);

            Codeplug.Channel channel = null;
            if (!string.IsNullOrWhiteSpace(opt.Channel))
            {
                channel = AllChannels(cp).FirstOrDefault(c => string.Equals(c.Name, opt.Channel, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"no channel named '{opt.Channel}' (run 'sidetone-cli list')");
            }

            uint tg;
            if (opt.Tg != null)
            {
                if (!uint.TryParse(opt.Tg, out tg))
                    throw new ArgumentException($"--tg must be a number, got '{opt.Tg}'");
            }
            else if (channel != null)
            {
                if (channel.GetChannelMode() != Codeplug.ChannelMode.P25)
                    throw new ArgumentException($"channel '{channel.Name}' is {channel.Mode}; this proof of concept only supports P25");
                if (!uint.TryParse(channel.Tgid, out tg))
                    throw new ArgumentException($"channel '{channel.Name}' has an invalid tgid '{channel.Tgid}'");
            }
            else
                throw new ArgumentException("give --channel NAME or --tg TGID");

            string systemName = opt.SystemName ?? channel?.System;
            Codeplug.System system = systemName == null
                ? cp.Systems[0]
                : cp.Systems.FirstOrDefault(s => string.Equals(s.Name, systemName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"no system named '{systemName}'");

            // the channel's configured key, requested from the FNE as soon as we're logged in
            bool channelEncrypted = channel != null && !string.IsNullOrWhiteSpace(channel.Algo) &&
                                    !channel.Algo.Equals("none", StringComparison.OrdinalIgnoreCase);
            byte configuredAlg = channelEncrypted ? channel.GetKeyRequestAlgoId() : (byte)0;
            ushort configuredKeyId = channelEncrypted ? channel.GetKeyId() : (ushort)0;
            if (channelEncrypted)
                Say($"'{channel.Name}' uses {KeyStore.AlgorithmName(configuredAlg)} key 0x{configuredKeyId:X4}; it will be requested from the FNE");

            List<IAudioSink> sinks = new List<IAudioSink>();
            try
            {
                if (!string.IsNullOrWhiteSpace(opt.Input))
                    Say("note: --input is only used by 'tui' (listen doesn't transmit)");
                if (!opt.NoPlay)
                {
                    AudioSelection audio = AudioSelection.Choose(opt.Output, null);
                    PipeWireSink pw = new PipeWireSink(opt.Player, audio.OutputTarget);
                    sinks.Add(pw);
                    Say($"{audio.OutputReport}, through {Path.GetFileName(pw.Command)}");
                }
                else if (!string.IsNullOrWhiteSpace(opt.Output))
                    Say("note: --output is ignored with --no-play");
                if (!string.IsNullOrWhiteSpace(opt.Wav))
                {
                    sinks.Add(new WavSink(opt.Wav));
                    Say($"audio: recording to {Path.GetFullPath(opt.Wav)}");
                }
                if (sinks.Count == 0)
                    Say("audio: none (--no-play without --wav); showing call activity only");

                KeyStore keys = new KeyStore();
                using P25VoiceReceiver receiver = new P25VoiceReceiver(tg, keys);
                receiver.Status += Say;
                receiver.Audio += samples =>
                {
                    ApplyGain(samples, opt.Gain);
                    foreach (IAudioSink sink in sinks)
                        sink.Write(samples);
                };

                RadioAliasBook aliases = new RadioAliasBook(cp, system, Path.GetDirectoryName(AppSettings.DefaultPath));
                aliases.Status += Say;
                aliases.Load();
                receiver.DescribeSource = aliases.Describe;

                CliPeer peer = new CliPeer(system, keys, opt.Verbose) { Aliases = aliases };
                peer.Status += Say;
                peer.P25Frame += e => receiver.Handle(e.DstId, e.SrcId, e.DUID, e.StreamId, e.Data);

                // a call using a key we don't hold yet asks the FNE for it
                receiver.KeyNeeded += (alg, kid) => peer.RequestKey(alg, kid);
                if (channelEncrypted && KeyStore.IsSupported(configuredAlg))
                    peer.RequestKey(configuredAlg, configuredKeyId);   // queued until login completes

                using ManualResetEventSlim stop = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
                AppDomain.CurrentDomain.ProcessExit += (s, e) => stop.Set();

                Say($"connecting to {system.Name} ({system.Address}:{system.Port}) as peer {system.PeerId}, following tg {tg}{(channel != null ? $" ({channel.Name})" : string.Empty)}");
                peer.Start();

                DateTime started = DateTime.Now;
                bool warned = false;
                while (!stop.Wait(1000))
                {
                    if (!peer.Connected && !warned && (DateTime.Now - started).TotalSeconds > 20)
                    {
                        warned = true;
                        Say("still not connected after 20 s: check the address, port, peer ID, password, and that NetBird is up");
                    }
                }

                Say($"stopping ({receiver.CallsSeen} calls, {receiver.FramesDecoded} voice frames decoded)");
                peer.Stop();
                return 0;
            }
            finally
            {
                foreach (IAudioSink sink in sinks)
                    sink.Dispose();
            }
        }

        private static int RunTui(Options opt)
        {
            Codeplug cp = LoadCodeplug(opt.Codeplug);
            Codeplug.System system = opt.SystemName == null
                ? cp.Systems[0]
                : cp.Systems.FirstOrDefault(s => string.Equals(s.Name, opt.SystemName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"no system named '{opt.SystemName}'");

            if (PipeWireSink.FindPlayer() == null)
                Console.Error.WriteLine("warning: pw-play not found; you'll see activity but hear nothing");
            if (PipeWireCapture.FindRecorder() == null)
                Console.Error.WriteLine("warning: pw-record not found; transmit won't work");

            // resolved before the TUI takes over the terminal, so a bad --output / --input is a plain error
            AudioSelection audio = AudioSelection.Choose(opt.Output, opt.Input);

            string logFile = string.IsNullOrWhiteSpace(opt.LogFile) ? null : Path.GetFullPath(opt.LogFile);
            if (opt.PttDevice == null && (opt.PttButton != null || opt.MuteButton != null))
                throw new ArgumentException("--ptt-button and --mute-button need --ptt-device");
            if (opt.PttDevice != null)
                _ = new PttDeviceWatcher(opt.PttDevice, opt.PttButton, opt.MuteButton);    // checks the button names

            string socket = opt.NoSocket ? null : opt.Socket != null ? Path.GetFullPath(opt.Socket) : ControlServer.DefaultPath;
            return new Tui(cp, system, TimeSpan.FromSeconds(opt.TxLimitSeconds), logFile, TimeSpan.FromSeconds(opt.MarkerSeconds),
                           opt.NoSettings ? null : opt.SettingsFile ?? AppSettings.DefaultPath)
            {
                Audio = audio,
                ControlSocketPath = socket,
                PttDevice = opt.PttDevice != null ? (opt.PttDevice, opt.PttButton, opt.MuteButton) : null
            }.Run();
        }

        /// <summary>
        /// sidetone-cli ptt down|up|toggle|hold|status: sends one command to a running console. Exit code 0 when the
        /// console says ok. hold keeps transmitting until this process is stopped (Ctrl+C, SIGTERM, killed)
        /// or the transmission ends some other way.
        /// </summary>
        private static int Ptt(string[] args)
        {
            string action = null, socket = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--socket")
                    socket = i + 1 < args.Length ? args[++i] : throw new ArgumentException("--socket needs a value");
                else if (action == null && !args[i].StartsWith("-"))
                    action = args[i].ToLowerInvariant();
                else
                    throw new ArgumentException($"unexpected '{args[i]}' (sidetone-cli ptt down | up | toggle | hold | status)");
            }
            if (action is not ("down" or "up" or "toggle" or "hold" or "status"))
                throw new ArgumentException("sidetone-cli ptt needs one of: down, up, toggle, hold, status");

            string path = socket != null ? Path.GetFullPath(socket) : ControlServer.DefaultPath;
            string command = action == "status" ? "status" : $"ptt {action}";
            string reply;
            bool printed = false;
            void Print(string r)
            {
                printed = true;
                (r.StartsWith("ok", StringComparison.Ordinal) ? Console.Out : Console.Error).WriteLine(r);
            }
            try
            {
                if (action == "hold")
                {
                    using CancellationTokenSource release = new CancellationTokenSource();
                    Console.CancelKeyPress += (s, e) => { e.Cancel = true; release.Cancel(); };
                    using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; release.Cancel(); });
                    using PosixSignalRegistration hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => { c.Cancel = true; release.Cancel(); });
                    reply = ControlClient.Send(path, command, release.Token, hold: true, onReply: Print);
                }
                else
                    reply = ControlClient.Send(path, command, onReply: Print);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }

            if (!printed)
                Print(reply);
            return reply.StartsWith("ok", StringComparison.Ordinal) ? 0 : 1;
        }

        private static int Devices()
        {
            AudioDeviceList list;
            try { list = AudioDeviceList.Query(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"error: can't list audio devices: {ex.Message}");
                return 1;
            }

            void Print(string title, List<AudioDevice> devs, string defName)
            {
                Console.WriteLine($"{title} (* = system default):");
                if (devs.Count == 0)
                    Console.WriteLine("  (none)");
                foreach (AudioDevice d in devs)
                {
                    Console.WriteLine($"  {(d.Name == defName ? "*" : " ")} {d.Id,4}  {d.Label}");
                    Console.WriteLine($"          {d.Name}");
                }
                Console.WriteLine();
            }

            Print("Outputs", list.Outputs, list.DefaultOutputName);
            Print("Inputs", list.Inputs, list.DefaultInputName);

            List<InputDeviceInfo> buttons = InputDevices.List().Where(d => d.EventPath != null && d.Keys.Count > 0 && d.Keys.Count <= 12).ToList();
            Console.WriteLine("Button devices (for --ptt-device; full keyboards not shown):");
            if (buttons.Count == 0)
                Console.WriteLine("  (none)");
            foreach (InputDeviceInfo d in buttons)
            {
                bool known = InputDevices.Known.ContainsKey((d.Vendor, d.Product));
                Console.WriteLine($"  {d.Name}{(known ? "  [known PTT device]" : string.Empty)}");
                Console.WriteLine($"          {d.EventPath}  {d.Vendor:x4}:{d.Product:x4}  buttons: {InputDevices.DescribeKeys(d)}");
            }
            Console.WriteLine();
            Console.WriteLine("Pick one with --output or --input and any part of its name that matches only that device,");
            Console.WriteLine("for example --output \"Savi\". IDs work too, but they can change after a reboot or replug.");
            return 0;
        }

        /// <summary>
        /// Applies a linear gain with clipping.
        /// </summary>
        public static void ApplyGain(short[] samples, double gain) => AudioMath.ApplyGain(samples, gain);

        private static readonly object sayLock = new object();

        private static void Say(string message)
        {
            lock (sayLock)
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
        }

        /// <summary>
        /// Command-line options.
        /// </summary>
        private sealed class Options
        {
            public string Codeplug, Channel, Tg, SystemName, Wav, Player, LogFile, Output, Input;
            public bool NoPlay, Verbose;
            public double Gain = 1.0;
            public int TxLimitSeconds = 120;
            public int MarkerSeconds = 10;
            public string SettingsFile;
            public bool NoSettings;
            public string Socket, PttDevice, PttButton, MuteButton;
            public bool NoSocket;

            public static Options Parse(string[] args)
            {
                Options o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                    switch (args[i])
                    {
                        case "--codeplug": case "-c": o.Codeplug = Next(); break;
                        case "--channel": o.Channel = Next(); break;
                        case "--tg": o.Tg = Next(); break;
                        case "--system": o.SystemName = Next(); break;
                        case "--wav": o.Wav = Next(); break;
                        case "--player": o.Player = Next(); break;
                        case "--output": o.Output = Next(); break;
                        case "--input": o.Input = Next(); break;
                        case "--gain":
                            if (!double.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out o.Gain) || o.Gain <= 0 || o.Gain > 20)
                                throw new ArgumentException("--gain must be a number between 0 and 20");
                            break;
                        case "--no-play": o.NoPlay = true; break;
                        case "--verbose": case "-v": o.Verbose = true; break;
                        case "--log": o.LogFile = Next(); break;
                        case "--settings":
                            o.SettingsFile = Next();
                            break;
                        case "--no-settings":
                            o.NoSettings = true;
                            break;
                        case "--socket": o.Socket = Next(); break;
                        case "--no-socket": o.NoSocket = true; break;
                        case "--ptt-device": o.PttDevice = Next(); break;
                        case "--ptt-button": o.PttButton = Next(); break;
                        case "--mute-button": o.MuteButton = Next(); break;
                        case "--marker-interval":
                            if (!int.TryParse(Next(), out o.MarkerSeconds) || o.MarkerSeconds < 5 || o.MarkerSeconds > 600)
                                throw new ArgumentException("--marker-interval must be between 5 and 600 seconds");
                            break;
                        case "--tx-limit":
                            if (!int.TryParse(Next(), out o.TxLimitSeconds) || o.TxLimitSeconds < 5 || o.TxLimitSeconds > 900)
                                throw new ArgumentException("--tx-limit must be between 5 and 900 seconds");
                            break;
                        default: throw new ArgumentException($"unknown option '{args[i]}'");
                    }
                }
                return o;
            }
        }
    } // public static class Program
}
