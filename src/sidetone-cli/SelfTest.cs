// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Offline self-test, run by CI on Linux ARM64 and x86-64. It checks that libvocoder loads, that
* IMBE encode/decode round-trips, that the receiver pulls the nine codewords out of an
* LDU frame and handles calls, that encrypted calls decrypt to exactly the clear audio,
* and that codeplugs and WAV files are read/written correctly. It does not touch the network.
*/

using System.Runtime.InteropServices;
using dvmconsole;
using fnecore.P25;

namespace Sidetone.Cli
{
    public static class SelfTest
    {
        private static int failures;

        private const ushort TestKeyId = 0x0561;
        private static readonly byte[] TestKey = Enumerable.Range(0, 32).Select(i => (byte)(0x10 + i * 7)).ToArray();
        private static readonly byte[] TestMi = { 0x3A, 0x91, 0x0C, 0x5E, 0xB2, 0x47, 0xD8, 0x13, 0x00 };

        private static void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (!ok)
            {
                failures++;
                // surfaces as an annotation on the GitHub Actions run
                if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                    Console.WriteLine($"::error title=selftest::FAIL: {what}");
            }
        }

        public static int Run()
        {
            failures = 0;
            Console.WriteLine($"sidetone-cli self-test on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");

            // 1. libvocoder loads and round-trips voice-like audio
            MBEEncoder encoder = new MBEEncoder(MBE_MODE.IMBE_88BIT);
            MBEDecoder decoder = new MBEDecoder(MBE_MODE.IMBE_88BIT);
            Check(true, "libvocoder loaded (encoder and decoder created)");

            const int frames = 90;                       // five superframes (LDU1 + LDU2 = 18 frames each)
            List<byte[]> codewords = new List<byte[]>();
            double outEnergy = 0;
            for (int f = 0; f < frames; f++)
            {
                short[] pcm = VoicedFrame(f);
                byte[] cw = new byte[P25VoiceReceiver.ImbeLength];
                encoder.encode(pcm, cw);
                codewords.Add(cw);

                short[] back = new short[P25VoiceReceiver.FrameSamples];
                decoder.decode(cw, back);
                if (f >= 10)
                    foreach (short s in back)
                        outEnergy += (double)s * s;
            }
            double rms = Math.Sqrt(outEnergy / ((frames - 10) * P25VoiceReceiver.FrameSamples));
            Check(rms > 100, $"IMBE round trip produces audio (output RMS {rms:F0})");
            Check(codewords.Select(c => Convert.ToHexString(c)).Distinct().Count() > 1, "encoder output varies frame to frame");

            // 2. codeword extraction from a network LDU frame
            byte[] ldu1 = BuildLduFrame(P25VoiceReceiver.Ldu1FirstRecord, codewords.Take(9).ToList());
            byte[] ldu2 = BuildLduFrame(P25VoiceReceiver.Ldu2FirstRecord, codewords.Skip(9).Take(9).ToList());
            bool ok1 = P25VoiceReceiver.TryExtractCodewords(ldu1, P25VoiceReceiver.Ldu1FirstRecord, out byte[][] got1);
            bool ok2 = P25VoiceReceiver.TryExtractCodewords(ldu2, P25VoiceReceiver.Ldu2FirstRecord, out byte[][] got2);
            Check(ok1 && Enumerable.Range(0, 9).All(i => got1[i].SequenceEqual(codewords[i])), "LDU1: all nine codewords extracted in order");
            Check(ok2 && Enumerable.Range(0, 9).All(i => got2[i].SequenceEqual(codewords[9 + i])), "LDU2: all nine codewords extracted in order");
            Check(!P25VoiceReceiver.TryExtractCodewords(ldu1, P25VoiceReceiver.Ldu2FirstRecord, out _), "LDU1 frame is rejected when LDU2 records are expected");
            byte[] corrupt = (byte[])ldu1.Clone();
            corrupt[P25VoiceReceiver.PayloadOffset + 53] = 0x00;
            Check(!P25VoiceReceiver.TryExtractCodewords(corrupt, P25VoiceReceiver.Ldu1FirstRecord, out _), "frame with a damaged record header is rejected");
            Check(!P25VoiceReceiver.TryExtractCodewords(new byte[100], P25VoiceReceiver.Ldu1FirstRecord, out _), "short frame is rejected");

            byte[] syncFrame = BuildLduFrame(P25VoiceReceiver.Ldu2FirstRecord, codewords.Skip(9).Take(9).ToList(), 0, P25Defines.P25_ALGO_AES, TestKeyId, TestMi);
            P25VoiceReceiver.ReadEncryptionSync(syncFrame, out byte sAlg, out ushort sKid, out byte[] sMi);
            Check(sAlg == P25Defines.P25_ALGO_AES && sKid == TestKeyId && sMi.SequenceEqual(TestMi) &&
                  P25VoiceReceiver.TryExtractCodewords(syncFrame, P25VoiceReceiver.Ldu2FirstRecord, out byte[][] syncCw) &&
                  Enumerable.Range(0, 9).All(i => syncCw[i].SequenceEqual(codewords[9 + i])),
                  "LDU2 encryption sync (algorithm, key ID, MI) read back without disturbing the voice");

            // 3. clear call on the followed talkgroup, traffic on others ignored
            using (P25VoiceReceiver rx = new P25VoiceReceiver(504))
            {
                List<short> audio = new List<short>();
                List<byte[]> fed = new List<byte[]>();
                List<string> status = new List<string>();
                rx.Audio += s => audio.AddRange(s);
                rx.Codeword += fed.Add;
                rx.Status += status.Add;

                rx.Handle(999, 1234, P25DUID.LDU1, 77, ldu1);           // other talkgroup
                Check(rx.CallsSeen == 0 && audio.Count == 0, "traffic on another talkgroup is ignored");

                foreach ((P25DUID duid, byte[] frame) in BuildCall(codewords, P25Defines.P25_ALGO_UNENCRYPT, 0, null, withHdu: true))
                    rx.Handle(504, 1234, duid, 42, frame);
                rx.Handle(504, 1234, P25DUID.TDU, 42, new byte[24]);

                Check(rx.CallsSeen == 1, "one call start detected");
                Check(rx.FramesDecoded == frames && rx.FramesMuted == 0, $"clear call: all {frames} voice frames played");
                Check(SameCodewords(fed, codewords), "clear call: decoder fed exactly the transmitted codewords, in order");
                Check(Rms(audio) > 100, $"clear call audio is not silent (RMS {Rms(audio):F0})");
                Check(status.Any(s => s.StartsWith("CALL START")) && status.Any(s => s.StartsWith("CALL END") && s.Contains("clear")), "call start and end reported");
            }

            // 4. late entry (no HDU): muted until the first LDU2 gives sync, then plays
            using (P25VoiceReceiver rx = new P25VoiceReceiver(504))
            {
                foreach ((P25DUID duid, byte[] frame) in BuildCall(codewords, P25Defines.P25_ALGO_UNENCRYPT, 0, null, withHdu: false))
                    rx.Handle(504, 1234, duid, 43, frame);
                Check(rx.FramesMuted == 18 && rx.FramesDecoded == frames - 18, "late-entry clear call plays from the second superframe");
            }

            // 5. encrypted call (AES-256) with the key: decrypts to exactly the clear audio
            KeyStore keys = new KeyStore();
            Check(keys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey), "AES key stored");
            Check(!keys.Store(0x99, 1, TestKey), "unsupported algorithm is refused");
            using (P25VoiceReceiver rx = new P25VoiceReceiver(500, keys))
            {
                List<short> audio = new List<short>();
                List<byte[]> fed = new List<byte[]>();
                List<string> status = new List<string>();
                int keyRequests = 0;
                rx.Audio += s => audio.AddRange(s);
                rx.Codeword += fed.Add;
                rx.Status += status.Add;
                rx.KeyNeeded += (_, _) => keyRequests++;

                List<(P25DUID, byte[])> call = BuildCall(codewords, P25Defines.P25_ALGO_AES, TestKeyId, TestKey, withHdu: true);
                Check(!call[0].Item2.Skip(P25VoiceReceiver.PayloadOffset + 10).Take(11).SequenceEqual(codewords[0]), "test call is really encrypted on the wire");

                foreach ((P25DUID duid, byte[] frame) in call)
                    rx.Handle(500, 1234, duid, 50, frame);
                rx.Handle(500, 1234, P25DUID.TDU, 50, new byte[24]);

                Check(rx.FramesDecoded == frames && rx.FramesMuted == 0 && keyRequests == 0, $"AES call: all {frames} frames decrypted, no key request needed");
                Check(SameCodewords(fed, codewords), "AES call: every codeword decrypts back to the clear original (MI cycled across 5 superframes)");
                Check(Rms(audio) > 100, $"decrypted call audio is not silent (RMS {Rms(audio):F0})");
                Check(status.Any(s => s.Contains("decrypting AES-256 key 0x0561")), "decryption reported");
            }

            // 6. encrypted call without the key: muted and the key is requested; key arriving mid-call unmutes
            KeyStore late = new KeyStore();
            using (P25VoiceReceiver rx = new P25VoiceReceiver(500, late))
            {
                List<(byte, ushort)> requested = new List<(byte, ushort)>();
                rx.KeyNeeded += (a, k) => requested.Add((a, k));

                List<(P25DUID, byte[])> call = BuildCall(codewords, P25Defines.P25_ALGO_AES, TestKeyId, TestKey, withHdu: true);
                for (int i = 0; i < 4; i++)                          // first two superframes, no key
                    rx.Handle(500, 1234, call[i].Item1, 51, call[i].Item2);
                Check(rx.FramesDecoded == 0 && rx.FramesMuted == 36, "AES call without a key is muted");
                Check(requested.Count == 1 && requested[0] == (P25Defines.P25_ALGO_AES, TestKeyId), "missing key requested once, with the right algorithm and key ID");

                late.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey); // FNE answers
                for (int i = 4; i < call.Count; i++)
                    rx.Handle(500, 1234, call[i].Item1, 51, call[i].Item2);
                Check(rx.FramesDecoded > 0 && rx.FramesDecoded == frames - 36 - 18,
                      "key arriving mid-call unmutes from the next superframe after an LDU2");
            }

            // 7. transmit: frames built for the FNE round-trip through the receiver
            TransmitTests(codewords.Count);
            ToneTests();

            // 8. codeplug parsing (same settings as the console)
            string tmp = Path.Combine(Path.GetTempPath(), $"sidetone-selftest-{Environment.ProcessId}");
            Directory.CreateDirectory(tmp);
            try
            {
                string cpPath = Path.Combine(tmp, "codeplug.yml");
                File.WriteAllText(cpPath, SampleCodeplug);
                Codeplug cp = Program.LoadCodeplug(cpPath);
                Codeplug.System sys = cp.Systems.FirstOrDefault();
                List<Codeplug.Channel> chans = cp.Zones?.FirstOrDefault()?.Channels;
                Codeplug.Channel ch = chans?.FirstOrDefault();
                Codeplug.Channel sec = chans?.Skip(1).FirstOrDefault();
                Check(sys != null && sys.Name == "Test FNE" && sys.PeerId == 9000001 && sys.Port == 62031, "codeplug system parsed");
                Check(ch != null && ch.Tgid == "504" && ch.GetChannelMode() == Codeplug.ChannelMode.P25 && ch.SelectableEncryption, "codeplug channel parsed (including dev-only keys)");
                Check(sec != null && sec.GetKeyRequestAlgoId() == P25Defines.P25_ALGO_AES && sec.GetKeyId() == TestKeyId, "encrypted channel's algorithm and key ID read from the codeplug");

                // 9. TUI: card grid, buttons and clicks (no terminal or network needed)
                TuiTests(cp, sys);

                // 10. audio device lookup (pw-dump parsing, --output / --input matching, startup report)
                AudioDeviceTests();

                // 11. microphone level meter
                MeterTests(cp, sys);

                // 12. radio aliases (local file, FNE download, cache, display)
                AliasTests(tmp);

                // 13. control socket (sidetone-cli ptt)
                ControlTests(tmp, cp, sys);

                // 14. dedicated PTT device (evdev)
                PttDeviceTests(cp, sys);

                // 15. zones (tabs): switching, hiding, remembering, and the TUI's tab line
                ZoneTests(tmp);

                // 16. log files, the choices both consoles start with, the TUI's key file box and card buttons
                CatchUpTests(tmp, cp, sys);

                // 17. deleting old recordings
                RecordingPruneTests(tmp);

                // 8. WAV writer
                string wavPath = Path.Combine(tmp, "test.wav");
                using (WavSink wav = new WavSink(wavPath))
                    for (int i = 0; i < 50; i++)
                        wav.Write(VoicedFrame(i));
                byte[] wavBytes = File.ReadAllBytes(wavPath);
                uint dataLen = BitConverter.ToUInt32(wavBytes, 40);
                Check(wavBytes.Length == 44 + 50 * 320 && dataLen == 50 * 320 && BitConverter.ToUInt32(wavBytes, 24) == 8000,
                      "WAV file has a valid 8 kHz header and the right length");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { /* ignore */ }
            }

            Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
            return failures == 0 ? 0 : 1;
        }

        // trimmed pw-dump output: two sinks, three sources, a playback stream and the "default" metadata
        private const string PwDumpFixture = """
[
  { "id": 34, "type": "PipeWire:Interface:Node", "info": { "props": {
      "media.class": "Audio/Sink", "object.serial": 34,
      "node.name": "alsa_output.pci-0000_0a_00.3.iec958-stereo",
      "node.description": "Family 17h (Models 00h-0fh) HD Audio Controller Digital Stereo (IEC958)" } } },
  { "id": 77, "type": "PipeWire:Interface:Node", "info": { "props": {
      "media.class": "Audio/Sink", "object.serial": 2077,
      "node.name": "alsa_output.usb-Plantronics_Savi_8200-00.analog-stereo",
      "node.description": "Plantronics Savi 8200 Office Series Analog Stereo" } } },
  { "id": 46, "type": "PipeWire:Interface:Node", "info": { "props": {
      "media.class": "Audio/Source", "object.serial": 46,
      "node.name": "alsa_input.pci-0000_0a_00.3.analog-stereo",
      "node.description": "Family 17h (Models 00h-0fh) HD Audio Controller Analog Stereo" } } },
  { "id": 73, "type": "PipeWire:Interface:Node", "info": { "props": {
      "media.class": "Audio/Source", "object.serial": 73,
      "node.name": "alsa_input.usb-Plantronics_Savi_8200-01.mono-fallback",
      "node.description": "Plantronics Savi 8200 Office Series Mono" } } },
  { "id": 75, "type": "PipeWire:Interface:Node", "info": { "props": {
      "media.class": "Audio/Source", "object.serial": 75,
      "node.name": "alsa_input.usb-USB_2.0_Camera-02.mono-fallback",
      "node.description": "USB 2.0 Camera Mono" } } },
  { "id": 102, "type": "PipeWire:Interface:Node", "info": { "props": {
      "media.class": "Stream/Output/Audio", "node.name": "Brave", "node.description": "Brave" } } },
  { "id": 31, "type": "PipeWire:Interface:Metadata", "props": { "metadata.name": "default" }, "metadata": [
      { "subject": 0, "key": "default.audio.sink", "type": "Spa:String:JSON",
        "value": { "name": "alsa_output.usb-Plantronics_Savi_8200-00.analog-stereo" } },
      { "subject": 0, "key": "default.audio.source", "type": "Spa:String:JSON",
        "value": "{\"name\":\"alsa_input.usb-Plantronics_Savi_8200-01.mono-fallback\"}" } ] }
]
""";

        private static short[] Sine(double amplitude, int n = 160, double hz = 1000)
        {
            short[] b = new short[n];
            for (int i = 0; i < n; i++)
                b[i] = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * hz * i / 8000.0));
            return b;
        }

        private static void MeterTests(Codeplug cp, Codeplug.System sys)
        {
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            LevelMeter m = new LevelMeter();
            Check(!m.Read(t0).Active, "mic meter: idle before any audio");

            m.Push(Sine(16384), t0);                         // -6 dBFS peak, about -9 dBFS RMS
            LevelMeter.Reading r = m.Read(t0);
            Check(r.Active && Math.Abs(r.LevelDb - -9.03) < 0.3 && Math.Abs(r.PeakDb - -6.02) < 0.3 && !r.Clipping,
                  $"mic meter: a -6 dBFS sine reads about -9 dB RMS with a -6 dB peak ({r.LevelDb:F1} / {r.PeakDb:F1})");

            m.Push(new short[160], t0.AddSeconds(0.5));       // silence: the level falls at 20 dB/s, the peak holds
            r = m.Read(t0.AddSeconds(0.5));
            Check(Math.Abs(r.LevelDb - -19.03) < 0.3 && Math.Abs(r.PeakDb - -6.02) < 0.3,
                  $"mic meter: level falls 10 dB in 0.5 s and the peak is held ({r.LevelDb:F1} / {r.PeakDb:F1})");

            m.Push(new short[160], t0.AddSeconds(3.0));       // -19 dB at 0.5 s reaches the -60 floor about 2 s later
            r = m.Read(t0.AddSeconds(3.0));
            Check(r.LevelDb <= LevelMeter.FloorDb + 0.01 && r.PeakDb <= LevelMeter.FloorDb + 0.01, "mic meter: the peak hold expires and silence reads as the floor");
            Check(!m.Read(t0.AddSeconds(3.5)).Active, "mic meter: goes idle when audio stops");

            short[] loud = Sine(32767);
            m.Push(loud, t0.AddSeconds(4));
            Check(m.Read(t0.AddSeconds(4)).Clipping, "mic meter: a full-scale block shows CLIP");
            m.Push(Sine(1000), t0.AddSeconds(6));
            Check(!m.Read(t0.AddSeconds(6)).Clipping, "mic meter: CLIP clears after the hold time");

            // in the TUI: the bottom edge of the title box
            Tui tui = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
            Check(Tui.MeterCells(31) == 13 && Tui.MeterCells(20) == 0, "mic meter: bar width fits the title box, hidden when there's no room");
            string edge = tui.Render(120, 40).RowText(Tui.TitleHeight - 1);
            Check(edge.Contains("MIC off"), "TUI shows 'MIC off' while the microphone is closed");

            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'v'));
            Check(tui.MicMonitor, "TUI v opens the mic for the meter");
            tui.MicLevel.Push(Sine(8192));                    // about -15 dB RMS
            edge = tui.Render(120, 40).RowText(Tui.TitleHeight - 1);
            Check(edge.Contains(" MIC ") && edge.Contains("█") && edge.Contains("dB") && !edge.Contains("MIC off"),
                  $"TUI meter shows the level ({edge.Trim()})");
            tui.MicLevel.Push(loud);
            edge = tui.Render(120, 40).RowText(Tui.TitleHeight - 1);
            Check(edge.Contains("CLIP"), "TUI meter shows CLIP on a full-scale input");

            int at = edge.IndexOf("MIC", StringComparison.Ordinal);
            tui.HandleInput(new InputEvent(InputKind.Click, X: at, Y: Tui.TitleHeight - 1));
            Check(!tui.MicMonitor && tui.Render(120, 40).RowText(Tui.TitleHeight - 1).Contains("MIC off"), "TUI click on the meter closes the mic");
        }

        // from a Surface Pro 11 with the Motorola desktop microphone plugged in (trimmed)
        private const string ProcInputFixture = """
I: Bus=0019 Vendor=045e Product=09ae Version=0111
N: Name="Microsoft Surface 045E:09AE Keyboard"
P: Phys=01:15:02:01:00
H: Handlers=sysrq kbd event2 leds 
B: PROP=0
B: EV=12001f
B: KEY=3f00733fff 0 0 483ffff17aff32d bfd4444600000000 1 130ff38b17d007 ffe77bfad9415fff ffbeffdff3cfffff fffffffffffffffe

I: Bus=0019 Vendor=045e Product=09af Version=0111
N: Name="Microsoft Surface 045E:09AF Mouse"
H: Handlers=mouse0 event7 
B: EV=17
B: KEY=30000 0 0 0 0

I: Bus=0003 Vendor=0cad Product=1804 Version=0111
N: Name="Motorola Solutions Inc. Motorola Desktop Microphone [v1.0.0]"
P: Phys=usb-xhci-hcd.1.auto-1.3.2/input2
H: Handlers=event27 js0 
B: PROP=0
B: EV=13
B: KEY=300000000 0 0 0 0
B: MSC=10

""";

        private static void PttDeviceTests(Codeplug cp, Codeplug.System sys)
        {
            List<InputDeviceInfo> devs = InputDevices.Parse(ProcInputFixture);
            InputDeviceInfo moto = InputDevices.Find(devs, "motorola desktop microphone");
            Check(devs.Count == 3 && moto != null && moto.EventPath == "/dev/input/event27" && moto.Vendor == 0x0cad && moto.Product == 0x1804 &&
                  moto.Keys.SequenceEqual(new[] { 288, 289 }), "PTT device: found by name in /proc/bus/input/devices, with its two buttons");
            Check(InputDevices.Find(devs, "/dev/input/event27") == moto && InputDevices.Find(devs, "footswitch") == null,
                  "PTT device: found by /dev/input path; unknown names find nothing");
            Check(devs[1].Keys.SequenceEqual(new[] { 272, 273 }) && devs[0].Keys.Count > 12, "PTT device: key bitmaps decoded (mouse buttons, full keyboard)");
            Check(InputDevices.ParseButton("BTN_THUMB") == 289 && InputDevices.ParseButton("trigger") == 288 && InputDevices.ParseButton("289") == 289 &&
                  InputDevices.ParseButton("KEY_F13") == 183 && InputDevices.ParseButton("banana") == -1 &&
                  InputDevices.ButtonName(289) == "BTN_THUMB" && InputDevices.ButtonName(288) == "BTN_TRIGGER",
                  "PTT device: button names and codes");

            PttDeviceWatcher w = new PttDeviceWatcher("Motorola", null, null);
            Check(w.ChooseButtons(moto) == null && w.PttCode == 289 && w.MuteCode == 288,
                  "PTT device: the Motorola desktop mic's bar is PTT and its other button is mute, with no options");
            PttDeviceWatcher swapped = new PttDeviceWatcher("Motorola", "BTN_TRIGGER", "none");
            Check(swapped.ChooseButtons(moto) == null && swapped.PttCode == 288 && swapped.MuteCode == -1, "PTT device: --ptt-button and --mute-button none override");
            InputDeviceInfo twoButtons = new InputDeviceInfo { Name = "Pedal", EventPath = "/dev/input/event9", Keys = new List<int> { 256, 257 } };
            InputDeviceInfo oneButton = new InputDeviceInfo { Name = "Pedal", EventPath = "/dev/input/event9", Keys = new List<int> { 256 } };
            string why = new PttDeviceWatcher("Pedal", null, null).ChooseButtons(twoButtons);
            Check(why != null && why.Contains("--ptt-button") && new PttDeviceWatcher("Pedal", null, null).ChooseButtons(oneButton) == null,
                  "PTT device: an unknown device's only button is used; with several, --ptt-button is asked for");
            bool badName = false;
            try { _ = new PttDeviceWatcher("x", "BTN_NOPE", null); } catch (ArgumentException) { badName = true; }
            Check(badName, "PTT device: an unknown --ptt-button name is refused at startup");

            List<string> seen = new List<string>();
            w.Ptt += down => seen.Add(down ? "down" : "up");
            w.Mute += () => seen.Add("mute");
            MemoryStream evs = new MemoryStream();
            void Ev(ushort t, ushort c, int v) { byte[] b = PttDeviceWatcher.EncodeEvent(t, c, v); evs.Write(b, 0, b.Length); }
            Ev(4, 4, 0x90002); Ev(1, 289, 1); Ev(0, 0, 0);      // PTT press (MSC_SCAN, key, SYN as the mic sends them)
            Ev(1, 289, 2);                                      // auto-repeat: ignored
            Ev(1, 288, 1); Ev(1, 288, 0);                       // mute press and release
            Ev(1, 289, 0); Ev(0, 0, 0);                         // PTT release
            evs.Position = 0;
            w.Pump(new SplitStream(evs, 7));                    // events arriving in odd-sized pieces
            Check(seen.SequenceEqual(new[] { "down", "mute", "up" }), $"PTT device: press, mute and release decoded from input events ({string.Join(",", seen)})");

            Tui tui = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
            Tui watching = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true, PttDevice = ("sidetone selftest no such device", null, null) };
            watching.StartPttDevice();
            Check(watching.PttDeviceWatching, "TUI: --ptt-device alone (no button options) starts watching the device");
            watching.StopPttDevice();
            Tui buttons = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true, PttDevice = ("sidetone selftest no such device", "BTN_THUMB", "none") };
            buttons.StartPttDevice();
            Check(buttons.PttDeviceWatching, "TUI: --ptt-device with button options starts watching the device");
            buttons.StopPttDevice();
            // mic mute: u, the device's mute button (same handler), shown on screen, silence to the transmitter
            short[] voice = Sine(8192);
            Check(!tui.MicMuted && ReferenceEquals(tui.MicToTx(voice), voice), "mic mute: off at start; mic audio goes to the transmitter unchanged");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'u'));
            short[] sent = tui.MicToTx(voice);
            string edge = tui.Render(120, 40).RowText(Tui.TitleHeight - 1);
            string footer = tui.Render(120, 40).RowText(40 - Tui.FooterHeight);
            Check(tui.MicMuted && sent.Length == voice.Length && sent.All(v => v == 0) && edge.Contains("MIC MUTED") && footer.Contains("MIC MUTED"),
                  "mic mute: u mutes; the transmitter gets silence; MIC MUTED shows in the title box and the bottom line");
            int at = edge.IndexOf("MIC MUTED", StringComparison.Ordinal);
            tui.HandleInput(new InputEvent(InputKind.Click, X: at, Y: Tui.TitleHeight - 1));
            Check(!tui.MicMuted && !tui.Render(120, 40).RowText(Tui.TitleHeight - 1).Contains("MUTED"), "mic mute: clicking MIC MUTED unmutes");
            tui.ToggleMicMute();
            tui.ToggleMicMute();
            Check(!tui.MicMuted, "mic mute: the device's mute button handler toggles it");

            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Check(!Tui.NoAudioDue(t0.AddSeconds(1.5), t0, DateTime.MinValue, false) && Tui.NoAudioDue(t0.AddSeconds(2.5), t0, DateTime.MinValue, false) &&
                  !Tui.NoAudioDue(t0.AddSeconds(2.5), t0, t0.AddSeconds(1), false) && !Tui.NoAudioDue(t0.AddSeconds(9), t0, DateTime.MinValue, true),
                  "no-audio warning: after 2 s of transmit without mic audio; not while audio arrives; not while muted");
            Check(Tui.RmsDb(new short[160]) <= LevelMeter.FloorDb && Tui.RmsDb(voice) > Tui.NoAudioDb && Tui.RmsDb(Sine(20)) < Tui.NoAudioDb,
                  "no-audio warning: speech counts as audio, a muted-mic trickle (-64 dB) doesn't");

            // helpers shared with the graphical console
            Check(DispatchConsole.TryParseRid(" 9511 ", out uint parsed) && parsed == 9511 && !DispatchConsole.TryParseRid("0", out _) &&
                  !DispatchConsole.TryParseRid("16777216", out _) && !DispatchConsole.TryParseRid("12a", out _) && !DispatchConsole.TryParseRid(null, out _),
                  "radio IDs are accepted from 1 to 16777215 only");
            Check(tui.SendTonePage("100", "1000").Contains("tone A") && tui.SendTonePage("1000", "x").Contains("tone B") &&
                  tui.SendTonePage("1000", "1500") == "not connected to the FNE yet" && tui.ToneBlocker() == "not connected to the FNE yet",
                  "a tone page is checked (range, then whether a tone can be sent) before anything is transmitted");
            Check(DispatchConsole.RadioActionNeedsConfirm(1) && !DispatchConsole.RadioActionNeedsConfirm(0) && !DispatchConsole.RadioActionNeedsConfirm(2),
                  "only inhibit asks to be confirmed");

            tui.OnDevicePtt(true);
            tui.OnDevicePtt(false);
            string text = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(text.Contains("not connected to the FNE yet") && !tui.GlobalMute, "TUI: device PTT while offline is refused in the log; release does nothing");
        }

        /// <summary>A stream that returns at most a few bytes per read.</summary>
        private sealed class SplitStream : Stream
        {
            private readonly Stream inner;
            private readonly int chunk;
            public SplitStream(Stream inner, int chunk) { this.inner = inner; this.chunk = chunk; }
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, chunk));
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static void ControlTests(string tmp, Codeplug cp, Codeplug.System sys)
        {
            string path = Path.Combine(tmp, "ctl.sock");
            ControlSession held = null;
            ManualResetEventSlim heldClosed = new ManualResetEventSlim(false);
            using (ControlServer server = new ControlServer(path, (line, session) =>
            {
                if (line == "ptt hold")
                {
                    held = session;
                    session.Closed += heldClosed.Set;
                }
                return $"ok: {line}";
            }))
            {
                server.Start();
                Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "control: the socket is readable and writable by its owner only");
                Check(ControlClient.Send(path, "status") == "ok: status", "control: a command gets one reply line");

                bool refused = false;
                try { new ControlServer(path, (l, x) => "ok").Start(); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused && ControlClient.IsAlive(path), "control: a second console can't take over a socket that's in use");

                using CancellationTokenSource release = new CancellationTokenSource();
                string early = null;
                Task<string> hold = Task.Run(() => ControlClient.Send(path, "ptt hold", release.Token, hold: true, onReply: r => early = r));
                for (int i = 0; i < 200 && held == null; i++) Thread.Sleep(10);
                Thread.Sleep(100);
                Check(held != null && early == "ok: ptt hold" && !hold.IsCompleted, "control: 'ptt hold' replies at once and keeps the connection open");
                release.Cancel();
                Check(heldClosed.Wait(2000) && hold.Wait(2000), "control: ending the hold client closes the connection, which the console sees");

                held = null;
                Task<string> hold2 = Task.Run(() => ControlClient.Send(path, "ptt hold", CancellationToken.None, hold: true));
                for (int i = 0; i < 200 && held == null; i++) Thread.Sleep(10);
                held?.Close();
                Check(hold2.Wait(2000), "control: the console ending a hold makes the hold client exit");
            }
            Check(!File.Exists(path), "control: the socket file is removed when the console stops");

            File.WriteAllText(path, "stale");
            bool replacedStale;
            using (ControlServer again = new ControlServer(path, (l, x) => "ok: again"))
            {
                again.Start();
                replacedStale = ControlClient.Send(path, "status") == "ok: again";
            }
            Check(replacedStale, "control: a socket left behind by a crashed console is replaced");

            bool noConsole = false;
            try { ControlClient.Send(path, "status"); }
            catch (InvalidOperationException ex) { noConsole = ex.Message.Contains("no Sidetone console"); }
            Check(noConsole, "control: 'sidetone-cli ptt' says so when no console is running");

            // through the TUI (offline): commands run on the UI thread
            string tuiPath = Path.Combine(tmp, "tui.sock");
            Tui tui = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true, ControlSocketPath = tuiPath };
            tui.ClaimUiThread();
            tui.StartControl();
            string Ask(string cmd)
            {
                Task<string> t = Task.Run(() => ControlClient.Send(tuiPath, cmd));
                for (int i = 0; i < 500 && !t.IsCompleted; i++)
                {
                    tui.RunControlCalls();
                    Thread.Sleep(5);
                }
                return t.IsCompleted ? t.Result : "(no reply)";
            }
            string status = Ask("status");
            Check(tui.ControlRunning && status.StartsWith("ok: idle, not connected") && status.Contains("Calling") && status.Contains("tg 504"),
                  $"TUI control: status names the PTT channel ({status})");
            string down = Ask("ptt down");
            Check(down == "error: not connected to the FNE yet", $"TUI control: 'ptt down' while offline is refused with the reason ({down})");
            Check(Ask("ptt up") == "ok: not transmitting" && Ask("PTT  Toggle").StartsWith("error: not connected"),
                  "TUI control: 'ptt up' when idle is fine; commands ignore case and extra spaces");
            Check(Ask("fire the lasers").StartsWith("error: unknown command"), "TUI control: unknown commands are refused");
            tui.StopControl();
            Check(!File.Exists(tuiPath), "TUI control: the socket goes away with the console");
        }

        private static void AliasTests(string tmp)
        {
            string dir = Path.Combine(tmp, "aliases");
            Directory.CreateDirectory(dir);
            string cpPath = Path.Combine(dir, "codeplug.yml");
            File.WriteAllText(cpPath, SampleCodeplug.Replace("    encrypted: false\n", "    encrypted: false\n    syncRadioAliases: true\n")
                                                    .Replace("    encrypted: false\r\n", "    encrypted: false\r\n    syncRadioAliases: true\r\n"));
            File.WriteAllText(Path.Combine(dir, "alias.yml"), "- alias: \"Kara Portable\"\n  rid: 9511\n- alias: \"Base\"\n  rid: 5555\n");
            Codeplug cp = Program.LoadCodeplug(cpPath);
            Codeplug.System sys = cp.Systems.First();
            Check(sys.SyncRadioAliases && sys.AliasPath == "alias.yml", "aliases: syncRadioAliases and the default aliasPath read from the codeplug");

            List<string> log = new List<string>();
            string cache = Path.Combine(dir, "cache");
            RadioAliasBook book = new RadioAliasBook(cp, sys, cache);
            book.Status += log.Add;
            book.Load();
            Check(book.LocalCount == 2 && book.Name(9511) == "Kara Portable" && log.Any(l => l.Contains("2 from") && l.Contains("alias.yml")),
                  "aliases: alias.yml beside the codeplug is loaded");
            Check(book.Describe(9511) == "Kara Portable (9511)" && book.Label(9511) == "Kara Portable" &&
                  book.Describe(777) == "777" && book.Label(777) == "777" && book.Name(0) == null,
                  "aliases: names shown as 'alias (RID)' in the log, the alias on cards, the number when unknown");

            Check(RadioAliasBook.ContainsRid("Kara M - 9511", 9511) && !RadioAliasBook.ContainsRid("Kara M - 95110", 9511) &&
                  !RadioAliasBook.ContainsRid("Unit 19511", 9511) && !RadioAliasBook.ContainsRid("Kara Portable", 9511),
                  "aliases: an alias that already contains its RID is recognised (whole numbers only)");

            byte[] fne = System.Text.Encoding.UTF8.GetBytes("# FNE radio_alias file\n9511,Kara (FNE),\n1234,Dispatch,\n4321,Unit - 4321,\n\n");
            sys.NetworkRidAliases = dvmconsole.AliasTools.ParseNetworkAliases(fne);
            Check(book.Describe(4321) == "Unit - 4321" && book.Describe(1234) == "Dispatch (1234)",
                  "aliases: the log doesn't repeat a RID the alias already shows");
            Check(book.Name(9511) == "Kara (FNE)" && book.Name(1234) == "Dispatch" && book.Name(5555) == "Base" && book.Count == 4 && book.Summary == "aliases 4",
                  "aliases: FNE names win, RIDs missing from the download fall back to the local file");
            bool rejected = false;
            try { dvmconsole.AliasTools.ParseNetworkAliases(System.Text.Encoding.UTF8.GetBytes("9511,Kara,extra\n")); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "aliases: a malformed FNE alias list is rejected (the current aliases are kept)");

            dvmconsole.RadioAliasCache.Save(cache, sys, fne);
            sys.NetworkRidAliases = null;
            log.Clear();
            new RadioAliasBook(cp, sys, cache) { }.Load();
            Check(sys.NetworkRidAliases?.Count == 3 && sys.NetworkRidAliases[1234] == "Dispatch", "aliases: the FNE download is cached and loaded at the next start");

            sys.SyncRadioAliases = false;
            Check(book.Name(9511) == "Kara Portable" && book.Name(1234) == null, "aliases: with syncRadioAliases off, only the local file is used");
            sys.SyncRadioAliases = true;

            Tui tui = new Tui(cp, sys, TimeSpan.FromSeconds(120), null, settingsPath: Path.Combine(dir, "settings.json")) { NoAudio = true };
            tui.Aliases.Load();
            tui.consoleRidForTest = 9000001;
            tui.OnP25Control(P25Defines.TSBK_IOSP_CALL_ALRT, 1234, 9000001, null);
            string screenText = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.Render(200, 40).RowText(1).Contains("aliases 4"), "TUI title shows the alias count (when the title box has room)");
            Check(screenText.Contains("call alert (page) from Dispatch (1234)") && screenText.Contains("Dispatch"),
                  "TUI log and recent calls show the alias");
        }

        private static void AudioDeviceTests()
        {
            AudioDeviceList list = AudioDeviceList.Parse(PwDumpFixture);
            Check(list.Outputs.Count == 2 && list.Inputs.Count == 3, "pw-dump: outputs and inputs found, streams left out");
            Check(list.DefaultOutput?.Id == 77 && list.DefaultInput?.Id == 73, "pw-dump: default output and input read from metadata (object and string forms)");

            AudioDevice savi = AudioDeviceList.Resolve(list.Outputs, "savi", "--output");
            Check(savi.Id == 77, "--output matches a unique part of the name, case ignored");
            Check(AudioDeviceList.Resolve(list.Outputs, "34", "--output").Id == 34 &&
                  AudioDeviceList.Resolve(list.Outputs, "2077", "--output").Id == 77, "--output matches an ID or a serial");
            Check(AudioDeviceList.Resolve(list.Inputs, "alsa_input.usb-USB_2.0_Camera-02.mono-fallback", "--input").Id == 75,
                  "--input matches an exact node name");

            string Error(Action a)
            {
                try { a(); return null; }
                catch (ArgumentException ex) { return ex.Message; }
            }
            string ambiguous = Error(() => AudioDeviceList.Resolve(list.Inputs, "mono", "--input"));
            Check(ambiguous != null && ambiguous.Contains("more than one") && ambiguous.Contains("USB 2.0 Camera Mono"),
                  "an ambiguous name is refused and the matches are listed");
            string missing = Error(() => AudioDeviceList.Resolve(list.Outputs, "HDMI", "--output"));
            Check(missing != null && missing.Contains("Plantronics Savi 8200 Office Series Analog Stereo"),
                  "an unknown name is refused and the outputs are listed");

            AudioSelection defaults = AudioSelection.Choose(null, null, () => list);
            Check(defaults.OutputTarget == null && defaults.InputTarget == null &&
                  defaults.OutputReport == "audio output: Plantronics Savi 8200 Office Series Analog Stereo (system default)" &&
                  defaults.InputReport == "audio input: Plantronics Savi 8200 Office Series Mono (system default)",
                  "no options: the system defaults are used and named in the log");

            AudioSelection chosen = AudioSelection.Choose("IEC958", "camera", () => list);
            Check(chosen.OutputTarget == "alsa_output.pci-0000_0a_00.3.iec958-stereo" &&
                  chosen.InputTarget == "alsa_input.usb-USB_2.0_Camera-02.mono-fallback" &&
                  chosen.OutputReport.EndsWith("(--output)") && chosen.InputReport.EndsWith("(--input)"),
                  "--output / --input: the chosen devices are targeted by node name and named in the log");

            AudioSelection noPw = AudioSelection.Choose(null, null, () => throw new InvalidOperationException("pw-dump not found"));
            Check(noPw.OutputTarget == null && noPw.OutputReport.Contains("PipeWire default") && noPw.OutputReport.Contains("pw-dump not found"),
                  "without pw-dump the defaults are still used and the log says why they can't be named");
        }

        private static void TuiTests(Codeplug cp, Codeplug.System sys)
        {
            // input parser
            InputParser parser = new InputParser();
            List<InputEvent> ev = parser.Feed(System.Text.Encoding.ASCII.GetBytes("\x1b[A\x1b[B\x1b[C\x1b[D \rq\x03"));
            Check(ev.Select(e => e.Key).SequenceEqual(new[] { InputKey.Up, InputKey.Down, InputKey.Right, InputKey.Left, InputKey.Space, InputKey.Enter, InputKey.Char, InputKey.CtrlC }) &&
                  ev[6].Char == 'q', "TUI input: arrows, Space, Enter, letters and Ctrl+C parsed");
            ev = parser.Feed(System.Text.Encoding.ASCII.GetBytes("\x1b[<0;12;5M\x1b[<0;12;5m\x1b[<64;3;4M\x1b[<65;3;4M\x1b[5~\x1b[6~"));
            Check(ev.Count == 5 && ev[0].Kind == InputKind.Click && ev[0].X == 11 && ev[0].Y == 4 && ev[1].Kind == InputKind.WheelUp &&
                  ev[2].Kind == InputKind.WheelDown && ev[3].Key == InputKey.PageUp && ev[4].Key == InputKey.PageDown, "TUI input: SGR mouse click (release ignored) and wheel parsed");
            Check(parser.Feed(new byte[] { 0x1b, (byte)'[', (byte)'<', (byte)'0', (byte)';' }).Count == 0 &&
                  parser.Feed(System.Text.Encoding.ASCII.GetBytes("7;2M")) is { Count: 1 } split && split[0].Kind == InputKind.Click && split[0].X == 6,
                  "TUI input: a mouse report split across reads is reassembled");
            ev = parser.Feed(new byte[] { 0x1b });
            Check(ev.Count == 1 && ev[0].Key == InputKey.Escape, "TUI input: a lone Esc is the Escape key");

            // screen buffer
            ScreenBuffer sb = new ScreenBuffer();
            sb.Begin(20, 3);
            int used = sb.Text(0, 0, "AES 🔒 x", Style.Plain);
            Check(used == 8 && sb.RowText(0).StartsWith("AES 🔒 x"), "TUI screen: the lock emoji takes two cells");
            sb.Text(0, 1, "abcdefghijklmnop", Style.Plain, 6);
            Check(sb.RowText(1).StartsWith("abcde… "), "TUI screen: long text is truncated with an ellipsis");
            bool hit = false;
            sb.OnClick(new Rect(2, 2, 4, 1), () => hit = true);
            sb.HitTest(3, 2)?.Invoke();
            Check(hit && sb.HitTest(6, 2) == null, "TUI screen: clicks map to the region under the pointer");
            Check(sb.Flush().Contains("🔒") && !sb.Flush().Contains("🔒"), "TUI screen: unchanged frames send nothing new");
            sb.Begin(10, 1);
            sb.Text(0, 0, "9511", Style.Plain);
            string plain = sb.Flush();
            Check(Style.Plain.Fg == Ansi.Default && Style.Plain.Bg == Ansi.Default && !plain.Contains("[0;30") && !plain.Contains(";40"),
                  "TUI screen: plain text uses the terminal's default colours (not black on black)");

            // grid geometry
            Tui.GridGeometry g = Tui.Layout(120, 40, 10);
            Check(g.Columns == 4 && g.CardWidth == 30 && g.TotalRows == 3 && g.LogHeight >= 6 && g.LogHeight <= 10, $"TUI grid: 4 cards across at 120 columns ({g.VisibleRows} rows visible, log {g.LogHeight})");
            Check(Tui.Layout(60, 40, 10).Columns == 2 && Tui.Layout(300, 40, 10).Columns == 4, "TUI grid: fewer columns on narrow terminals, never more than 4");

            // clicks on a rendered frame
            Tui tui = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
            ScreenBuffer frame = tui.Render(120, 40);
            Check(frame.RowText(1).Contains("PTT") && frame.RowText(1).Contains("MUTE") && frame.RowText(1).Contains("EXIT"), "TUI title bar has PTT, MUTE and EXIT buttons");
            Check(frame.RowText(Tui.TitleHeight).Contains("Calling") && frame.RowText(Tui.TitleHeight).Contains("Secure"), "TUI cards for both channels drawn side by side");
            Check(frame.RowText(Tui.TitleHeight + 1).Contains("🔒"), "TUI secure card shows the lock");
            Check(frame.RowText(Tui.TitleHeight).StartsWith("┏") && frame.RowText(Tui.TitleHeight).Contains("▶ Calling") &&
                  frame.RowText(Tui.TitleHeight).Substring(30).TrimStart().StartsWith("╭"), "TUI focused card has a heavy border; others are thin");
            Tui.GridGeometry bottom = Tui.Layout(120, 60, 2);
            Check(bottom.LogHeight <= 10 && bottom.LogTop + bottom.LogHeight == 60 - Tui.FooterHeight, "TUI log is capped and sits just above the footer");
            Check(Enumerable.Range(0, 40).Any(y => frame.RowText(y).Contains("Recent calls")), "TUI recent-calls panel drawn beside the log");

            tui.HandleInput(new InputEvent(InputKind.Click, X: 30 + 5, Y: Tui.TitleHeight + 3));    // body of card 2
            Check(tui.Cursor == 1 && tui.IsSelected(1) && !tui.IsSelected(0), "TUI click on a card focuses it and starts listening");
            tui.Render(120, 40);
            string top = tui.Render(120, 40).RowText(Tui.TitleHeight);
            int toggle = top.IndexOf("[●]", StringComparison.Ordinal);
            Check(toggle > 30, "TUI listening card shows [●]");
            tui.HandleInput(new InputEvent(InputKind.Click, X: toggle + 1, Y: Tui.TitleHeight));
            Check(!tui.IsSelected(1), "TUI click on [●] stops listening");

            tui.Render(120, 40);
            string title = tui.Render(120, 40).RowText(1);
            tui.HandleInput(new InputEvent(InputKind.Click, X: title.IndexOf("MUTE", StringComparison.Ordinal), Y: 0));
            Check(tui.GlobalMute, "TUI MUTE button mutes all audio");
            tui.Render(120, 40);
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Left));
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Space));    // PTT while offline: refused, no crash
            tui.Render(120, 40);
            tui.HandleInput(new InputEvent(InputKind.Click, X: title.IndexOf("EXIT", StringComparison.Ordinal), Y: 2));
            Check(tui.Cursor == 0 && tui.QuitRequested, "TUI keyboard focus, offline PTT refusal and EXIT button");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, '3'));    // offline: refused, no crash
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'c'));
            string titleRow = tui.Render(120, 40).RowText(1);
            Check(tui.MarkerOn && titleRow.Contains("T1") && titleRow.Contains("T5") && titleRow.Contains("MARK"),
                  "TUI tone buttons T1-T5 and MARK in the title bar; c toggles the marker");
            Check(titleRow.Contains("PAGE"), "TUI PAGE button in the title bar");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'p'));
            foreach (char ch in "11534") tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, ch));   // digits go to the field, not alert tones
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Backspace));
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, '.'));
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, '4'));
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Tab));
            foreach (char ch in "99") tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, ch));
            string dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.PageDialogOpen && dialog.Contains("Tone (QC II)") && dialog.Contains("1153.4") && dialog.Contains("SEND"),
                  "TUI page dialog opens with p and takes typed tones");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));    // 99 Hz: refused, dialog stays open
            dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.PageDialogOpen && dialog.Contains("tone B must be"), "TUI page dialog refuses an out-of-range tone");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Escape));
            Check(!tui.PageDialogOpen, "TUI page dialog closes with Esc");

            // digital page (P25 call alert)
            byte[] alert = CliPeer.BuildCallAlert(1234567, 9511);
            // P25 layout: octet 0 opcode, 1 MFID, 2-3 reserved, 4-6 target, 7-9 source, 10-11 CRC
            // (checked on the bytes: fnecore's TSBK Decode reads the payload one octet early)
            Check(alert.Length == P25Defines.P25_TSBK_LENGTH_BYTES && (alert[0] & 0x3F) == P25Defines.TSBK_IOSP_CALL_ALRT && alert[1] == 0 &&
                  alert[2] == 0 && alert[3] == 0 && (alert[4] << 16 | alert[5] << 8 | alert[6]) == 1234567 &&
                  (alert[7] << 16 | alert[8] << 8 | alert[9]) == 9511 && fnecore.EDAC.CRC.CheckCCITT162(alert, P25Defines.P25_TSBK_LENGTH_BYTES),
                  "digital page: call alert TSBK carries target and source RIDs with a valid CRC");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'd'));
            foreach (char ch in "1234567") tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, ch));
            dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.PageDialogOpen && tui.PageDialogDigital && dialog.Contains("Radio ID") && dialog.Contains("1234567"),
                  "TUI d opens the page dialog in digital mode and takes a radio ID");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));    // offline: refused with a message
            dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.PageDialogOpen && dialog.Contains("not connected"), "TUI digital page refused while offline, dialog stays open");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Left));
            Check(!tui.PageDialogDigital, "TUI arrows switch the page dialog between tone and digital");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Escape));

            // radio check / inhibit / uninhibit
            byte[] check = CliPeer.BuildExtendedFunction(ExtendedFunction.CHECK, 9511, 1234567);
            byte[] inhibit = CliPeer.BuildExtendedFunction(ExtendedFunction.INHIBIT, P25Defines.WUID_FNE, 1234567);
            Check((check[0] & 0x3F) == P25Defines.TSBK_IOSP_EXT_FNCT && check[2] == 0x00 && check[3] == 0x00 &&
                  (check[4] << 16 | check[5] << 8 | check[6]) == 9511 && (check[7] << 16 | check[8] << 8 | check[9]) == 1234567 &&
                  inhibit[3] == 0x7F && (inhibit[4] << 16 | inhibit[5] << 8 | inhibit[6]) == P25Defines.WUID_FNE &&
                  fnecore.EDAC.CRC.CheckCCITT162(check, P25Defines.P25_TSBK_LENGTH_BYTES) && fnecore.EDAC.CRC.CheckCCITT162(inhibit, P25Defines.P25_TSBK_LENGTH_BYTES),
                  "radio check / inhibit TSBKs carry function, argument and target with valid CRCs");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'r'));
            foreach (char ch in "7654321") tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, ch));
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Tab));
            dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.RadioDialogOpen && tui.RadioDialogAction == "Inhibit" && dialog.Contains("Radio command") && dialog.Contains("7654321") &&
                  dialog.Contains("Uninhibit"), "TUI r opens the radio command box: radio ID, then Check / Inhibit / Uninhibit");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));
            dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.RadioDialogOpen && dialog.Contains("press SEND again"), "TUI inhibit asks for confirmation first");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));
            dialog = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(tui.RadioDialogOpen && dialog.Contains("not connected"), "TUI radio command refused while offline");
            // a radio's check ack, as seen in an FNE log: tgt = the radio, arg = the console RID
            byte[] ack = CliPeer.BuildExtendedFunction(ExtendedFunction.CHECK_ACK, 99511, 9511);
            byte[] fecAck = new byte[P25Defines.P25_TSBK_FEC_LENGTH_BYTES];
            new fnecore.EDAC.Trellis().Encode12(ack, ref fecAck);
            byte[] tsduFrame = new byte[24 + P25Defines.P25_TSDU_FRAME_LENGTH_BYTES];
            byte[] rawTsdu = new byte[P25Defines.P25_TSDU_FRAME_LENGTH_BYTES];
            P25Interleaver.Encode(fecAck, ref rawTsdu, 114, 318);
            Buffer.BlockCopy(rawTsdu, 0, tsduFrame, 24, rawTsdu.Length);
            byte[] back = CliPeer.DecodeTsdu(tsduFrame);
            Check(back != null && back.SequenceEqual(ack), "network TSDU frame decodes back to its TSBK (trellis, interleave, CRC)");
            tui.consoleRidForTest = 99511;
            tui.OnP25Control(P25Defines.TSBK_IOSP_EXT_FNCT, 9511, 9511, back);
            string logRows = string.Join("\n", Enumerable.Range(0, 40).Select(y => tui.Render(120, 40).RowText(y)));
            Check(logRows.Contains("radio 9511 answered the radio check"), "radio check ack addressed to the radio (console RID in the argument) is reported");
            tui.HandleInput(new InputEvent(InputKind.Key, InputKey.Escape));
            Check(!tui.RadioDialogOpen && tui.Render(120, 40).RowText(1).Contains("RADIO") && tui.Render(120, 40).RowText(1).Contains("T6"),
                  "TUI title bar has T6 and RADIO");

            // patching: a call on one member goes out on the others as the same codewords
            {
                const uint consoleRid = 9000001, radio = 1234;
                Tui pt = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
                CliPeer headers = new CliPeer(sys, new KeyStore(), verbose: false);
                CaptureSink sentOut = new CaptureSink(headers);
                pt.sinkForTest = sentOut;
                pt.consoleRidForTest = consoleRid;
                pt.patchManualPump = true;      // the test drives the patch clock
                pt.Keys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey);
                ConsoleChannel calling = pt.Channels.First(ch => ch.IsP25 && !ch.Encrypted);
                ConsoleChannel secureCh = pt.Channels.First(ch => ch.Encrypted);

                // a clear call from a radio on Calling
                CaptureSink onAir = new CaptureSink(headers);
                P25Transmitter talker = new P25Transmitter(onAir, new KeyStore(), radio, calling.Tg, P25Defines.P25_ALGO_UNENCRYPT, 0);
                List<byte[]> spoken = new List<byte[]>();
                talker.ClearFrame += spoken.Add;
                talker.Start(out _);
                for (int i = 0; i < 36; i++) talker.PushAudio(VoicedFrame(i));
                uint airStream = talker.StreamId;
                talker.Stop();

                pt.SetPatch(calling, 1);
                bool alone = sentOut.Tdus.Count == 0 && calling.Selected && pt.PatchName(calling) == "Patch 1" && pt.PatchNames.Count == 3;
                pt.SetPatch(secureCh, 1);
                Check(alone && secureCh.Selected && pt.PatchMembers(1).Count == 2 &&
                      string.Join("\n", pt.SnapshotLog(20)).Contains("joins secure and clear channels"),
                      "patch: joining starts listening, and a patch of secure and clear channels is warned about");

                foreach (var v in onAir.Voice)
                    calling.Rx.Handle(calling.Tg, radio, (P25DUID)v.Frame[22], v.Stream, v.Frame);
                bool during = pt.GetStatus(secureCh).Activity == ChannelActivity.Transmitting && pt.GetStatus(secureCh).Text.Contains("patch");
                calling.Rx.Handle(calling.Tg, radio, P25DUID.TDU, airStream, new byte[24]);
                for (int ms = 0; ms <= 2000; ms += 10)
                    pt.PatchPump(TimeSpan.FromMilliseconds(ms));       // what was buffered drains out

                uint outStream = sentOut.Voice.Count > 0 ? sentOut.Voice[0].Stream : 0;
                Check(during && sentOut.Voice.Count == 4 && outStream != 0 && outStream != airStream && sentOut.Voice.All(v => v.Stream == outStream) &&
                      sentOut.Tdus.Count == 5 && sentOut.Tdus[0].Grant && sentOut.Tdus.All(t => t.Dst == secureCh.Tg && t.Src == consoleRid) &&
                      sentOut.Voice[0].Frame[P25VoiceReceiver.HduAlgOffset] == P25Defines.P25_ALGO_AES,
                      $"patch: a clear call on one member is sent on the secure member as its own call, encrypted, and ended with it ({sentOut.Voice.Count} LDUs, {sentOut.Tdus.Count} TDUs)");
                Check(sentOut.TduEncrypted.Count == 1 && sentOut.TduEncrypted[0] && onAir.TduEncrypted.Count == 1 && !onAir.TduEncrypted[0],
                      "grant demand: an encrypted call's says it's encrypted, a clear call's doesn't");
                byte[] encGrant = headers.BuildTdu(consoleRid, 500, grantDemand: true, encrypted: true);
                byte[] clearGrant = headers.BuildTdu(consoleRid, 504, grantDemand: true, encrypted: false);
                byte[] endTdu = headers.BuildTdu(consoleRid, 500, grantDemand: false, encrypted: true);
                Check(encGrant[14] == 0x88 && clearGrant[14] == 0x80 && endTdu[14] == 0x00 && encGrant[22] == (byte)P25DUID.TDU && encGrant[23] == 24 &&
                      ((encGrant[8] << 16) | (encGrant[9] << 8) | encGrant[10]) == 500 && ((encGrant[5] << 16) | (encGrant[6] << 8) | encGrant[7]) == consoleRid,
                      $"grant demand: the TDU's control byte has the grant encrypt flag (0x08) for an encrypted call, as dvmhost expects (0x{encGrant[14]:X2} / 0x{clearGrant[14]:X2} / 0x{endTdu[14]:X2})");
                List<byte[]> heard = RoundTrip(sentOut, secureCh.Tg, consoleRid, pt.Keys, out long patchMuted);
                Check(patchMuted == 0 && SameCodewords(heard, spoken), "patch: the voice arrives as exactly the codewords that were spoken (no second pass through the vocoder)");

                // the FNE sending our own patch traffic back must not start it going the other way
                int before = sentOut.Voice.Count, tdusBefore = sentOut.Tdus.Count;
                foreach (var v in sentOut.Voice.Take(before).ToList())
                    secureCh.Rx.Handle(secureCh.Tg, consoleRid, (P25DUID)v.Frame[22], v.Stream, v.Frame);
                for (int ms = 3000; ms <= 5000; ms += 10)
                    pt.PatchPump(TimeSpan.FromMilliseconds(ms));
                Check(sentOut.Voice.Count == before && sentOut.Tdus.Count == tdusBefore, "patch: its own traffic coming back is not patched again");

                // the console's own PTT on a member keys the rest of the patch with the same voice
                CaptureSink mainOut = new CaptureSink(headers);
                P25Transmitter main = new P25Transmitter(mainOut, pt.Keys, consoleRid, calling.Tg, P25Defines.P25_ALGO_UNENCRYPT, 0);
                main.Start(out _);
                pt.StartPatchTx(calling, main);
                for (int i = 0; i < 18; i++) main.PushAudio(VoicedFrame(i));
                pt.StopPatchTx();
                main.Stop();
                Check(mainOut.Voice.Count == 2 && sentOut.Voice.Count == before + 2 && sentOut.Tdus.Count == tdusBefore + 5,
                      "patch: the console's own transmission on a member also goes out on the rest of the patch");

                pt.Deselect(secureCh);
                bool left = secureCh.Patch == 0 && pt.PatchMembers(1).Count == 1;
                pt.ClearPatches();
                Check(left && calling.Patch == 0, "patch: a channel that stops listening leaves its patch; clearing empties them all");
            }

            // patching at a steady pace: a call that arrives in bursts goes out one LDU every 180 ms
            {
                const uint paceRid = 9000001, paceRadio = 4321;
                Tui pace = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
                CliPeer paceHeaders = new CliPeer(sys, new KeyStore(), verbose: false);
                CaptureSink paceOut = new CaptureSink(paceHeaders);
                pace.sinkForTest = paceOut;
                pace.consoleRidForTest = paceRid;
                pace.patchManualPump = true;
                pace.Keys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey);
                ConsoleChannel paceFrom = pace.Channels.First(ch => ch.IsP25 && !ch.Encrypted);
                ConsoleChannel paceTo = pace.Channels.First(ch => ch.Encrypted);
                pace.SetPatch(paceFrom, 1);
                pace.SetPatch(paceTo, 1);

                CaptureSink paceAir = new CaptureSink(paceHeaders);
                P25Transmitter paceTalker = new P25Transmitter(paceAir, new KeyStore(), paceRadio, paceFrom.Tg, P25Defines.P25_ALGO_UNENCRYPT, 0);
                paceTalker.Start(out _);
                for (int i = 0; i < 6 * 9; i++) paceTalker.PushAudio(VoicedFrame(i));      // six LDUs
                uint paceStream = paceTalker.StreamId;
                paceTalker.Stop();
                void Feed(int ldu) => paceFrom.Rx.Handle(paceFrom.Tg, paceRadio, (P25DUID)paceAir.Voice[ldu].Frame[22], paceStream, paceAir.Voice[ldu].Frame);

                // one LDU in: the talkgroup is asked for at once, but nothing is sent until two are waiting
                Feed(0);
                pace.PatchPump(TimeSpan.FromMilliseconds(0));
                bool asked = paceOut.Tdus.Count == 1 && paceOut.Tdus[0].Grant && paceOut.Voice.Count == 0;
                pace.PatchPump(TimeSpan.FromMilliseconds(100));
                bool waiting = paceOut.Voice.Count == 0;

                // then four more arrive in one burst: they go out evenly, one every 180 ms
                for (int l = 1; l <= 4; l++) Feed(l);
                List<int> sentAt = new List<int>();
                for (int ms = 100; ms <= 1100; ms += 10)
                {
                    int had = paceOut.Voice.Count;
                    pace.PatchPump(TimeSpan.FromMilliseconds(ms));
                    for (int k = had; k < paceOut.Voice.Count; k++)
                        sentAt.Add(ms);
                }
                List<int> steps = sentAt.Zip(sentAt.Skip(1), (a, b) => b - a).ToList();
                Check(asked && waiting && sentAt.Count == 5 && sentAt[0] == 100 && steps.All(d => d == 180),
                      $"patch pacing: the grant is asked for at once, sending starts with two LDUs buffered, and a burst goes out one LDU every 180 ms (at {string.Join(", ", sentAt)} ms)");

                // the source falls behind: wait for a buffer again, then the last LDU and the end drain out before the call ends
                Feed(5);
                for (int ms = 1110; ms <= 1500; ms += 10)
                    pace.PatchPump(TimeSpan.FromMilliseconds(ms));
                bool heldBack = paceOut.Voice.Count == 5;
                paceFrom.Rx.Handle(paceFrom.Tg, paceRadio, P25DUID.TDU, paceStream, new byte[24]);
                bool stillSending = paceOut.Tdus.Count == 1 && pace.GetStatus(paceTo).Text.Contains("patch");
                for (int ms = 1510; ms <= 2500; ms += 10)
                    pace.PatchPump(TimeSpan.FromMilliseconds(ms));
                string endLine = pace.SnapshotLog(10).LastOrDefault(l => l.Contains("Patch 1: end")) ?? string.Empty;
                Check(heldBack && stillSending && paceOut.Voice.Count == 6 && paceOut.Tdus.Count == 5 &&
                      endLine.Contains("54 of 54 frames sent") && endLine.Contains("fell behind 1×") && endLine.Contains("longest gap"),
                      $"patch pacing: a source that falls behind is waited for, and an ended call drains what's buffered before it ends ('{endLine}')");

                List<byte[]> paceHeard = RoundTrip(paceOut, paceTo.Tg, paceRid, pace.Keys, out long paceMuted);
                List<byte[]> paceSpoken = new List<byte[]>();
                foreach (var v in paceAir.Voice)
                {
                    P25VoiceReceiver.TryExtractCodewords(v.Frame, (P25DUID)v.Frame[22] == P25DUID.LDU1 ? P25VoiceReceiver.Ldu1FirstRecord : P25VoiceReceiver.Ldu2FirstRecord, out byte[][] cws);
                    paceSpoken.AddRange(cws);
                }
                Check(paceMuted == 0 && SameCodewords(paceHeard, paceSpoken), "patch pacing: every frame still arrives, unchanged and in order");
                pace.ClearPatches();
            }

            // the same with the real timer, as on the air: a burst of four LDUs takes at least three LDU times to go out
            {
                Tui live = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
                CliPeer liveHeaders = new CliPeer(sys, new KeyStore(), verbose: false);
                CaptureSink liveOut = new CaptureSink(liveHeaders);
                live.sinkForTest = liveOut;
                live.consoleRidForTest = 9000001;
                live.Keys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey);
                ConsoleChannel liveFrom = live.Channels.First(ch => ch.IsP25 && !ch.Encrypted);
                ConsoleChannel liveTo = live.Channels.First(ch => ch.Encrypted);
                live.SetPatch(liveFrom, 1);
                live.SetPatch(liveTo, 1);

                CaptureSink liveAir = new CaptureSink(liveHeaders);
                P25Transmitter liveTalker = new P25Transmitter(liveAir, new KeyStore(), 4321, liveFrom.Tg, P25Defines.P25_ALGO_UNENCRYPT, 0);
                liveTalker.Start(out _);
                for (int i = 0; i < 36; i++) liveTalker.PushAudio(VoicedFrame(i));
                uint liveStream = liveTalker.StreamId;
                liveTalker.Stop();

                System.Diagnostics.Stopwatch liveClock = System.Diagnostics.Stopwatch.StartNew();
                foreach (var v in liveAir.Voice)
                    liveFrom.Rx.Handle(liveFrom.Tg, 4321, (P25DUID)v.Frame[22], liveStream, v.Frame);
                liveFrom.Rx.Handle(liveFrom.Tg, 4321, P25DUID.TDU, liveStream, new byte[24]);
                long firstAt = -1, lastAt = -1;
                int seen = 0;
                while (liveClock.ElapsedMilliseconds < 5000 && Volatile.Read(ref liveOut.TduCount) < 5)
                {
                    int now = Volatile.Read(ref liveOut.VoiceCount);
                    if (now > seen)
                    {
                        if (seen == 0) firstAt = liveClock.ElapsedMilliseconds;
                        lastAt = liveClock.ElapsedMilliseconds;
                        seen = now;
                    }
                    Thread.Sleep(2);
                }
                if (Volatile.Read(ref liveOut.VoiceCount) > seen)     // the last LDU and the end go out in the same tick
                {
                    seen = Volatile.Read(ref liveOut.VoiceCount);
                    lastAt = liveClock.ElapsedMilliseconds;
                }
                long spread = lastAt - firstAt;
                Check(seen == 4 && Volatile.Read(ref liveOut.TduCount) == 5 && spread >= 3 * 180 - 40 && spread < 3 * 180 + 400,
                      $"patch pacing: with the real timer, a burst goes out at the LDU rate and the call ends ({seen} LDUs over {spread} ms)");
                live.ClearPatches();
            }

            // the audio device layer: everything plays and records through the backend
            {
                FakeAudio fake = new FakeAudio();
                IAudioBackend real = AudioBackend.Current;
                try
                {
                    AudioBackend.Current = fake;
                    Tui ft = new Tui(cp, sys, TimeSpan.FromSeconds(120), null);
                    ConsoleChannel ch = ft.Channels.First(x => x.IsP25 && !x.Encrypted);
                    ft.Select(ch);
                    bool opened = fake.Outputs.Count == 1 && ch.Sink == fake.Outputs[0];
                    ch.Sink.Write(new short[160]);
                    ft.OpenMic(out string micError);
                    fake.Input?.Raise(Enumerable.Range(0, 160).Select(i => (short)(i % 2 == 0 ? 8000 : -8000)).ToArray());
                    bool metered = ft.MicLevel.Read().Active;
                    ft.AddDemoCall(ch, 1234, "Unit 1", 1, false, "clear", DateTime.Now);
                    ft.ReplayLast(ch);
                    bool replayed = fake.Outputs.Count == 2;
                    for (int i = 0; i < 200 && fake.Outputs[1].Blocks < 50; i++)
                        Thread.Sleep(20);
                    ft.Deselect(ch, quiet: true);
                    Check(opened && micError == null && fake.Input != null && metered && replayed && fake.Outputs[1].Blocks == 50 &&
                          AudioSelection.Choose(null, null).OutputReport.Contains("Fake default"),
                          $"audio layer: channels, the microphone, replays and device lists all go through the backend ({fake.Outputs.Count} outputs)");
                    ft.Select(ch);
                    int logged = ft.SnapshotLog(50).Count;
                    fake.Outputs[2].Fail("the device went away");
                    Check(ft.SnapshotLog(50).Any(l => l.Contains("the device went away")), "audio layer: a backend reporting a failed output is logged on the channel");
                }
                finally
                {
                    AudioBackend.Current = real;
                }
                Check(AudioBackend.Current is PipeWireBackend && AudioBackend.Current.Name == "PipeWire", "audio layer: Linux uses the PipeWire backend");
            }

            // audio processing
            {
                short[] Tone(double hz, double dbfs, int blocks, int seed = 0)
                {
                    double amp = Math.Pow(10, dbfs / 20) * 32767;
                    short[] t = new short[blocks * 160];
                    for (int i = 0; i < t.Length; i++)
                        t[i] = (short)Math.Round(amp * Math.Sin(2 * Math.PI * hz * (i + seed) / 8000.0));
                    return t;
                }
                double LevelDb(short[] x, int from)
                {
                    double sum = 0;
                    for (int i = from; i < x.Length; i++) sum += (double)x[i] * x[i];
                    return 20 * Math.Log10(Math.Sqrt(sum / (x.Length - from)) / 32768.0);
                }
                short[] Run(VoiceProcessor p, short[] x)
                {
                    short[] y = (short[])x.Clone();
                    for (int i = 0; i + 160 <= y.Length; i += 160)
                    {
                        short[] b = y.AsSpan(i, 160).ToArray();
                        p.Process(b);
                        b.CopyTo(y, i);
                    }
                    return y;
                }

                VoiceProcessing lowOnly = new VoiceProcessing(true, false, false, false);
                double hum = LevelDb(Run(new VoiceProcessor(lowOnly), Tone(60, -20, 100)), 8000) - LevelDb(Tone(60, -20, 100), 8000);
                double voice = LevelDb(Run(new VoiceProcessor(lowOnly), Tone(1000, -20, 100)), 8000) - LevelDb(Tone(1000, -20, 100), 8000);
                Check(hum < -18 && Math.Abs(voice) < 0.5, $"processing: the low cut takes 60 Hz hum down {-hum:F0} dB and leaves 1 kHz alone ({voice:+0.0;-0.0} dB)");

                VoiceProcessor agc = new VoiceProcessor(new VoiceProcessing(false, true, false, false));
                double quietOut = LevelDb(Run(agc, Tone(800, -34, 250)), 30000);
                VoiceProcessor agc2 = new VoiceProcessor(new VoiceProcessing(false, true, false, false));
                double loudOut = LevelDb(Run(agc2, Tone(800, -8, 250)), 30000);
                VoiceProcessor agc3 = new VoiceProcessor(new VoiceProcessing(false, true, false, false));
                double faintOut = LevelDb(Run(agc3, Tone(800, -50, 250)), 30000) - LevelDb(Tone(800, -50, 250), 30000);
                Check(Math.Abs(quietOut - VoiceProcessor.TargetDb) < 2.5 && loudOut < -15 && loudOut > -21 && Math.Abs(faintOut) < 0.5,
                      $"processing: auto level brings quiet speech up ({quietOut:F1} dBFS) and loud speech down ({loudOut:F1}), and leaves near-silence alone ({faintOut:+0.0;-0.0} dB)");

                VoiceProcessor lim = new VoiceProcessor(new VoiceProcessing(false, false, false, true));
                short[] hot = Run(lim, Tone(500, 0, 50));
                int peak = hot.Max(v => Math.Abs((int)v));
                Check(peak <= (int)(Math.Pow(10, VoiceProcessor.CeilingDb / 20) * 32768) + 2, $"processing: the limiter keeps a full-scale tone at {20 * Math.Log10(peak / 32768.0):F1} dBFS");

                VoiceProcessor gate = new VoiceProcessor(new VoiceProcessing(false, false, true, false));
                double hissOut = LevelDb(Run(gate, Tone(1500, -60, 100)), 8000);
                double talkOut = LevelDb(Run(gate, Tone(1500, -25, 100)), 8000) - LevelDb(Tone(1500, -25, 100), 8000);
                Check(hissOut < -60 - 18 && Math.Abs(talkOut) < 1, $"processing: the noise gate turns background down ({hissOut:F0} dBFS) and lets speech through ({talkOut:+0.0;-0.0} dB)");

                short[] raw = Tone(700, -30, 20);
                short[] same = Run(new VoiceProcessor(VoiceProcessing.Off), raw);
                Check(same.SequenceEqual(raw), "processing: with every stage off, audio passes through untouched");

                // the console uses it on the way to the transmitter, as the settings say
                Tui pc = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
                pc.ReloadAudioProcessing();
                bool defaults = pc.MicProcessing == VoiceProcessing.MicDefault && !pc.RxLeveling;
                pc.ProcessingOff = true;
                pc.ReloadAudioProcessing();
                Check(defaults && !pc.MicProcessing.Any && pc.Channels.All(ch => ch.RxLevel == null),
                      "processing: the console starts with the microphone defaults, and --no-processing turns it all off");

                string wavIn = Path.Combine(Path.GetTempPath(), $"sidetone-proc-{Environment.ProcessId}.wav");
                try
                {
                    using (WavSink w = new WavSink(wavIn))
                        w.Write(Tone(440, -12, 10));
                    short[] readBack = WavFile.ReadVoice(wavIn);
                    Check(readBack.Length == 1600 && readBack.SequenceEqual(Tone(440, -12, 10)) && WavFile.Describe(readBack).Contains("speech -15"),
                          "processing: an 8 kHz WAV reads back exactly, and its levels are described");
                }
                finally
                {
                    try { File.Delete(wavIn); } catch (Exception) { }
                }
            }

            // instant recall, recording and page notifications
            {
                const uint consoleRid = 9000001, radio = 1234;
                Tui rt = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
                CliPeer headers = new CliPeer(sys, new KeyStore(), verbose: false);
                ConsoleChannel calling = rt.Channels.First(ch => ch.IsP25 && !ch.Encrypted);
                string recDir = Path.Combine(Path.GetTempPath(), $"sidetone-rec-{Environment.ProcessId}");
                rt.RecordingsFolder = recDir;
                rt.recordInline = true;
                List<short[]> played = new List<short[]>();
                rt.replaySinkForTest = () => new ListSink(played);

                void Call(int frames, int seed)
                {
                    CaptureSink air = new CaptureSink(headers);
                    P25Transmitter talker = new P25Transmitter(air, new KeyStore(), radio, calling.Tg, P25Defines.P25_ALGO_UNENCRYPT, 0);
                    talker.Start(out _);
                    for (int i = 0; i < frames; i++) talker.PushAudio(VoicedFrame(i + seed));
                    uint stream = talker.StreamId;
                    talker.Stop();
                    foreach (var v in air.Voice)
                        calling.Rx.Handle(calling.Tg, radio, (P25DUID)v.Frame[22], v.Stream, v.Frame);
                    calling.Rx.Handle(calling.Tg, radio, P25DUID.TDU, stream, new byte[24]);
                }

                try
                {
                    rt.ReplayLast(calling);
                    Check(rt.Replaying == null && rt.SnapshotLog(1)[0].Contains("no calls to replay"), "recall: nothing to replay on a channel that isn't listening");

                    rt.Settings.RecordingFormat = DispatchConsole.FormatWav;   // WAV first: its size is exact
                    rt.SetRecord(calling, true);
                    Call(18, 0);
                    List<RecalledCall> kept = rt.RecalledCalls(calling);
                    Check(calling.Selected && kept.Count == 1 && kept[0].Length == TimeSpan.FromMilliseconds(360) && !kept[0].Tx && kept[0].Crypt == "clear",
                          "recall: a call heard on a channel is kept (recording started listening)");

                    string wav = rt.lastRecording;
                    bool named = wav != null && Path.GetFileName(wav).Contains("_TG" + calling.Tg + "_RX_") && wav.StartsWith(recDir) &&
                                 Path.GetFileName(Path.GetDirectoryName(wav)) == kept[0].Started.ToString("yyyy-MM-dd");
                    long size = wav != null && File.Exists(wav) ? new FileInfo(wav).Length : 0;
                    UnixFileMode mode = wav != null && File.Exists(wav) ? File.GetUnixFileMode(wav) : 0;
                    Check(rt.RecordingsWritten == 1 && named && size == 44 + 18 * 160 * 2 && (mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                          $"recording: the call is saved as a WAV named for its time, talkgroup and talker, readable only by the user ({size} bytes)");


                    rt.ReplayLast(calling);
                    Check(played.Count == 18 && rt.Replaying == null, "recall: the last call plays back in full");

                    // the default: Opus, an eighth of the size, and the same call when decoded
                    rt.Settings.RecordingFormat = null;
                    const int opusFrames = 252;     // 5 s, in whole LDUs (a part LDU at the end isn't sent)
                    List<short[]> spokenPcm = new List<short[]>();
                    {
                        MBEEncoder enc = new MBEEncoder(MBE_MODE.IMBE_88BIT);
                        MBEDecoder dec = new MBEDecoder(MBE_MODE.IMBE_88BIT);
                        for (int i = 0; i < opusFrames; i++)
                        {
                            byte[] cw = new byte[P25VoiceReceiver.ImbeLength];
                            enc.encode(VoicedFrame(i + 500), cw);
                            short[] spokenFrame = new short[P25VoiceReceiver.FrameSamples];
                            dec.decode(cw, spokenFrame);
                            spokenPcm.Add(spokenFrame);
                        }
                    }
                    Call(opusFrames, 500);
                    string opus = rt.lastRecording;
                    byte[] opusBytes = opus != null && File.Exists(opus) ? File.ReadAllBytes(opus) : Array.Empty<byte>();
                    string head = System.Text.Encoding.ASCII.GetString(opusBytes, 0, Math.Min(200, opusBytes.Length));
                    long wavEquivalent = 44 + opusFrames * 160 * 2;
                    List<short> decoded = new List<short>();
                    if (opusBytes.Length > 0)
                    {
                        Concentus.OpusCodecFactory.AttemptToUseNativeLibrary = false;
                        Concentus.IOpusDecoder od = Concentus.OpusCodecFactory.CreateDecoder(8000, 1);
                        Concentus.Oggfile.OpusOggReadStream oggIn = new Concentus.Oggfile.OpusOggReadStream(od, new MemoryStream(opusBytes));
                        while (oggIn.HasNextPacket)
                        {
                            short[] packet = oggIn.DecodeNextPacket();
                            if (packet != null)
                                decoded.AddRange(packet);
                        }
                    }
                    // the Ogg pages as a strict player (ffmpeg) wants them: valid checksums, pre-skip set, no empty
                    // last page, the last page ends the stream at the exact end
                    bool pagesOk = false;
                    string pagesSaid = "no file";
                    if (opusBytes.Length > 0)
                    {
                        var pages = OpusFileSink.Pages(opusBytes);
                        bool crcs = pages.All(pg =>
                        {
                            byte[] copy = opusBytes.AsSpan(pg.Offset, pg.Length).ToArray();
                            uint stored = BitConverter.ToUInt32(copy, 22);
                            Array.Clear(copy, 22, 4);
                            return OpusFileSink.OggCrc(copy, 0, copy.Length) == stored;
                        });
                        var lastPage = pages[^1];
                        int preSkip = opusBytes[pages[0].Offset + pages[0].HeaderLength + 10] | (opusBytes[pages[0].Offset + pages[0].HeaderLength + 11] << 8);
                        long endGranule = BitConverter.ToInt64(opusBytes, lastPage.Offset + 6);
                        bool onlyLastEnds = pages.Take(pages.Count - 1).All(pg => (opusBytes[pg.Offset + 5] & 0x04) == 0) && (opusBytes[lastPage.Offset + 5] & 0x04) != 0;
                        pagesOk = crcs && lastPage.Offset + lastPage.Length == opusBytes.Length && lastPage.BodyLength > 0 && onlyLastEnds &&
                                  preSkip > 0 && endGranule == preSkip + (long)opusFrames * 160 * 6;
                        pagesSaid = $"{pages.Count} pages, checksums {(crcs ? "ok" : "bad")}, pre-skip {preSkip}, end {endGranule}";
                    }
                    Check(pagesOk, $"recording: the Opus file's pages are well formed, with pre-skip and an exact end ({pagesSaid})");
                    double Rms(IEnumerable<short> xs) { double sum = 0; int n = 0; foreach (short x in xs) { sum += (double)x * x; n++; } return n == 0 ? 0 : Math.Sqrt(sum / n); }
                    double spokenRms = Rms(spokenPcm.SelectMany(f => f)), decodedRms = Rms(decoded);
                    int expectSamples = opusFrames * 160;
                    Check(opus != null && opus.EndsWith(".opus") && head.StartsWith("OggS") && head.Contains("OpusHead") && opusBytes.Length < wavEquivalent / 5 &&
                          Math.Abs(decoded.Count - expectSamples) <= 480 && decodedRms > spokenRms * 0.6 && decodedRms < spokenRms * 1.5 &&
                          (File.GetUnixFileMode(opus) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                          $"recording: by default the call is saved as Opus, about an eighth of the WAV's size, and decodes to the same length and level " +
                          $"({opusBytes.Length} bytes vs {wavEquivalent}; {decoded.Count} samples vs {expectSamples}; level {decodedRms:F0} vs {spokenRms:F0})");

                    for (int n = 0; n < 11; n++)
                        Call(9, 100 + n);
                    kept = rt.RecalledCalls(calling);
                    Check(kept.Count == DispatchConsole.RecallPerChannel && kept.All(k => k.Length == TimeSpan.FromMilliseconds(180)),
                          $"recall: only the last {DispatchConsole.RecallPerChannel} calls per channel are kept");

                    played.Clear();
                    rt.ReplayOlder(calling);
                    rt.ReplayOlder(calling);
                    Check(played.Count == 18, "recall: L in the TUI plays the kept calls");

                    rt.SetRecord(calling, false);
                    int written = rt.RecordingsWritten;
                    Call(9, 500);
                    Check(rt.RecordingsWritten == written && rt.RecalledCalls(calling).Count == DispatchConsole.RecallPerChannel,
                          "recording: off means calls are still kept for recall, but not saved");

                    // the console's own transmission is kept as it went out
                    CaptureSink mainOut = new CaptureSink(headers);
                    P25Transmitter main = new P25Transmitter(mainOut, rt.Keys, consoleRid, calling.Tg, P25Defines.P25_ALGO_UNENCRYPT, 0);
                    main.Start(out _);
                    rt.RecallTxStartedForTest(calling, main);
                    for (int i = 0; i < 9; i++) main.PushAudio(VoicedFrame(i));
                    main.Stop();
                    rt.RecallTxStoppedForTest(calling);
                    RecalledCall own = rt.RecalledCalls(calling)[0];
                    Check(own.Tx && own.Length == TimeSpan.FromMilliseconds(180), "recall: the console's own transmissions are kept too");

                    // a page addressed to the console shows one desktop notification, not one per repeat
                    List<string> shown = new List<string>();
                    rt.notifyForTest = (title, body) => shown.Add(title);
                    rt.consoleRidForTest = consoleRid;
                    byte[] pageTsbk = CliPeer.BuildCallAlert(consoleRid, radio);
                    rt.OnP25Control(P25Defines.TSBK_IOSP_CALL_ALRT, radio, consoleRid, pageTsbk);
                    rt.OnP25Control(P25Defines.TSBK_IOSP_CALL_ALRT, radio, consoleRid, pageTsbk);
                    rt.OnP25Control(P25Defines.TSBK_IOSP_CALL_ALRT, radio, 4321, pageTsbk);
                    rt.Notifications = false;
                    rt.OnP25Control(P25Defines.TSBK_IOSP_CALL_ALRT, 5555, consoleRid, pageTsbk);
                    Check(shown.Count == 1 && shown[0].Contains("Paged by"), $"notifications: a page to this console shows one notification; repeats, other radios' pages and 'off' don't ({shown.Count})");
                }
                finally
                {
                    try { Directory.Delete(recDir, true); } catch (Exception) { }
                }
            }

            // local key file (a KFDtool .ekc container), and selectable encryption
            string keyDir = Path.Combine(Path.GetTempPath(), $"sidetone-keys-{Environment.ProcessId}");
            try
            {
                Directory.CreateDirectory(keyDir);
                Tui keyed = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
                ConsoleChannel secure = keyed.Channels.First(ch => ch.Encrypted);
                ConsoleChannel clearChan = keyed.Channels.First(ch => ch.IsP25 && !ch.Encrypted);
                int keyBytes = secure.KeyRequestAlg == 0x84 ? 32 : secure.KeyRequestAlg == 0x81 ? 8 : 5;
                byte[] material = Enumerable.Range(1, keyBytes).Select(i => (byte)(i * 7)).ToArray();
                string ekc = Path.Combine(keyDir, "test.ekc");
                WriteEkc(ekc, "correct horse", new[]
                {
                    (Tek: true, Alg: (int)secure.KeyRequestAlg, Id: (int)secure.KeyId, Hex: Convert.ToHexString(material)),
                    (Tek: true, Alg: 0x84, Id: 0x7001, Hex: new string('A', 64)),
                    (Tek: false, Alg: 0x84, Id: 0x7002, Hex: new string('B', 64))       // a key-wrapping key: left out
                });

                LocalKeyFile wrongOne = LocalKeyFile.Open(ekc, "wrong", out string wrongError, out bool wrongPassword);
                Check(wrongOne == null && wrongPassword && wrongError.Contains("password"), "key file: a wrong password is refused, and says so");
                LocalKeyFile missing = LocalKeyFile.Open(Path.Combine(keyDir, "nope.ekc"), "x", out string missingError, out bool missingWrong);
                File.WriteAllText(Path.Combine(keyDir, "keys.txt"), "not a container");
                LocalKeyFile notEkc = LocalKeyFile.Open(Path.Combine(keyDir, "keys.txt"), "x", out string notEkcError, out bool notEkcWrong);
                Check(missing == null && !missingWrong && missingError.Contains("found") && notEkc == null && !notEkcWrong && notEkcError.Contains(".ekc"),
                      "key file: a missing file and a file that isn't an .ekc container are plain errors, not password errors");

                bool before = keyed.HasKey(secure);
                keyed.KeyFilePath = ekc;
                string loadError = keyed.LoadKeyFile("correct horse", out _);
                Check(!before && loadError == null && keyed.KeyFileLoaded && keyed.Keys.LocalCount == 2 && keyed.HasKey(secure) && keyed.KeyIsLocal(secure) &&
                      keyed.Keys.TryGet(secure.KeyRequestAlg, secure.KeyId, out byte[] got) && got.SequenceEqual(material),
                      $"key file: unlocks with its password, traffic keys only, and the channel's key comes from it ({loadError})");
                keyed.Keys.Store(secure.KeyRequestAlg, secure.KeyId, new byte[keyBytes]);
                Check(keyed.Keys.TryGet(secure.KeyRequestAlg, secure.KeyId, out byte[] still) && still.SequenceEqual(material),
                      "key file: its key wins over one the FNE sends for the same key ID");
                string keyLog = string.Join("\n", keyed.SnapshotLog(20));
                Check(keyLog.Contains("keys: 2 from test.ekc") && !keyLog.Contains("correct horse") && !keyLog.Contains(Convert.ToHexString(material)),
                      "key file: the log says how many keys were loaded, and never the password or a key");
                Check(LocalKeyFile.Resolve(cp, "/tmp/x.ekc") == "/tmp/x.ekc" && LocalKeyFile.Resolve(cp, null) == null,
                      "key file: an option or setting names it; a codeplug without one means none");

                // selectable encryption
                keyed.ToggleTxSecure(secure);
                bool fixedStays = !secure.TxClear && secure.TxSecure;
                keyed.ToggleTxSecure(clearChan);
                secure.Selectable = true;
                keyed.ToggleTxSecure(secure);
                bool clear = secure.TxClear && !secure.TxSecure && secure.ActiveTxAlg == fnecore.P25.P25Defines.P25_ALGO_UNENCRYPT && secure.ActiveKeyId == 0;
                keyed.ToggleTxSecure(secure);
                Check(fixedStays && !clearChan.TxClear && clear && secure.TxSecure && secure.ActiveTxAlg == secure.TxAlg && secure.ActiveKeyId == secure.KeyId,
                      "selectable encryption: only a channel the codeplug marks can be switched to clear, and back");
                secure.Selectable = false;
            }
            finally
            {
                try { Directory.Delete(keyDir, true); } catch (Exception) { }
            }

            // settings from before the rename are carried over once
            string configRoot = Path.Combine(Path.GetTempPath(), $"sidetone-config-{Environment.ProcessId}");
            try
            {
                Directory.CreateDirectory(Path.Combine(configRoot, AppSettings.LegacyFolderName, "RadioAliases"));
                File.WriteAllText(Path.Combine(configRoot, AppSettings.LegacyFolderName, "settings.json"), "{ \"Theme\": \"dark\" }");
                File.WriteAllText(Path.Combine(configRoot, AppSettings.LegacyFolderName, "RadioAliases", "cache.json"), "{}");
                bool copied = AppSettings.AdoptLegacyFolder(configRoot);
                string moved = Path.Combine(configRoot, AppSettings.FolderName, "settings.json");
                bool arrived = File.Exists(moved) && AppSettings.Load(moved, out _).Theme == "dark" &&
                               File.Exists(Path.Combine(configRoot, AppSettings.FolderName, "RadioAliases", "cache.json"));
                File.WriteAllText(moved, "{ \"Theme\": \"light\" }");
                bool again = AppSettings.AdoptLegacyFolder(configRoot);
                Check(copied && arrived && !again && AppSettings.Load(moved, out _).Theme == "light" &&
                      File.Exists(Path.Combine(configRoot, AppSettings.LegacyFolderName, "settings.json")),
                      "settings: the old dvmcli folder is copied to the new name once, and never over newer settings");
                Check(!AppSettings.AdoptLegacyFolder(Path.Combine(configRoot, "nothing-here")), "settings: nothing to carry over is fine");
            }
            finally
            {
                try { Directory.Delete(configRoot, true); } catch (Exception) { }
            }

            // remembered settings
            string settingsFile = Path.Combine(Path.GetTempPath(), $"sidetone-settings-{Environment.ProcessId}.json");
            try
            {
                Tui first = new Tui(cp, sys, TimeSpan.FromSeconds(120), null, settingsPath: settingsFile) { NoAudio = true };
                first.Render(120, 40);
                first.HandleInput(new InputEvent(InputKind.Key, InputKey.Right));      // focus Secure
                first.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));      // listen
                first.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'm'));  // mute it
                first.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, '+'));
                first.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 't'));  // talk permit off
                first.SaveSettings();
                string json = File.ReadAllText(settingsFile);

                Tui second = new Tui(cp, sys, TimeSpan.FromSeconds(120), null, settingsPath: settingsFile) { NoAudio = true };
                second.RestoreSettings();
                Check(second.Cursor == 1 && second.IsSelected(1) && !second.IsSelected(0) && !second.TalkPermit && json.Contains("1.25") &&
                      !json.Contains("password", StringComparison.OrdinalIgnoreCase),
                      "settings remembered: listening channels, focus, volume, mute, talk permit (no passwords in the file)");
            }
            finally
            {
                try { File.Delete(settingsFile); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Captures what a transmitter would send to the FNE; headers come from a real (offline) CliPeer.
        /// </summary>
        private sealed class CaptureSink : IP25TrafficSink
        {
            private readonly CliPeer headers;
            public readonly List<(byte[] Frame, ushort Seq, uint Stream)> Voice = new();
            public readonly List<(uint Src, uint Dst, uint Stream, bool Grant)> Tdus = new();

            public CaptureSink(CliPeer headers) => this.headers = headers;
            public byte[] CreateP25Header(P25DUID duid, fnecore.RemoteCallData callData, fnecore.CryptoParams cryptoParams) =>
                headers.CreateP25Header(duid, callData, cryptoParams);
            public int VoiceCount, TduCount;      // safe to read from another thread
            public void SendP25Voice(byte[] frame, ushort packetSequence, uint streamId)
            {
                Voice.Add(((byte[])frame.Clone(), packetSequence, streamId));
                Interlocked.Increment(ref VoiceCount);
            }
            public readonly List<bool> TduEncrypted = new List<bool>();
            public void SendP25Tdu(uint srcId, uint dstId, uint streamId, bool grantDemand, bool encrypted = false)
            {
                if (grantDemand)
                    TduEncrypted.Add(encrypted);
                int count = grantDemand ? 1 : 4;   // as CliPeer does
                for (int i = 0; i < count; i++)
                    Tdus.Add((srcId, dstId, streamId, grantDemand));
                Interlocked.Add(ref TduCount, count);
            }
        }

        private static void TransmitTests(int frames)
        {
            const uint rid = 9000001, tg = 504, secureTg = 500;

            // expected codewords: the encoder is deterministic, so a fresh one gives the transmitter's output
            MBEEncoder reference = new MBEEncoder(MBE_MODE.IMBE_88BIT);
            List<short[]> pcm = Enumerable.Range(0, frames).Select(VoicedFrame).ToList();
            List<byte[]> expected = pcm.Select(p => { byte[] c = new byte[P25VoiceReceiver.ImbeLength]; reference.encode(p, c); return c; }).ToList();

            Codeplug.System offline = new Codeplug.System { Name = "Test", Identity = "TEST", Address = "127.0.0.1", Port = 62031, PeerId = rid, Rid = rid.ToString(), Password = "x" };
            CliPeer headerPeer = new CliPeer(offline, new KeyStore(), verbose: false);
            Check(headerPeer.ConsoleRid == rid, "peer reads the console RID from the codeplug");

            // clear call
            CaptureSink clear = new CaptureSink(headerPeer);
            P25Transmitter tx = new P25Transmitter(clear, new KeyStore(), rid, tg, P25Defines.P25_ALGO_UNENCRYPT, 0);
            Check(tx.Start(out string err1) && err1 == null, "clear transmit starts");
            foreach (short[] p in pcm) tx.PushAudio(p);
            for (int i = 0; i < 5; i++) tx.PushAudio(pcm[i]);   // partial superframe at the end, dropped
            uint stream = tx.StreamId;
            tx.Stop();

            Check(clear.Tdus.Count == 5 && clear.Tdus[0].Grant && clear.Tdus.Skip(1).All(t => !t.Grant) &&
                  clear.Tdus.All(t => t.Src == rid && t.Dst == tg && t.Stream == stream && stream != 0),
                  "call framing: one grant demand, then four end TDUs, all on the call's stream ID");
            Check(clear.Voice.Count == frames / 9 && clear.Voice.All(v => v.Stream == stream) &&
                  clear.Voice.Select(v => (int)v.Seq).SequenceEqual(Enumerable.Range(0, frames / 9)),
                  $"{frames / 9} LDUs sent with RTP sequence 0..{frames / 9 - 1}; partial superframe dropped");

            byte[] l1 = clear.Voice[0].Frame;
            int lcRecord = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[2];
            int tgRecord = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[3];
            int srcRecord = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[4];
            Check(l1[22] == (byte)P25DUID.LDU1 && clear.Voice[1].Frame[22] == (byte)P25DUID.LDU2 && l1[23] == 178 && l1.Length == P25FrameBuilder.FrameSize,
                  "LDU1/LDU2 headers carry the DUID and payload length");
            Check(l1[lcRecord + 1] == P25Defines.LC_GROUP && l1[lcRecord + 3] == 0x04 &&
                  ((l1[tgRecord + 1] << 16) | (l1[tgRecord + 2] << 8) | l1[tgRecord + 3]) == tg &&
                  ((l1[srcRecord + 1] << 16) | (l1[srcRecord + 2] << 8) | l1[srcRecord + 3]) == rid,
                  "link control: group call, priority 4, talkgroup and source in the voice records");
            Check(l1[P25VoiceReceiver.HduFlagOffset] == P25Defines.P25_FT_HDU_VALID && l1[P25VoiceReceiver.HduAlgOffset] == P25Defines.P25_ALGO_UNENCRYPT,
                  "clear call marked clear in the HDU parameters");

            List<byte[]> fed = RoundTrip(clear, tg, rid, new KeyStore(), out long muted);
            Check(muted == 0 && SameCodewords(fed, expected), "clear transmit decodes back to exactly the encoded codewords");

            // encrypted call (AES-256)
            KeyStore keys = new KeyStore();
            keys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey);
            CaptureSink secure = new CaptureSink(headerPeer);
            P25Transmitter stx = new P25Transmitter(secure, keys, rid, secureTg, P25Defines.P25_ALGO_AES, TestKeyId);
            Check(stx.Start(out string err2) && err2 == null && stx.Encrypted, "AES transmit starts with the key held");
            foreach (short[] p in pcm) stx.PushAudio(p);
            stx.Stop();

            byte[] s1 = secure.Voice[0].Frame;
            Check(s1[P25VoiceReceiver.HduFlagOffset] == P25Defines.P25_FT_HDU_VALID && s1[P25VoiceReceiver.HduAlgOffset] == P25Defines.P25_ALGO_AES &&
                  ((s1[P25VoiceReceiver.HduKeyIdOffset] << 8) | s1[P25VoiceReceiver.HduKeyIdOffset + 1]) == TestKeyId &&
                  s1.Skip(P25VoiceReceiver.HduMiOffset).Take(P25Defines.P25_MI_LENGTH).Any(b => b != 0) &&
                  s1[lcRecord + 3] == 0x44,
                  "AES call: HDU carries algorithm, key ID and a random MI; service options marked protected");
            Check(P25VoiceReceiver.TryExtractCodewords(s1, P25VoiceReceiver.Ldu1FirstRecord, out byte[][] wire) && !wire[0].SequenceEqual(expected[0]),
                  "AES call is encrypted on the wire");

            List<byte[]> secureFed = RoundTrip(secure, secureTg, rid, keys, out long secureMuted);
            Check(secureMuted == 0 && SameCodewords(secureFed, expected), "AES transmit decrypts back to exactly the encoded codewords");

            // refusals
            CaptureSink none = new CaptureSink(headerPeer);
            P25Transmitter nokey = new P25Transmitter(none, new KeyStore(), rid, secureTg, P25Defines.P25_ALGO_AES, TestKeyId);
            Check(!nokey.Start(out string err3) && err3.Contains("not transmitting in the clear") && none.Tdus.Count == 0,
                  "secure channel without its key refuses to transmit (nothing sent)");
            P25Transmitter norid = new P25Transmitter(none, new KeyStore(), 0, tg, P25Defines.P25_ALGO_UNENCRYPT, 0);
            Check(!norid.Start(out _) && none.Tdus.Count == 0, "transmit without a console RID is refused");
        }

        /// <summary>
        /// Plays captured transmit frames into a receiver and returns the codewords it would decode.
        /// </summary>
        private static void ToneTests()
        {
            const uint rid = 9000001;

            // plans match the Windows console's built-in tones
            TonePlan a1 = TonePlan.Alert(1), a2 = TonePlan.Alert(2), a3 = TonePlan.Alert(3), mk = TonePlan.Marker();
            int lead = TonePlan.LeadInMs / 20;
            Check(a1.Frames.Length % 9 == 0 && a1.Frames.Take(lead).All(f => f == null) &&
                  a1.Frames.Skip(lead).Take(250).All(f => f == 1000) && a1.Frames.Skip(lead + 250).All(f => f == null),
                  $"alert 1: 0.75 s silence, 5 s of 1000 Hz, 0.75 s silence ({a1.Frames.Length} frames, whole LDUs)");
            ushort?[] body2 = a2.Frames.Skip(lead).Take(250).ToArray();
            Check(body2.Take(12).All(f => f == 1500) && body2.Skip(13).Take(12).All(f => f == 800) && body2.All(f => f == 1500 || f == 800),
                  "alert 2 alternates 1500 / 800 Hz every 250 ms");
            Check(a3.Frames.Skip(lead).Take(250).Count(f => f == 1000) is >= 120 and <= 130 && a3.Frames.Skip(lead).Take(250).Contains(null),
                  "alert 3 pulses 1000 Hz on and off");
            Check(TonePlan.Alert(4).Frames.Contains((ushort)1000) && TonePlan.Alert(4).Frames.Contains((ushort)1500) &&
                  TonePlan.Alert(5).Frames.Contains((ushort)940) && TonePlan.Alert(5).Frames.Contains((ushort)1640),
                  "alerts 4 and 5 use their own frequency pairs");
            Check(mk.Frames.Count(f => f == 650) == 25 && mk.Frames.Length % 9 == 0, "channel marker is 0.5 s of 650 Hz");
            ushort?[] body6 = TonePlan.Alert(6).Frames.Skip(lead).Take(250).ToArray();
            Check(body6.Take(6).All(f => f == 460) && body6.Skip(7).Take(5).All(f => f == 860) && body6.All(f => f == 460 || f == 860),
                  "alert 6 warbles 460 / 860 Hz every 125 ms");
            short[] tpt = Tui.TalkPermitPcm();
            Check(tpt.Length == 400 && tpt[0] == 0 && tpt.Max() > 6000 && tpt.Max() < 6600, "talk permit tone: 50 ms of 1200 Hz at 20 % with fades");
            TonePlan pg = TonePlan.Page(1153.4, 1185.2);
            Check(pg.Frames.Take(lead).All(f => f == null) && pg.Frames.Skip(lead).Take(50).All(f => f == 1153) &&
                  pg.Frames.Skip(lead + 50).Take(150).All(f => f == 1185) && pg.Frames.Skip(lead + 200).All(f => f == null) && pg.Frames.Length % 9 == 0,
                  "QuickCall II page: tone A 1 s, tone B 3 s, with lead-in and tail");
            bool rejected = false;
            try { TonePlan.Page(100, 1000); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "page tones outside 250-3000 Hz are refused");

            // send alert 1 on two talkgroups at once: one clear, one AES
            Codeplug.System offline = new Codeplug.System { Name = "Test", Identity = "TEST", Address = "127.0.0.1", Port = 62031, PeerId = rid, Rid = rid.ToString(), Password = "x" };
            CliPeer headerPeer = new CliPeer(offline, new KeyStore(), verbose: false);
            KeyStore keys = new KeyStore();
            keys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey);
            CaptureSink clearSink = new CaptureSink(headerPeer), secureSink = new CaptureSink(headerPeer);
            P25Transmitter clearTx = new P25Transmitter(clearSink, keys, rid, 504, P25Defines.P25_ALGO_UNENCRYPT, 0);
            P25Transmitter secureTx = new P25Transmitter(secureSink, keys, rid, 500, P25Defines.P25_ALGO_AES, TestKeyId);
            Check(clearTx.Start(out _) && secureTx.Start(out _), "tone transmitters start on both talkgroups");
            int monitored = 0;
            ToneJob job = new ToneJob(a1, new List<(string, P25Transmitter)> { ("Calling", clearTx), ("Secure", secureTx) }, _ => monitored++);
            job.Start(realTime: false);

            int ldus = a1.Frames.Length / 9;
            Check(clearSink.Voice.Count == ldus && secureSink.Voice.Count == ldus && clearSink.Tdus.Count == 5 && secureSink.Tdus.Count == 5 &&
                  clearSink.Voice[0].Stream != secureSink.Voice[0].Stream && monitored == a1.Frames.Length,
                  $"alert sent as two separate calls ({ldus} LDUs each, grant and end TDUs, local monitor fed)");

            byte[] toneWord = new byte[P25VoiceReceiver.ImbeLength];
            MBEToneGenerator.IMBEEncodeSingleTone(1000, toneWord);
            List<byte[]> clearFed = RoundTrip(clearSink, 504, rid, keys, out long m1);
            List<byte[]> secureFed = RoundTrip(secureSink, 500, rid, keys, out long m2);
            Check(m1 == 0 && clearFed.Count == a1.Frames.Length && clearFed.Skip(lead).Take(250).All(c => c.SequenceEqual(toneWord)),
                  "clear alert decodes to the 1000 Hz IMBE tone codeword");
            Check(m2 == 0 && secureFed.Count == a1.Frames.Length && secureFed.Skip(lead).Take(250).All(c => c.SequenceEqual(toneWord)) &&
                  !secureSink.Voice[3].Frame.SequenceEqual(clearSink.Voice[3].Frame),
                  "AES alert is encrypted on the wire and decrypts to the same tone");
        }

        private static List<byte[]> RoundTrip(CaptureSink sent, uint tg, uint src, KeyStore keys, out long muted)
        {
            List<byte[]> fed = new List<byte[]>();
            using P25VoiceReceiver rx = new P25VoiceReceiver(tg, keys);
            rx.Codeword += fed.Add;
            foreach (var (frame, _, stream) in sent.Voice)
                rx.Handle(tg, src, (P25DUID)frame[22], stream, frame);
            rx.Handle(tg, src, P25DUID.TDU, sent.Voice[0].Stream, new byte[24]);
            muted = rx.FramesMuted;
            return fed;
        }

        private static bool SameCodewords(List<byte[]> got, List<byte[]> expected) =>
            got.Count == expected.Count && got.Zip(expected).All(p => p.First.SequenceEqual(p.Second));

        private static double Rms(List<short> samples) =>
            samples.Count == 0 ? 0 : Math.Sqrt(samples.Sum(v => (double)v * v) / samples.Count);

        /// <summary>
        /// Builds the LDU1/LDU2 frames of a call the way a radio would send it: when encrypted, each
        /// superframe is encrypted under the current MI, and its LDU2 carries the next MI (LFSR-cycled).
        /// </summary>
        private static List<(P25DUID, byte[])> BuildCall(List<byte[]> clear, byte alg, ushort kid, byte[] key, bool withHdu)
        {
            bool encrypted = alg != P25Defines.P25_ALGO_UNENCRYPT;
            byte[] mi = (byte[])TestMi.Clone();
            P25Crypto tx = null;
            if (encrypted)
            {
                tx = new P25Crypto();
                tx.SetKey(kid, alg, key);
                tx.Prepare(alg, kid, mi);
            }

            List<(P25DUID, byte[])> frames = new List<(P25DUID, byte[])>();
            for (int sf = 0; sf + 18 <= clear.Count; sf += 18)
            {
                List<byte[]> l1 = clear.Skip(sf).Take(9).Select(c => (byte[])c.Clone()).ToList();
                List<byte[]> l2 = clear.Skip(sf + 9).Take(9).Select(c => (byte[])c.Clone()).ToList();
                if (encrypted)
                {
                    foreach (byte[] c in l1) tx.Process(c, P25DUID.LDU1);
                    foreach (byte[] c in l2) tx.Process(c, P25DUID.LDU2);
                }

                byte[] hduMi = (byte[])mi.Clone();
                byte[] nextMi = (byte[])mi.Clone();
                if (encrypted)
                {
                    P25Crypto.CycleP25Lfsr(nextMi);
                    tx.Prepare(alg, kid, nextMi);
                }

                byte[] f1 = BuildLduFrame(P25VoiceReceiver.Ldu1FirstRecord, l1, P25VoiceReceiver.HduFrameLength);
                if (withHdu && sf == 0)
                {
                    f1[P25VoiceReceiver.HduFlagOffset] = P25Defines.P25_FT_HDU_VALID;
                    f1[P25VoiceReceiver.HduAlgOffset] = alg;
                    f1[P25VoiceReceiver.HduKeyIdOffset] = (byte)(kid >> 8);
                    f1[P25VoiceReceiver.HduKeyIdOffset + 1] = (byte)kid;
                    Array.Copy(hduMi, 0, f1, P25VoiceReceiver.HduMiOffset, P25Defines.P25_MI_LENGTH);
                }

                byte[] f2 = BuildLduFrame(P25VoiceReceiver.Ldu2FirstRecord, l2, 0, alg, kid, encrypted ? nextMi : new byte[P25Defines.P25_MI_LENGTH]);
                frames.Add((P25DUID.LDU1, f1));
                frames.Add((P25DUID.LDU2, f2));
                mi = nextMi;
            }
            return frames;
        }

        /// <summary>
        /// A voiced, speech-like test frame: 140 Hz fundamental with falling harmonics and a slow pitch glide.
        /// </summary>
        private static short[] VoicedFrame(int frame)
        {
            short[] pcm = new short[P25VoiceReceiver.FrameSamples];
            for (int i = 0; i < pcm.Length; i++)
            {
                double t = (frame * pcm.Length + i) / 8000.0;
                double f0 = 140 + 20 * Math.Sin(2 * Math.PI * 0.7 * t);
                double v = 0;
                for (int h = 1; h <= 20; h++)
                    v += Math.Sin(2 * Math.PI * f0 * h * t) / h;
                pcm[i] = (short)Math.Clamp(3000 * v, short.MinValue, short.MaxValue);
            }
            return pcm;
        }

        /// <summary>
        /// Builds a network frame with nine DFSI voice records, laid out as the console's receiver expects.
        /// For LDU2 frames, optionally fills the encryption sync (MI in records 0x6D-0x6F, algorithm and key ID in 0x70).
        /// </summary>
        private static byte[] BuildLduFrame(byte firstRecord, List<byte[]> nine, int length = 0, byte alg = 0, ushort kid = 0, byte[] mi = null)
        {
            byte[] frame = new byte[Math.Max(length, P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.MinimumPayloadLength + 4)];
            for (int n = 0; n < 9; n++)
            {
                int record = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[n];
                frame[record] = (byte)(firstRecord + n);
                Buffer.BlockCopy(nine[n], 0, frame, record + P25VoiceReceiver.ImbeOffsetInRecord[n], P25VoiceReceiver.ImbeLength);
            }

            if (firstRecord == P25VoiceReceiver.Ldu2FirstRecord && alg != 0)
            {
                for (int i = 0; i < P25VoiceReceiver.MiRecordIndexes.Length; i++)
                {
                    int record = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[P25VoiceReceiver.MiRecordIndexes[i]];
                    Buffer.BlockCopy(mi, i * 3, frame, record + 1, 3);
                }
                int algRecord = P25VoiceReceiver.PayloadOffset + P25VoiceReceiver.RecordOffsets[P25VoiceReceiver.AlgRecordIndex];
                frame[algRecord + 1] = alg;
                frame[algRecord + 2] = (byte)(kid >> 8);
                frame[algRecord + 3] = (byte)kid;
            }
            return frame;
        }

        private const string SampleCodeplug = @"
systems:
  - name: ""Test FNE""
    identity: ""TEST CLI""
    address: ""127.0.0.1""
    port: 62031
    peerId: 9000001
    rid: ""9000001""
    password: ""PASSWORD""
    encrypted: false
zones:
  - name: ""Test""
    channels:
      - name: ""Calling""
        system: ""Test FNE""
        tgid: ""504""
        mode: ""p25""
        selectable_encryption: true
      - name: ""Secure""
        system: ""Test FNE""
        tgid: ""500""
        mode: ""p25""
        algo: ""aes""
        keyId: 0x561
";

        private const string ZoneCodeplug = @"
systems:
  - name: ""Test FNE""
    identity: ""TEST CLI""
    address: ""127.0.0.1""
    port: 62031
    peerId: 9000001
    rid: ""9000001""
    password: ""PASSWORD""
  - name: ""Other FNE""
    identity: ""OTHER""
    address: ""127.0.0.1""
    port: 62032
    peerId: 9000002
    rid: ""9000002""
    password: ""PASSWORD""
zones:
  - name: ""Alpha""
    tabColor: ""#E57373""
    tabTextColor: ""#000000""
    channels:
      - { name: ""A1"", system: ""Test FNE"", tgid: ""601"", mode: ""p25"" }
      - { name: ""A2"", system: ""Test FNE"", tgid: ""602"", mode: ""p25"" }
  - name: """"
    channels:
      - { name: ""N1"", system: ""Test FNE"", tgid: ""611"", mode: ""p25"" }
  - name: ""Bravo""
    channels:
      - { name: ""B1"", system: ""Test FNE"", tgid: ""621"", mode: ""p25"" }
      - { name: ""B2"", system: ""Other FNE"", tgid: ""622"", mode: ""p25"" }
  - name: ""alpha""
    channels:
      - { name: ""A3"", system: ""Test FNE"", tgid: ""603"", mode: ""p25"" }
  - name: ""Elsewhere""
    channels:
      - { name: ""E1"", system: ""Other FNE"", tgid: ""631"", mode: ""p25"" }
";

        private static void ZoneTests(string tmp)
        {
            string zonePath = Path.Combine(tmp, "zones.yml");
            File.WriteAllText(zonePath, ZoneCodeplug);
            Codeplug zcp = Program.LoadCodeplug(zonePath);
            Codeplug.System zsys = zcp.Systems[0];
            string zoneRec = Path.Combine(tmp, "zone-recordings");

            Tui zt = new Tui(zcp, zsys, TimeSpan.FromSeconds(120), null) { NoAudio = true, RecordingsFolder = zoneRec };
            List<string> zoneNames = zt.Zones.Select(z => z.Name).ToList();
            ConsoleZone alpha = zt.Zones[0], unnamed = zt.Zones[1], bravo = zt.Zones[2];
            Check(string.Join(",", zoneNames) == "Alpha,Zone 2,Bravo" &&
                  string.Join(",", alpha.Channels.Select(c => c.Cfg.Name)) == "A1,A2,A3" && bravo.Channels.Count == 1,
                  $"zones: one tab per zone, same-named zones share a tab, a nameless one is 'Zone N', other systems' channels and zones left out ({string.Join(",", zoneNames)})");
            Check(alpha.TabColor == "#E57373" && alpha.TabTextColor == "#000000" && bravo.TabColor == null && zt.Channels[0].InZone == alpha,
                  "zones: tab colours come from the codeplug");

            // switching tabs
            int a1 = 0, a2 = 1, n1 = 2, b1 = 3, a3 = 4;      // codeplug order
            zt.SetFocus(a2);
            zt.ShowZone(bravo);
            bool onBravo = zt.CurrentZone == bravo && zt.FocusIndex == b1;
            zt.NextZone(1);
            bool backToA2 = zt.CurrentZone == alpha && zt.FocusIndex == a2;
            zt.NextZone(-1);
            bool wrapped = zt.CurrentZone == bravo;
            zt.ShowZoneNumber(2);
            bool second = zt.CurrentZone == unnamed && zt.FocusIndex == n1;
            zt.ShowZoneNumber(9);
            Check(onBravo && backToA2 && wrapped && second && zt.CurrentZone == unnamed,
                  "zones: switching tabs moves the focus there, each tab remembers its focused card, and the ends wrap round");

            // the TUI: a tab line, cards from the tab on show only, arrows stay on the tab, [ and ] switch
            zt.ShowZone(alpha);
            zt.Render(120, 40);
            string tabLine = zt.Render(120, 40).RowText(Tui.TitleHeight);
            string cardTop = zt.Render(120, 40).RowText(Tui.TitleHeight + 1);
            Check(zt.TabRows == 1 && tabLine.Contains("1 Alpha") && tabLine.Contains("2 Zone 2") && tabLine.Contains("3 Bravo") &&
                  cardTop.Contains("A1") && cardTop.Contains("A3") && !cardTop.Contains("B1"),
                  $"TUI tabs: a line of tabs under the title, and only the tab's own cards ('{tabLine.Trim()}')");
            zt.HandleInput(new InputEvent(InputKind.Key, InputKey.End));
            bool lastOnTab = zt.FocusIndex == a3;
            zt.HandleInput(new InputEvent(InputKind.Key, InputKey.Right));
            bool stayed = zt.FocusIndex == a3;
            zt.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, ']'));
            bool next = zt.CurrentZone == unnamed;
            zt.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, '['));
            Check(lastOnTab && stayed && next && zt.CurrentZone == alpha && zt.FocusIndex == a3,
                  "TUI tabs: End and the arrows move within the tab; ] and [ go to the next and previous tab");
            zt.Render(120, 40);
            int bravoX = tabLine.IndexOf("3 Bravo", StringComparison.Ordinal);
            zt.HandleInput(new InputEvent(InputKind.Click, X: bravoX + 1, Y: Tui.TitleHeight));
            Check(zt.CurrentZone == bravo, "TUI tabs: clicking a tab shows it");
            zt.ShowZone(alpha);

            // hiding a zone switches its channels off
            zt.Select(zt.Channels[a1]);
            zt.SetPatch(zt.Channels[a1], 1);
            zt.SetRecord(zt.Channels[a2], true);
            zt.Select(zt.Channels[b1]);
            bool before = zt.Channels[a1].Selected && zt.Channels[a1].Patch == 1 && zt.Channels[a2].Record;
            string hid = zt.SetZoneHidden(alpha, true);
            Check(before && hid == null && alpha.Hidden && zt.Channels.Where(c => c.InZone == alpha).All(c => !c.Selected && c.Patch == 0 && !c.Record) &&
                  zt.Channels[b1].Selected && zt.CurrentZone == unnamed && zt.VisibleZones.Count == 2,
                  "zones: hiding one stops listening on its channels (and their patch and recording), leaves the others alone, and moves to the next tab");
            zt.Select(zt.Channels[a1]);
            int focusNow = zt.FocusIndex;
            zt.SetFocus(a1);
            zt.ShowZone(alpha);
            Check(!zt.Channels[a1].Selected && zt.SnapshotLog(5).Any(l => l.Contains("zone Alpha is hidden")) && zt.FocusIndex == focusNow && zt.CurrentZone == unnamed,
                  "zones: a hidden zone's channels can't be listened to or focused, and its tab can't be shown");
            zt.OnFrame(new fnecore.P25DataReceivedEvent(1, 3012, 601, fnecore.CallType.GROUP, P25DUID.LDU1, fnecore.FrameType.VOICE, 0, 77, new byte[300]));
            zt.OnFrame(new fnecore.P25DataReceivedEvent(1, 3012, 611, fnecore.CallType.GROUP, P25DUID.LDU1, fnecore.FrameType.VOICE, 0, 78, new byte[300]));
            Check(!zt.Channels[a1].Active && zt.Channels[n1].Active && zt.ZoneActivity(unnamed) == ChannelActivity.Receiving && zt.ZoneActivity(alpha) == ChannelActivity.Idle,
                  "zones: traffic on a hidden zone isn't shown; traffic on a tab marks it");
            zt.Channels[n1].Active = false;

            string onlyOne = zt.SetZoneHidden(unnamed, true) == null ? zt.SetZoneHidden(bravo, true) : "first refused";
            Check(onlyOne != null && onlyOne.Contains("only zone on show") && !bravo.Hidden && unnamed.Hidden,
                  $"zones: the last zone on show can't be hidden ('{onlyOne}')");
            zt.SetZoneHidden(unnamed, false);
            zt.toneChans.Add(zt.Channels[b1]);
            string toning = zt.SetZoneHidden(bravo, true);
            zt.toneChans.Clear();
            Check(toning != null && toning.Contains("transmitting") && !bravo.Hidden, "zones: one being transmitted on can't be hidden");

            string shown = zt.SetZoneHidden(alpha, false);
            zt.ShowZone(alpha);
            zt.Select(zt.Channels[a1]);
            Check(shown == null && !alpha.Hidden && zt.CurrentZone == alpha && zt.Channels[a1].Selected,
                  "zones: shown again, its tab and channels work again (not listening until asked)");

            // the TUI's zones box
            zt.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'z'));
            bool boxOpen = zt.ZoneDialogOpen;
            zt.HandleInput(new InputEvent(InputKind.Key, InputKey.Down));
            zt.HandleInput(new InputEvent(InputKind.Key, InputKey.Space));
            bool hiddenByBox = unnamed.Hidden;
            zt.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'm'));     // a shortcut does nothing while the box is open
            zt.HandleInput(new InputEvent(InputKind.Key, InputKey.Space));
            zt.HandleInput(new InputEvent(InputKind.Key, InputKey.Escape));
            Check(boxOpen && hiddenByBox && !unnamed.Hidden && !zt.ZoneDialogOpen && !zt.Channels[a1].Muted,
                  "TUI zones box: z opens it, Space hides and shows the highlighted zone, Esc closes it");

            Check(Tui.NearestAnsi("#E57373") == Ansi.BrightRed && Tui.NearestAnsi("#81C784") == Ansi.BrightGreen && Tui.NearestAnsi("#1F6FEB") == Ansi.BrightBlue &&
                  Tui.NearestAnsi("#000000") == Ansi.Black && Tui.NearestAnsi("FFFFFF") == Ansi.BrightWhite && Tui.NearestAnsi("#7a1010") == Ansi.Red &&
                  Tui.NearestAnsi("teal") == Ansi.Default && Tui.NearestAnsi(null) == Ansi.Default,
                  "TUI tabs: codeplug tab colours become the nearest terminal colour, pale ones included");

            // one zone: no tab line, as before
            Tui single = new Tui(Program.LoadCodeplug(Path.Combine(tmp, "codeplug.yml")), zsys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
            single.Render(120, 40);
            single.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'z'));
            Check(single.Zones.Count == 1 && single.TabRows == 0 && !single.ZoneDialogOpen, "zones: a codeplug with one zone shows no tabs");

            // remembered: hidden zones and the tab on show
            string zoneSettings = Path.Combine(tmp, "zone-settings.json");
            Tui zfirst = new Tui(zcp, zsys, TimeSpan.FromSeconds(120), null, settingsPath: zoneSettings) { NoAudio = true };
            zfirst.SetFocus(b1);
            zfirst.Select(zfirst.Channels[a1]);
            zfirst.SetZoneHidden(zfirst.Zones[1], true);
            zfirst.SaveSettings();
            AppSettings edited = AppSettings.Load(zoneSettings, out _);     // as if Zone 2's channel had been left listening
            edited.For(zsys.Name).Channels[DispatchConsole.ChanKey(zfirst.Channels[n1])] = new ChannelSettings { Listening = true };
            edited.Save(zoneSettings);
            zfirst.Shutdown();
            Tui zsecond = new Tui(zcp, zsys, TimeSpan.FromSeconds(120), null, settingsPath: zoneSettings) { NoAudio = true };
            zsecond.RestoreSettings();
            Check(zsecond.Zones[1].Hidden && !zsecond.Channels[n1].Selected && zsecond.Channels[a1].Selected && zsecond.CurrentZone == zsecond.Zones[2] &&
                  File.ReadAllText(zoneSettings).Contains("\"HiddenZones\""),
                  "zones: hidden zones and the tab on show are remembered, and a hidden zone's channels stay off");
            zsecond.Shutdown();

            AppSettings every = AppSettings.Load(zoneSettings, out _);
            every.For(zsys.Name).HiddenZones = new List<string> { "alpha", "Zone 2", "BRAVO" };
            every.Save(zoneSettings);
            Tui zthird = new Tui(zcp, zsys, TimeSpan.FromSeconds(120), null, settingsPath: zoneSettings) { NoAudio = true };
            zthird.RestoreSettings();
            Check(!zthird.Zones[0].Hidden && zthird.Zones[1].Hidden && zthird.Zones[2].Hidden && !zthird.Channels[zthird.FocusIndex].Hidden,
                  "zones: if every zone was hidden, the first one is shown (names match whatever the case)");
            zthird.Shutdown();
            zt.Shutdown();
        }

        private static void RecordingPruneTests(string tmp)
        {
            // old recordings: only Sidetone's audio files in date-named day folders are deleted
            string recRoot = Path.Combine(tmp, "prune-recordings");
            DateTime pruneToday = new DateTime(2026, 11, 18);
            string Day(string systemName, int back) => Path.Combine(recRoot, systemName, pruneToday.AddDays(-back).ToString("yyyy-MM-dd"));
            foreach ((string sysName, int back) in new[] { ("CTRS", 0), ("CTRS", 29), ("CTRS", 30), ("CTRS", 45), ("Other", 60) })
            {
                Directory.CreateDirectory(Day(sysName, back));
                File.WriteAllText(Path.Combine(Day(sysName, back), "120000_Calling_TG504_RX_x.opus"), "x");
                File.WriteAllText(Path.Combine(Day(sysName, back), "120500_Calling_TG504_RX_y.wav"), "x");
            }
            File.WriteAllText(Path.Combine(Day("CTRS", 45), "notes.txt"), "keep me");
            Directory.CreateDirectory(Path.Combine(recRoot, "CTRS", "not-a-date"));
            File.WriteAllText(Path.Combine(recRoot, "CTRS", "not-a-date", "old.opus"), "x");
            File.WriteAllText(Path.Combine(recRoot, "loose.opus"), "x");

            int prunedRec = Recordings.Prune(recRoot, pruneToday, 30);
            Check(prunedRec == 6 && Directory.Exists(Day("CTRS", 29)) && File.Exists(Path.Combine(Day("CTRS", 29), "120000_Calling_TG504_RX_x.opus")) &&
                  !Directory.Exists(Day("CTRS", 30)) && !Directory.Exists(Day("Other", 60)) &&
                  File.Exists(Path.Combine(Day("CTRS", 45), "notes.txt")) && !File.Exists(Path.Combine(Day("CTRS", 45), "120500_Calling_TG504_RX_y.wav")) &&
                  File.Exists(Path.Combine(recRoot, "CTRS", "not-a-date", "old.opus")) && File.Exists(Path.Combine(recRoot, "loose.opus")),
                  $"recordings: those from more than N days ago are deleted (and their emptied day folders); anything else is left alone ({prunedRec} deleted)");
            Check(Recordings.Prune(recRoot, pruneToday, 0) == 0 && Recordings.Prune(Path.Combine(tmp, "no-such-folder"), pruneToday, 7) == 0 &&
                  Recordings.Prune(null, pruneToday, 7) == 0,
                  "recordings: keeping them, or a folder that doesn't exist, deletes nothing");
        }

        private static void CatchUpTests(string tmp, Codeplug cp, Codeplug.System sys)
        {
            // log files: one per day, old ones pruned, others left alone
            string logDir = Path.Combine(tmp, "logs");
            Directory.CreateDirectory(logDir);
            DateTime today = new DateTime(2026, 11, 18);
            foreach (int back in new[] { 0, 13, 14, 30 })
                File.WriteAllText(LogFiles.FileFor(logDir, today.AddDays(-back)), "x");
            File.WriteAllText(Path.Combine(logDir, "notes.txt"), "x");
            int pruned = LogFiles.Prune(logDir, today);
            Check(LogFiles.FileFor(logDir, today).EndsWith("sidetone-2026-11-18.log") && pruned == 2 &&
                  File.Exists(LogFiles.FileFor(logDir, today.AddDays(-13))) && !File.Exists(LogFiles.FileFor(logDir, today.AddDays(-14))) &&
                  File.Exists(Path.Combine(logDir, "notes.txt")),
                  $"log files: one per day; those older than {LogFiles.KeepDays} days are deleted, anything else is left alone");

            string liveLogs = Path.Combine(tmp, "live-logs");
            Tui logged = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true, LogFolder = liveLogs };
            logged.AddLog("a line for the log file");
            string todays = LogFiles.FileFor(liveLogs, DateTime.Now);
            string written = File.Exists(todays) ? File.ReadAllText(todays) : string.Empty;
            string fixedFile = Path.Combine(tmp, "fixed.log");
            Tui fixedLog = new Tui(cp, sys, TimeSpan.FromSeconds(120), fixedFile) { NoAudio = true, LogFolder = liveLogs };
            fixedLog.AddLog("goes to the --log file");
            Check(written.Contains("a line for the log file") && written.StartsWith(DateTime.Now.ToString("yyyy-MM-dd ")) &&
                  logged.LogFilePath == todays && fixedLog.LogFilePath == fixedFile && File.ReadAllText(fixedFile).Contains("--log file") &&
                  !File.ReadAllText(todays).Contains("--log file") && new Tui(cp, sys, TimeSpan.FromSeconds(120), null).LogFilePath == null,
                  "log files: lines go to today's file with the date; --log FILE wins; without a folder there's no file");

            // what both consoles start with: the option, else what Settings saved, else the default
            AppSettings chosen = new AppSettings { TxLimitSeconds = 300, PttDeviceName = "sidetone selftest no such device", PttButton = "BTN_NO_SUCH_BUTTON", KeyFile = "/keys/saved.ekc" };
            List<string> startNotes = new List<string>();
            var savedPtt = StartupChoices.PttDevice(null, null, null, chosen, startNotes);
            bool optionPttRefused = false, buttonsAlone = false;
            try { StartupChoices.PttDevice("sidetone selftest no such device", "BTN_NO_SUCH_BUTTON", null, chosen, new List<string>()); }
            catch (ArgumentException) { optionPttRefused = true; }
            try { StartupChoices.PttDevice(null, "BTN_THUMB", null, chosen, new List<string>()); }
            catch (ArgumentException) { buttonsAlone = true; }
            Check(StartupChoices.TxLimit(null, chosen) == 300 && StartupChoices.TxLimit(60, chosen) == 60 && StartupChoices.TxLimit(null, new AppSettings()) == 120 &&
                  StartupChoices.TxLimit(5000, chosen) == StartupChoices.MaxTxLimit && StartupChoices.MaxTxLimit == 900,
                  "start-up: the time limit is the option's, else the saved one, else 120 s; 5 to 900 s in both consoles");
            Check(savedPtt == null && startNotes.Any(n => n.Contains("saved PTT device isn't being used")) && optionPttRefused && buttonsAlone,
                  "start-up: a saved PTT device that can't be used (a bad button name) is a warning; on the command line it's an error");
            Check(StartupChoices.KeyFile(cp, null, chosen) == "/keys/saved.ekc" && StartupChoices.KeyFile(cp, "/keys/option.ekc", chosen) == "/keys/option.ekc" &&
                  StartupChoices.KeyFile(cp, null, new AppSettings()) == null,
                  "start-up: the key file is the option's, else the one chosen in Settings, else the codeplug's");

            // the TUI's key file box
            string ekc = Path.Combine(tmp, "tui-keys.ekc");
            WriteEkc(ekc, "q uiet", new[] { (true, (int)P25Defines.P25_ALGO_AES, (int)TestKeyId, Convert.ToHexString(TestKey)) });
            Tui kt = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true, KeyFilePath = ekc };
            kt.Render(120, 40);
            bool lockShown = kt.Render(120, 40).RowText(40 - Tui.FooterHeight).Contains("key file locked");
            void TypeIn(Tui t, string text)
            {
                foreach (char ch in text)
                    t.HandleInput(ch == ' ' ? new InputEvent(InputKind.Key, InputKey.Space) : new InputEvent(InputKind.Key, InputKey.Char, ch));
            }
            kt.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'K'));
            bool opened = kt.KeyFileDialogOpen;
            TypeIn(kt, "wrong");
            kt.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));
            bool refused = kt.KeyFileDialogOpen && (kt.KeyFileDialogError ?? string.Empty).Contains("assword") && !kt.KeyFileLoaded;
            TypeIn(kt, "q uiet");       // q and Space are typing here, not quit and PTT
            kt.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));
            Check(lockShown && opened && refused && !kt.KeyFileDialogOpen && kt.KeyFileLoaded && !kt.QuitRequested && kt.HasKey(kt.Channels.First(ch => ch.Encrypted)) &&
                  !kt.Render(120, 40).RowText(40 - Tui.FooterHeight).Contains("key file locked") && !string.Join("\n", kt.SnapshotLog(20)).Contains("q uiet"),
                  "TUI key file box: K opens it, a wrong password says so, the right one unlocks the keys (and is never logged)");
            kt.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'K'));
            Tui none = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
            none.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'K'));
            Check(!kt.KeyFileDialogOpen && kt.SnapshotLog(1).Any(l => l.Contains("already unlocked")) && !none.KeyFileDialogOpen &&
                  none.SnapshotLog(1).Any(l => l.Contains("no key file")),
                  "TUI key file box: not opened when the file is unlocked already, or when there is none");

            // the TUI's card buttons, on each card's bottom border
            Tui bt = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
            int borderRow = Tui.TitleHeight + Tui.CardHeight - 1;
            string border = bt.Render(120, 40).RowText(borderRow);
            int recAt = border.IndexOf("[rec]", StringComparison.Ordinal), patchAt = border.IndexOf("[⇄]", StringComparison.Ordinal);
            ConsoleChannel first = bt.Channels[0], secureCard = bt.Channels.First(ch => ch.Encrypted);
            if (recAt >= 0)
                bt.HandleInput(new InputEvent(InputKind.Click, X: recAt + 1, Y: borderRow));
            bool recording = first.Record && first.Selected;
            bt.Render(120, 40);
            if (patchAt >= 0)
                bt.HandleInput(new InputEvent(InputKind.Click, X: patchAt + 1, Y: borderRow));
            bool patched = first.Patch == 1;
            string border2 = bt.Render(120, 40).RowText(borderRow);
            int keyAt = border2.IndexOf("[key]", StringComparison.Ordinal);
            if (keyAt >= 0)
                bt.HandleInput(new InputEvent(InputKind.Click, X: keyAt + 1, Y: borderRow));
            Check(border.Contains("[▶]") && recording && patched && border2.Contains("[●REC]") && border2.Contains("[⇄1]") && keyAt > 0 &&
                  bt.FocusIndex == bt.Channels.ToList().IndexOf(secureCard) && bt.SnapshotLog(3).Any(l => l.Contains($"{secureCard.Cfg.Name}: not connected")),
                  $"TUI card buttons: replay, record, patch and key request are clickable on each card ('{border2.Trim()}')");
            bt.ClearPatches();
            bt.SetRecord(first, false);
        }

        /// <summary>A sound system that plays and records nothing, and counts what it's given.</summary>
        private sealed class FakeAudio : IAudioBackend
        {
            public sealed class Out : IAudioOutput
            {
                public int Blocks;
                public event Action<string> Failed;
                public void Write(short[] samples) => Interlocked.Increment(ref Blocks);
                public void Fail(string why) => Failed?.Invoke(why);
                public void Dispose() { }
            }
            public sealed class In : IAudioInput
            {
                public event Action<short[]> Block;
                public event Action<string> Failed;
                public void Raise(short[] block) => Block?.Invoke(block);
                public void Fail(string why) => Failed?.Invoke(why);
                public void Dispose() { }
            }

            public readonly List<Out> Outputs = new List<Out>();
            public In Input;
            public string Name => "Fake";
            public AudioDeviceList ListDevices() => throw new InvalidOperationException("no devices here");
            public IAudioOutput OpenOutput(string device) { Out o = new Out(); lock (Outputs) Outputs.Add(o); return o; }
            public IAudioInput OpenInput(string device) => Input = new In();
            public IReadOnlyList<string> Problems() => Array.Empty<string>();
        }

        private sealed class ListSink : IAudioSink
        {
            private readonly List<short[]> into;
            public ListSink(List<short[]> into) => this.into = into;
            public void Write(short[] samples) => into.Add((short[])samples.Clone());
            public void Dispose() { }
        }

        /// <summary>
        /// Writes a KFDtool-style key container: a length-prefixed gzip of an XML outer container whose
        /// inner container is AES-256 encrypted with a PBKDF2 (SHA-512) key from the password.
        /// </summary>
        private static void WriteEkc(string path, string password, (bool Tek, int Alg, int Id, string Hex)[] entries)
        {
            System.Text.StringBuilder inner = new System.Text.StringBuilder("<InnerContainer version=\"1.0\"><Keys>");
            foreach (var e in entries)
                inner.Append($"<KeyItem><KeyTypeAuto>false</KeyTypeAuto><KeyTypeTek>{(e.Tek ? "true" : "false")}</KeyTypeTek><KeyTypeKek>{(e.Tek ? "false" : "true")}</KeyTypeKek>" +
                             $"<Sln>1</Sln><AlgorithmId>{e.Alg}</AlgorithmId><KeyId>{e.Id}</KeyId><Key>{e.Hex}</Key></KeyItem>");
            inner.Append("</Keys></InnerContainer>");

            byte[] salt = Enumerable.Range(0, 32).Select(i => (byte)(i + 3)).ToArray();
            const int iterations = 1000;
            byte[] derived = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA512, 32);
            using System.Security.Cryptography.Aes aes = System.Security.Cryptography.Aes.Create();
            aes.Key = derived;
            aes.Mode = System.Security.Cryptography.CipherMode.CBC;
            byte[] cipher = new System.Security.Cryptography.Xml.EncryptedXml().EncryptData(System.Text.Encoding.UTF8.GetBytes(inner.ToString()), aes);
            var data = new System.Security.Cryptography.Xml.EncryptedData
            {
                Type = System.Security.Cryptography.Xml.EncryptedXml.XmlEncElementUrl,
                EncryptionMethod = new System.Security.Cryptography.Xml.EncryptionMethod(System.Security.Cryptography.Xml.EncryptedXml.XmlEncAES256Url),
                CipherData = new System.Security.Cryptography.Xml.CipherData(cipher)
            };
            string outer = "<OuterContainer version=\"1.0\"><KeyDerivation><DerivationAlgorithm>PBKDF2</DerivationAlgorithm><HashAlgorithm>SHA512</HashAlgorithm>" +
                           $"<KeyLength>32</KeyLength><IterationCount>{iterations}</IterationCount><Salt>{Convert.ToBase64String(salt)}</Salt></KeyDerivation>" +
                           data.GetXml().OuterXml + "</OuterContainer>";

            byte[] plain = System.Text.Encoding.UTF8.GetBytes(outer);
            using FileStream file = File.Create(path);
            file.Write(BitConverter.GetBytes(plain.Length));
            using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Compress);
            gzip.Write(plain);
        }
    } // public static class SelfTest
}
