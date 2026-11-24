// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
*   sidetone-cli list     --codeplug FILE
*   sidetone-cli listen   --codeplug FILE (--channel NAME | --tg TGID) [--system NAME]
*                   [--wav FILE] [--no-play] [--player CMD] [--output DEVICE] [--key-file FILE.ekc] [--verbose]
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
            DesktopPlatform.Setup();    // macOS and Windows: their own folders, speech, password store and notifications
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

            if (args[0] == "process")
            {
                try { return ProcessFile(args.Skip(1).ToArray()); }
                catch (ArgumentException ex) { return Usage(ex.Message); }
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
            Console.WriteLine("                  [--wav FILE] [--no-play] [--player CMD] [--output DEVICE] [--key-file FILE.ekc] [--verbose]");
            Console.WriteLine("  sidetone-cli tui      --codeplug FILE [--system NAME] [--tx-limit SECONDS] [--log FILE | --no-log]");
            Console.WriteLine("                  [--marker-interval SECONDS] [--settings FILE | --no-settings]");
            Console.WriteLine("                  [--output DEVICE] [--input DEVICE] [--socket PATH | --no-socket]");
            Console.WriteLine("                  [--ptt-device NAME [--ptt-button BUTTON] [--mute-button BUTTON|none]]");
            Console.WriteLine("                  [--key-file FILE.ekc] [--record-dir DIR] [--no-notify] [--no-processing]");
            Console.WriteLine("  sidetone-cli devices");
            Console.WriteLine("  sidetone-cli process IN.wav OUT.wav [--gate] [--no-low-cut] [--no-auto-level] [--no-limiter] [--vocoder]");
            Console.WriteLine("  sidetone-cli ptt      down | up | toggle | hold | status [--socket PATH]");
            Console.WriteLine("  sidetone-cli selftest");
            Console.WriteLine();
            Console.WriteLine("listen follows one P25 talkgroup and plays them through the sound system (PipeWire).");
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

        private static LocalKeyFile listenKeys;

        /// <summary>
        /// listen's local key file (--key-file, the one chosen in Settings, or the codeplug's): unlocked with
        /// the keyring's password, or by asking (three tries; empty skips). Its keys go into
        /// <paramref name="keys"/>, ahead of the FNE's. Returns it (to dispose), or null.
        /// </summary>
        internal static LocalKeyFile OpenListenKeyFile(string path, KeyStore keys, Func<string, string> ask)
        {
            if (path == null)
                return null;
            string file = Path.GetFileName(path);
            if (!File.Exists(path))
            {
                Say($"warning: key file not found: {path}");
                return null;
            }
            List<string> tries = new List<string>();
            string remembered = SecretStore.Lookup(path);
            if (remembered != null)
                tries.Add(remembered);
            for (int attempt = 0; attempt < 3 + tries.Count; attempt++)
            {
                bool fromKeyring = attempt < tries.Count;
                string password = fromKeyring ? tries[attempt] : ask($"Password for {file} (Enter to skip): ");
                if (string.IsNullOrEmpty(password))
                {
                    Say("keys: key file skipped; using the FNE's keys only");
                    return null;
                }
                LocalKeyFile opened = LocalKeyFile.Open(path, password, out string error, out bool wrong);
                if (opened != null)
                {
                    keys.SetLocal(opened.Lookup, opened.Count);
                    Say($"keys: {opened.Count} from {file}{(fromKeyring ? " (password from the keyring)" : string.Empty)}");
                    return opened;
                }
                if (!fromKeyring)
                    Say($"keys: {error}");
                if (!wrong)
                    return null;
            }
            Say("keys: key file not unlocked; using the FNE's keys only");
            return null;
        }

        /// <summary>Asks for the key file's password without showing it (a line from stdin when it isn't a terminal).</summary>
        private static string AskKeyFilePassword(string prompt)
        {
            if (Console.IsInputRedirected)
                return Console.ReadLine() ?? string.Empty;
            Console.Write(prompt);
            System.Text.StringBuilder typed = new System.Text.StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                    break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (typed.Length > 0)
                        typed.Length--;
                }
                else if (!char.IsControl(key.KeyChar))
                    typed.Append(key.KeyChar);
            }
            Console.WriteLine();
            return typed.ToString();
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
                    throw new ArgumentException($"channel '{channel.Name}' is {channel.Mode}; Sidetone only supports P25 so far");
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
                    if (opt.Player != null)
                    {
                        PipeWireSink pw = new PipeWireSink(opt.Player, audio.OutputTarget);    // --player: a PipeWire player of your choosing
                        sinks.Add(pw);
                        Say($"{audio.OutputReport}, through {Path.GetFileName(pw.Command)}");
                    }
                    else
                    {
                        sinks.Add(AudioBackend.Current.OpenOutput(audio.OutputTarget));
                        Say($"{audio.OutputReport}, through {AudioBackend.Current.Name}");
                    }
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
                AppSettings savedForKeys = opt.NoSettings ? null : AppSettings.Load(opt.SettingsFile ?? AppSettings.DefaultPath, out _);
                listenKeys = OpenListenKeyFile(StartupChoices.KeyFile(cp, opt.KeyFile, savedForKeys), keys, AskKeyFilePassword);
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
                listenKeys?.Dispose();      // wipes the key file's keys from memory
                listenKeys = null;
            }
        }

        private static int RunTui(Options opt)
        {
            Codeplug cp = LoadCodeplug(opt.Codeplug);

            foreach (string problem in AudioBackend.Current.Problems())
                Console.Error.WriteLine($"warning: {problem}");

            // the command line wins; otherwise what the graphical console's Settings saved; otherwise the defaults.
            // Resolved before the TUI takes over the terminal, so a bad --output / --input is a plain error.
            string settingsPath = opt.NoSettings ? null : opt.SettingsFile ?? AppSettings.DefaultPath;
            string settingsError = null;
            AppSettings saved = settingsPath == null ? new AppSettings() : AppSettings.Load(settingsPath, out settingsError);
            List<string> notes = new List<string>();
            if (settingsError != null)
                notes.Add($"warning: {settingsError}");
            AudioSelection audio = StartupChoices.Audio(opt.Output, opt.Input, saved, notes);
            (string Device, string Button, string Mute)? ptt = StartupChoices.PttDevice(opt.PttDevice, opt.PttButton, opt.MuteButton, saved, notes);
            // every system with channels, unless --system names one or Settings turned some off
            List<Codeplug.System> systems = CodeplugLoader.SystemsToConnect(cp, saved, opt.SystemName, notes);

            string logFile = string.IsNullOrWhiteSpace(opt.LogFile) ? null : Path.GetFullPath(opt.LogFile);
            string socket = opt.NoSocket ? null : opt.Socket != null ? Path.GetFullPath(opt.Socket) : ControlServer.DefaultPath;
            Tui tui = new Tui(cp, systems, TimeSpan.FromSeconds(StartupChoices.TxLimit(opt.TxLimitSeconds, saved, cp)), logFile,
                              TimeSpan.FromSeconds(opt.MarkerSeconds), settingsPath)
            {
                Audio = audio,
                ControlSocketPath = socket,
                PttDevice = ptt,
                KeyFilePath = StartupChoices.KeyFile(cp, opt.KeyFile, saved),
                LogFolder = opt.NoLog || logFile != null ? null : LogFiles.DefaultFolder,
                HistoryFolder = settingsPath == null ? null : DispatchConsole.DefaultHistoryFolder
            };
            if (opt.RecordDir != null)
                tui.RecordingsFolder = Path.GetFullPath(opt.RecordDir);
            if (opt.NoNotify)
                tui.Notifications = false;
            tui.ProcessingOff = opt.NoProcessing;
            foreach (string note in notes)
                tui.AddLog(note);
            if (tui.KeyFilePath != null)
                UnlockKeyFile(tui);     // from the keyring, before the TUI starts; otherwise the TUI asks
            return tui.Run();
        }

        /// <summary>
        /// Unlocks the local key file with the password remembered in the keyring, if there is one. If
        /// not, the TUI opens its key file box when it starts (K opens it again later).
        /// </summary>
        private static void UnlockKeyFile(Tui tui)
        {
            if (!File.Exists(tui.KeyFilePath))
            {
                tui.AddLog($"warning: key file not found: {tui.KeyFilePath}");
                return;
            }
            string remembered = SecretStore.Lookup(tui.KeyFilePath);
            if (remembered != null && tui.LoadKeyFile(remembered, out _) == null)
                return;
            tui.AskForKeyFilePassword = true;
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

        /// <summary>
        /// sidetone-cli process: runs a recording through the microphone processing (and, with --vocoder,
        /// through IMBE and back, as it would sound on the air), to hear what the settings do.
        /// </summary>
        private static int ProcessFile(string[] args)
        {
            List<string> files = new List<string>();
            bool lowCut = true, autoLevel = true, gate = false, limiter = true, vocoderPass = false;
            foreach (string a in args)
            {
                switch (a)
                {
                    case "--gate": gate = true; break;
                    case "--no-low-cut": lowCut = false; break;
                    case "--no-auto-level": autoLevel = false; break;
                    case "--no-limiter": limiter = false; break;
                    case "--vocoder": vocoderPass = true; break;
                    default:
                        if (a.StartsWith("--"))
                            throw new ArgumentException($"unknown option '{a}'");
                        files.Add(a);
                        break;
                }
            }
            if (files.Count != 2)
                throw new ArgumentException("process needs an input and an output WAV file");

            short[] input = WavFile.ReadVoice(files[0]);
            VoiceProcessing stages = new VoiceProcessing(lowCut, autoLevel, gate, limiter);
            VoiceProcessor proc = new VoiceProcessor(stages);
            MBEEncoder encoder = vocoderPass ? new MBEEncoder(MBE_MODE.IMBE_88BIT) : null;
            MBEDecoder decoder = vocoderPass ? new MBEDecoder(MBE_MODE.IMBE_88BIT) : null;
            List<short[]> output = new List<short[]>();
            const int block = P25VoiceReceiver.FrameSamples;
            for (int i = 0; i + block <= input.Length; i += block)
            {
                short[] samples = input.AsSpan(i, block).ToArray();
                proc.Process(samples);
                if (vocoderPass)
                {
                    byte[] imbe = new byte[P25VoiceReceiver.ImbeLength];
                    encoder.encode(samples, imbe);
                    samples = new short[block];
                    decoder.decode(imbe, samples);
                }
                output.Add(samples);
            }

            using (WavSink wav = new WavSink(files[1]))
            {
                foreach (short[] s in output)
                    wav.Write(s);
            }
            short[] all = output.SelectMany(s => s).ToArray();
            Console.WriteLine($"processing: {stages}{(vocoderPass ? ", then through IMBE and back" : string.Empty)}");
            Console.WriteLine($"in:  {WavFile.Describe(input)}");
            Console.WriteLine($"out: {WavFile.Describe(all)}");
            Console.WriteLine($"wrote {files[1]} ({all.Length / (double)VoiceProcessor.SampleRate:F1} s)");
            return 0;
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
                Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {message}");
        }

        /// <summary>
        /// Command-line options.
        /// </summary>
        private sealed class Options
        {
            public string Codeplug, Channel, Tg, SystemName, Wav, Player, LogFile, Output, Input;
            public bool NoPlay, Verbose;
            public double Gain = 1.0;
            public int? TxLimitSeconds;
            public int MarkerSeconds = 10;
            public string SettingsFile;
            public bool NoSettings;
            public string Socket, PttDevice, PttButton, MuteButton, KeyFile, RecordDir;
            public bool NoNotify, NoProcessing, NoLog;
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
                        case "--no-log": o.NoLog = true; break;
                        case "--settings":
                            o.SettingsFile = Next();
                            break;
                        case "--no-settings":
                            o.NoSettings = true;
                            break;
                        case "--socket": o.Socket = Next(); break;
                        case "--no-socket": o.NoSocket = true; break;
                        case "--ptt-device": o.PttDevice = Next(); break;
                        case "--key-file": o.KeyFile = Next(); break;
                        case "--record-dir": o.RecordDir = Next(); break;
                        case "--no-notify": o.NoNotify = true; break;
                        case "--no-processing": o.NoProcessing = true; break;
                        case "--ptt-button": o.PttButton = Next(); break;
                        case "--mute-button": o.MuteButton = Next(); break;
                        case "--marker-interval":
                            if (!int.TryParse(Next(), out o.MarkerSeconds) || o.MarkerSeconds < 5 || o.MarkerSeconds > 600)
                                throw new ArgumentException("--marker-interval must be between 5 and 600 seconds");
                            break;
                        case "--tx-limit":
                            if (!int.TryParse(Next(), out int limit) || limit < StartupChoices.MinTxLimit || limit > StartupChoices.MaxTxLimit)
                                throw new ArgumentException($"--tx-limit must be between {StartupChoices.MinTxLimit} and {StartupChoices.MaxTxLimit} seconds");
                            o.TxLimitSeconds = limit;
                            break;
                        default: throw new ArgumentException($"unknown option '{args[i]}'");
                    }
                }
                return o;
            }
        }
    } // public static class Program
}
