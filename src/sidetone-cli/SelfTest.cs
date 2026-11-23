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

            // libvocoder's decoder used to leave its auto-gain flag uninitialised (patches/dvmvocoder-autogain.patch):
            // a decoder made in reused, dirty memory came out about six times too loud. Dirty the heap, then make decoders.
            double steadyRms = DecodedRms(new MBEDecoder(MBE_MODE.IMBE_88BIT), codewords);
            int loudDecoders = 0;
            for (int round = 0; round < 30; round++)
            {
                List<IntPtr> dirty = new List<IntPtr>();
                for (int b = 0; b < 16; b++)
                {
                    int size = 256 * (b + 1);
                    IntPtr block = Marshal.AllocHGlobal(size);
                    for (int i = 0; i < size; i++)
                        Marshal.WriteByte(block, i, 0x01);
                    dirty.Add(block);
                }
                foreach (IntPtr block in dirty)
                    Marshal.FreeHGlobal(block);
                double r = DecodedRms(new MBEDecoder(MBE_MODE.IMBE_88BIT), codewords);
                if (r > steadyRms * 1.5 || r < steadyRms / 1.5)
                    loudDecoders++;
            }
            Check(loudDecoders == 0, $"libvocoder: decoders made in reused memory decode at the same level ({loudDecoders} of 30 differed from RMS {steadyRms:F0})");

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

                // 18. one-way patches
                OneWayPatchTests(cp, sys);

                // 19. audio outputs per tab and per channel
                OutputTests(tmp);

                // 19b. web streams (MP3 over HTTP, from a local server)
                StreamTests(tmp);

                // 19c. several FNE systems at once
                MultiSystemTests(tmp);

                // 20. call history kept between runs
                HistoryTests(tmp, cp, sys);

                // 21. listen with a local key file
                ListenKeyTests(tmp);

                // 22. announcements: WAV files played onto talkgroups
                AnnouncementTests(tmp, cp, sys);

                // 23. text to speech and the time announcement
                SpeechTests(tmp, cp, sys);

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
                Check(OperatingSystem.IsWindows() || File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "control: the socket is readable and writable by its owner only");
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
            Check(noPw.OutputTarget == null && noPw.OutputReport.Contains($"{AudioBackend.Current.Name} default") && noPw.OutputReport.Contains("pw-dump not found"),
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

            // the same with the real timer, as on the air: a burst of four LDUs takes at least three LDU times to go out.
            // Timing on a busy machine can stall once (a first start loading from disk), so a second try is allowed.
            (int Seen, int Tdus, long Spread) LiveRun()
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
                live.ClearPatches();
                return (seen, Volatile.Read(ref liveOut.TduCount), lastAt - firstAt);
            }
            bool LiveOk((int Seen, int Tdus, long Spread) r) => r.Seen == 4 && r.Tdus == 5 && r.Spread >= 3 * 180 - 40 && r.Spread < 3 * 180 + 400;
            var liveResult = LiveRun();
            if (!LiveOk(liveResult))
                liveResult = LiveRun();
            Check(LiveOk(liveResult),
                  $"patch pacing: with the real timer, a burst goes out at the LDU rate and the call ends ({liveResult.Seen} LDUs over {liveResult.Spread} ms)");

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

                    // the transmit time limit: a transmission still going past it is stopped (the stuck-PTT safety stop)
                    Tui lt = new Tui(cp, sys, TimeSpan.FromSeconds(1), null);
                    CaptureSink limitOut = new CaptureSink(new CliPeer(sys, new KeyStore(), verbose: false));
                    lt.sinkForTest = limitOut;
                    lt.consoleRidForTest = 9000002;
                    string limitStart = lt.StartTx(lt.Channels.First(x => x.IsP25 && !x.Encrypted && !x.Cfg.RxOnly));
                    bool limitWasOn = lt.Transmitting;
                    lt.Housekeeping();
                    bool keptBefore = lt.Transmitting;
                    Thread.Sleep(1300);
                    lt.Housekeeping();
                    Check(limitStart == null && limitWasOn && keptBefore && !lt.Transmitting && limitOut.TduCount > 0 &&
                          lt.SnapshotLog(20).Any(l => l.Contains("1 s transmit limit reached")),
                          $"transmit limit: a transmission still on after the limit is stopped and ended on the network (started: {limitStart ?? "yes"}, TDUs {limitOut.TduCount})");
                    lt.Shutdown();
                }
                finally
                {
                    AudioBackend.Current = real;
                }
                Check(OperatingSystem.IsMacOS() ? AudioBackend.Current is PortAudioBackend && AudioBackend.Current.Name == "Core Audio"
                      : OperatingSystem.IsWindows() ? AudioBackend.Current.Name == "Windows audio"
                      : AudioBackend.Current is PipeWireBackend && AudioBackend.Current.Name == "PipeWire",
                      $"audio layer: the system's own backend is used (Linux PipeWire, macOS Core Audio, Windows WASAPI: {AudioBackend.Current.Name})");
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
                    string[] nameParts = wav != null ? Path.GetFileNameWithoutExtension(wav).Split('_') : Array.Empty<string>();
                    bool named = wav != null && nameParts.Length >= 4 && nameParts[0] == kept[0].Started.ToString("HHmmss") &&
                                 nameParts[3] == kept[0].Source.ToString() && wav.StartsWith(recDir) &&
                                 Path.GetFileName(Path.GetDirectoryName(wav)) == kept[0].Started.ToString("yyyy-MM-dd");
                    long size = wav != null && File.Exists(wav) ? new FileInfo(wav).Length : 0;
                    UnixFileMode mode = wav != null && File.Exists(wav) && !OperatingSystem.IsWindows() ? File.GetUnixFileMode(wav) : 0;     // Windows: the user's own folders
                    Check(rt.RecordingsWritten == 1 && named && size == 44 + 18 * 160 * 2 && (mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                          $"recording: the call is saved as a WAV named TIME_SYSTEM_CHANNEL_RADIOID[_ALIAS], readable only by the user ({size} bytes, {Path.GetFileName(wav)})");


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
                          (OperatingSystem.IsWindows() || (File.GetUnixFileMode(opus) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0),
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
                Check(LocalKeyFile.Resolve(cp, "/tmp/x.ekc") == Path.GetFullPath("/tmp/x.ekc") && LocalKeyFile.Resolve(cp, null) == null,     // (Windows adds the drive)
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
        private static double DecodedRms(MBEDecoder decoder, List<byte[]> codewords)
        {
            double energy = 0;
            int n = 0;
            for (int f = 0; f < codewords.Count; f++)
            {
                short[] o = new short[P25VoiceReceiver.FrameSamples];
                decoder.decode(codewords[f], o);
                if (f < 10)
                    continue;
                foreach (short v in o)
                    energy += (double)v * v;
                n += o.Length;
            }
            return Math.Sqrt(energy / Math.Max(1, n));
        }

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

            // a call starting on a listening channel is announced (Android wakes the screen with it)
            List<(ConsoleChannel Chan, uint Src)> callsStarted = new List<(ConsoleChannel, uint)>();
            zt.CallStarted += (ch, src) => callsStarted.Add((ch, src));
            bool wasListening = zt.Channels[n1].Selected;
            zt.Select(zt.Channels[n1]);
            zt.OnFrame(new fnecore.P25DataReceivedEvent(1, 3012, 611, fnecore.CallType.GROUP, P25DUID.LDU1, fnecore.FrameType.VOICE, 0, 90, new byte[300]));
            zt.OnFrame(new fnecore.P25DataReceivedEvent(1, 3012, 611, fnecore.CallType.GROUP, P25DUID.LDU2, fnecore.FrameType.VOICE, 0, 90, new byte[300]));
            zt.OnFrame(new fnecore.P25DataReceivedEvent(1, 3012, 611, fnecore.CallType.GROUP, P25DUID.TDU, fnecore.FrameType.TERMINATOR, 0, 90, new byte[300]));
            Check(callsStarted.Count == 1 && callsStarted[0].Chan == zt.Channels[n1] && callsStarted[0].Src == 3012,
                  $"console: a call starting on a listening channel is announced once, with its source (Android wakes the screen with it) [{string.Join(", ", callsStarted.Select(x => $"{x.Chan.Cfg.Name}/{x.Src}"))}]");

            // a call whose end (a TDU) is lost on the way ends after 3 s without frames; so does a busy channel's
            DateTime staleClock = DateTime.UtcNow;
            zt.clockForTest = () => staleClock;
            zt.OnFrame(new fnecore.P25DataReceivedEvent(1, 3012, 611, fnecore.CallType.GROUP, P25DUID.LDU1, fnecore.FrameType.VOICE, 0, 91, new byte[300]));
            bool rxStarted = zt.Channels[n1].Rx?.InCall == true;
            staleClock += TimeSpan.FromSeconds(2);
            zt.CallHousekeeping();
            bool rxHeld = zt.Channels[n1].Rx?.InCall == true;
            staleClock += TimeSpan.FromSeconds(2);
            zt.CallHousekeeping();
            bool rxEnded = zt.Channels[n1].Rx?.InCall == false && zt.SnapshotLog(10).Any(l => l.Contains("its end didn't arrive"));
            zt.Deselect(zt.Channels[n1]);
            zt.OnFrame(new fnecore.P25DataReceivedEvent(1, 3012, 611, fnecore.CallType.GROUP, P25DUID.LDU1, fnecore.FrameType.VOICE, 0, 92, new byte[300]));
            bool busy = zt.Channels[n1].Active;
            staleClock += TimeSpan.FromSeconds(2);
            zt.CallHousekeeping();
            bool busyHeld = zt.Channels[n1].Active;
            staleClock += TimeSpan.FromSeconds(2);
            zt.CallHousekeeping();
            zt.clockForTest = null;
            Check(rxStarted && rxHeld && rxEnded && busy && busyHeld && !zt.Channels[n1].Active,
                  $"console: a call whose end is lost ends after 3 s without frames, on a listening channel and a busy one [{rxStarted} {rxHeld} {rxEnded} {busy} {busyHeld}]");
            if (wasListening)
                zt.Select(zt.Channels[n1]);
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

        /// <summary>Writes a WAV file in any PCM layout: 8/16/24/32-bit integer, or 32-bit float.</summary>
        private static void WriteTestWav(string path, int rate, int channels, int bits, bool isFloat, double seconds, Func<double, int, double> sample, int format = 0)
        {
            int frames = (int)(rate * seconds), bytesPer = bits / 8;
            using BinaryWriter w = new BinaryWriter(File.Create(path));
            w.Write("RIFF"u8.ToArray()); w.Write(36 + frames * channels * bytesPer); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((ushort)(format != 0 ? format : isFloat ? 3 : 1)); w.Write((ushort)channels);
            w.Write(rate); w.Write(rate * channels * bytesPer); w.Write((ushort)(channels * bytesPer)); w.Write((ushort)bits);
            w.Write("data"u8.ToArray()); w.Write(frames * channels * bytesPer);
            for (int f = 0; f < frames; f++)
                for (int ch = 0; ch < channels; ch++)
                {
                    double v = Math.Clamp(sample((double)f / rate, ch), -1, 1);
                    if (isFloat) w.Write((float)v);
                    else if (bits == 8) w.Write((byte)Math.Round(v * 127 + 128));
                    else if (bits == 16) w.Write((short)Math.Round(v * 32767));
                    else if (bits == 24) { int x = (int)Math.Round(v * 8388607); w.Write((byte)x); w.Write((byte)(x >> 8)); w.Write((byte)(x >> 16)); }
                    else w.Write((int)Math.Round(v * 2147483647.0));
                }
        }

        /// <summary>The level of one frequency in some 8 kHz audio (Goertzel), relative to full scale.</summary>
        private static double ToneLevel(short[] x, double hz)
        {
            double k = 2 * Math.Cos(2 * Math.PI * hz / 8000), s1 = 0, s2 = 0;
            foreach (short v in x) { double s0 = v / 32768.0 + k * s1 - s2; s2 = s1; s1 = s0; }
            return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - k * s1 * s2)) / (x.Length / 2.0);
        }

        private static void AnnouncementTests(string tmp, Codeplug cp, Codeplug.System sys)
        {
            string dir = Path.Combine(tmp, "announce");
            Directory.CreateDirectory(dir);
            double Db(double v) => 20 * Math.Log10(Math.Max(v, 1e-9));

            // any common WAV becomes 8 kHz mono, levelled to -3 dBFS
            string cd = Path.Combine(dir, "cd-stereo.wav");
            WriteTestWav(cd, 44100, 2, 16, false, 2.0, (t, ch) => 0.25 * Math.Sin(2 * Math.PI * 1000 * t));
            short[] fromCd = WavFile.ReadAnnouncement(cd);
            int crossings = 0;
            for (int i = 1; i < fromCd.Length; i++) if ((fromCd[i - 1] < 0) != (fromCd[i] < 0)) crossings++;
            double cdLevel = WavFile.SpeechLevelDb(fromCd);
            Check(Math.Abs(fromCd.Length - 16000) <= 2 && Math.Abs(crossings - 4000) <= 20 && Math.Abs(cdLevel - VoiceProcessor.TargetDb) < 0.5,
                  $"announcement: a 44.1 kHz stereo WAV becomes 8 kHz mono at the same pitch, at the microphone's level ({fromCd.Length} samples, {cdLevel:F1} dBFS)");

            string hiRes = Path.Combine(dir, "float48k.wav");
            WriteTestWav(hiRes, 48000, 1, 32, true, 1.0, (t, ch) => 0.3 * Math.Sin(2 * Math.PI * 500 * t) + 0.3 * Math.Sin(2 * Math.PI * 5000 * t));
            short[] fromFloat = WavFile.ReadAnnouncement(hiRes);
            double at500 = ToneLevel(fromFloat, 500), alias3k = ToneLevel(fromFloat, 3000);
            Check(fromFloat.Length == 8000 && Db(at500) - Db(alias3k) > 40,
                  $"announcement: a 48 kHz float WAV is filtered before resampling, so a 5 kHz tone doesn't fold down to 3 kHz ({Db(at500) - Db(alias3k):F0} dB below)");

            string deep = Path.Combine(dir, "24bit.wav"), cheap = Path.Combine(dir, "8bit.wav");
            WriteTestWav(deep, 16000, 1, 24, false, 0.5, (t, ch) => 0.5 * Math.Sin(2 * Math.PI * 800 * t));
            WriteTestWav(cheap, 8000, 1, 8, false, 0.5, (t, ch) => 0.5 * Math.Sin(2 * Math.PI * 800 * t));
            short[] fromDeep = WavFile.ReadAnnouncement(deep), fromCheap = WavFile.ReadAnnouncement(cheap);
            Check(fromDeep.Length == 4000 && fromCheap.Length == 4000 && Db(ToneLevel(fromDeep, 800)) > -25 && Db(ToneLevel(fromCheap, 800)) > -25,
                  "announcement: 24-bit and 8-bit WAVs are read too");

            string quiet = Path.Combine(dir, "quiet.wav");
            WriteTestWav(quiet, 8000, 1, 16, false, 0.5, (t, ch) => 0.0316 * Math.Sin(2 * Math.PI * 700 * t));    // -30 dBFS
            double quietPeak = WavFile.ReadAnnouncement(quiet).Max(v => Math.Abs((int)v)) / 32768.0;
            Check(Math.Abs(Db(quietPeak) - (-18)) < 0.6, $"announcement: a quiet file is raised at most 12 dB ({Db(quietPeak):F1} dBFS)");

            // speech is levelled by its loudness (like the microphone), not its peaks; pauses don't count; a spike can't clip
            string loudSpeech = Path.Combine(dir, "loud-speech.wav"), softSpeech = Path.Combine(dir, "soft-speech.wav"), spiky = Path.Combine(dir, "spiky.wav");
            void SpeechFile(string path, double gain, bool spike)
            {
                using WavSink sink = new WavSink(path);
                for (int f = 0; f < 200; f++)
                {
                    short[] frame = f % 50 < 35 ? VoicedFrame(f).Select(v => (short)Math.Clamp(v * gain, -32767, 32767)).ToArray() : new short[160];   // with pauses
                    if (spike && f == 20) frame[80] = 32000;
                    sink.Write(frame);
                }
            }
            SpeechFile(loudSpeech, 4.0, false);
            SpeechFile(softSpeech, 0.3, false);
            SpeechFile(spiky, 0.3, true);
            double loudDb = WavFile.SpeechLevelDb(WavFile.ReadAnnouncement(loudSpeech)), softDb = WavFile.SpeechLevelDb(WavFile.ReadAnnouncement(softSpeech));
            short[] fromSpiky = WavFile.ReadAnnouncement(spiky);
            double spikyPeak = Db(fromSpiky.Max(v => Math.Abs((int)v)) / 32768.0);
            Check(Math.Abs(loudDb - VoiceProcessor.TargetDb) < 1 && Math.Abs(softDb - VoiceProcessor.TargetDb) < 1 && spikyPeak <= WavFile.AnnouncementCeilingDb + 0.1,
                  $"announcement: loud and soft speech both come out at the microphone's level (-20 dBFS: {loudDb:F1}, {softDb:F1}), and a spike is kept under -1 dBFS ({spikyPeak:F1})");

            string notWav = Path.Combine(dir, "notes.wav"), adpcm = Path.Combine(dir, "adpcm.wav"), tooLong = Path.Combine(dir, "long.wav");
            File.WriteAllText(notWav, "not audio");
            WriteTestWav(adpcm, 8000, 1, 16, false, 0.1, (t, ch) => 0, format: 2);
            WriteTestWav(tooLong, 4000, 1, 8, false, WavFile.MaxAnnouncementSeconds + 1, (t, ch) => 0);
            string Why(string path) { try { WavFile.ReadAnnouncement(path); return null; } catch (ArgumentException ex) { return ex.Message; } }
            string w1 = Why(notWav), w2 = Why(adpcm), w3 = Why(tooLong);
            Check(w1 != null && w1.Contains("isn't a WAV") && w2 != null && w2.Contains("can't be read") && w3 != null && w3.Contains("minutes"),
                  "announcement: a file that isn't a WAV, a compressed WAV and one that's too long are refused, saying why");

            // sent like a tone: lead-in, then the audio through the voice encoder, in whole LDUs
            TonePlan plan = TonePlan.FromAudio("announcement test", fromCd);
            int lead = TonePlan.LeadInMs / 20;
            List<short[]> planned = plan.MonitorPcm().ToList();
            Check(plan.Frames.Length % 9 == 0 && plan.Frames.All(f => f == null) && planned.Count == plan.Frames.Length &&
                  planned.Take(lead).All(f => f.All(v => v == 0)) && planned[lead].SequenceEqual(fromCd.Take(160)) && plan.Frames.Length >= lead + 100,
                  "announcement: the plan is the lead-in, then the audio, in whole LDUs");

            // speech-like audio (IMBE is a speech codec: a pure sine doesn't survive it, which is why alert tones use tone codewords)
            string speech = Path.Combine(dir, "speech.wav");
            using (WavSink sw = new WavSink(speech))
                for (int i = 0; i < 150; i++) sw.Write(VoicedFrame(i));
            short[] spoken = WavFile.ReadAnnouncement(speech);
            TonePlan speechPlan = TonePlan.FromAudio("announcement speech", spoken);
            Codeplug.System aOffline = new Codeplug.System { Name = "Test", Identity = "TEST", Address = "127.0.0.1", Port = 62031, PeerId = 9000001, Rid = "9000001", Password = "x" };
            CaptureSink annSink = new CaptureSink(new CliPeer(aOffline, new KeyStore(), verbose: false));
            P25Transmitter annTx = new P25Transmitter(annSink, new KeyStore(), 9000001, 504, P25Defines.P25_ALGO_UNENCRYPT, 0);
            annTx.Start(out _);
            new ToneJob(speechPlan, new List<(string, P25Transmitter)> { ("Calling", annTx) }, null).Start(realTime: false);
            List<byte[]> annHeard = RoundTrip(annSink, 504, 9000001, new KeyStore(), out _);
            MBEDecoder annDec = new MBEDecoder(MBE_MODE.IMBE_88BIT);
            List<short> annPcm = new List<short>();
            foreach (byte[] cw in annHeard.Skip(lead + 10).Take(120)) { short[] o = new short[160]; annDec.decode(cw, o); annPcm.AddRange(o); }
            double RmsDb(IEnumerable<short> xs) { double sum = 0; int n = 0; foreach (short x in xs) { sum += (double)x * x; n++; } return Db(Math.Sqrt(sum / Math.Max(1, n)) / 32768.0); }
            // the vocoder alone on the same audio: what the codec itself does to the level, for comparison
            MBEEncoder soloEnc = new MBEEncoder(MBE_MODE.IMBE_88BIT);
            MBEDecoder soloDec = new MBEDecoder(MBE_MODE.IMBE_88BIT);
            List<short> soloPcm = new List<short>();
            for (int f = 0; f < 10 + 120; f++)
            {
                short[] frame = new short[160];
                Array.Copy(spoken, f * 160, frame, 0, Math.Min(160, spoken.Length - f * 160));
                byte[] cw = new byte[P25VoiceReceiver.ImbeLength];
                soloEnc.encode(frame, cw);
                short[] o = new short[160];
                soloDec.decode(cw, o);
                if (f >= 10) soloPcm.AddRange(o);
            }
            double codecLevel = RmsDb(soloPcm), heardLevel = RmsDb(annPcm), sentLevel = RmsDb(spoken.Skip(10 * 160).Take(120 * 160));
            Check(annHeard.Count == speechPlan.Frames.Length && Math.Abs(codecLevel - heardLevel) < 1.5,
                  $"announcement: speech sent through the transmitter arrives as the vocoder alone would make it ({heardLevel:F1} vs {codecLevel:F1} dBFS; {sentLevel:F1} going in)");

            // the console: the remembered list, and why it won't send
            string annSettings = Path.Combine(tmp, "announce-settings.json");
            Tui at = new Tui(cp, sys, TimeSpan.FromSeconds(1), null, settingsPath: annSettings) { NoAudio = true };
            string addedBad = at.AddAnnouncement(notWav), addedGood = at.AddAnnouncement(cd);
            at.SaveSettings();
            bool listed = at.Announcements.SequenceEqual(new[] { Path.GetFullPath(cd) }) && File.ReadAllText(annSettings).Contains("cd-stereo.wav");
            string tooLongForLimit = at.PlayAnnouncement(cd, allListening: false);
            Tui at2 = new Tui(cp, sys, TimeSpan.FromSeconds(120), null, settingsPath: annSettings) { NoAudio = true };
            string offlineWhy = at2.PlayAnnouncement(at2.Announcements[0], allListening: false);
            string noneListening = at2.PlayAnnouncement(at2.Announcements[0], allListening: true);
            Check(addedBad != null && addedGood == null && listed && tooLongForLimit.Contains("longer than the 1 s transmit time limit") &&
                  offlineWhy.Contains("not connected") && noneListening.Contains("no listening channels") && DispatchConsole.AnnouncementName(cd) == "cd-stereo",
                  "announcements: remembered; refused (saying why) when too long for the time limit, not connected, or with nothing listening");

            // the TUI's box: add by path, play (refused here: not connected), remove
            at2.Render(120, 40);
            at2.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'n'));
            bool annOpen = at2.AnnounceDialogOpen;
            at2.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'a'));
            foreach (char ch in deep)
                at2.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, ch));
            at2.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));
            bool addedByPath = at2.Announcements.Count == 2;
            at2.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));
            bool refusedShown = (at2.AnnounceDialogMessage ?? string.Empty).Contains("not connected") && at2.AnnounceDialogOpen;
            at2.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'r'));
            at2.HandleInput(new InputEvent(InputKind.Key, InputKey.Escape));
            Check(annOpen && addedByPath && refusedShown && at2.Announcements.Count == 1 && !at2.AnnounceDialogOpen,
                  "TUI announcements box: n opens it, a adds a file by path, Enter tries to play it (and says why not), r removes it");
        }

        /// <summary>A stand-in for a platform's text to speech (Android's): writes a second of speech-like audio.</summary>
        private sealed class FakePlatformSpeech : IPlatformSpeech
        {
            public string Name => "Test speech";
            public string VoiceName => "test voice";
            public string LastText;
            public double LastRate;
            public bool Fail;

            public void SpeakToWav(string text, double rate, string wavPath)
            {
                LastText = text;
                LastRate = rate;
                if (Fail)
                    throw new ArgumentException("Test speech isn't ready");
                using WavSink sink = new WavSink(wavPath);
                for (int f = 0; f < 50; f++)
                    sink.Write(VoicedFrame(f));
            }
        }

        private static void ConnectionTests()
        {
            // fnecore logs in again by itself when the connection is lost; the peer's state follows it (it once stayed "connected")
            Codeplug.System cs = new Codeplug.System { Name = "Conn", Identity = "CONN", Address = "127.0.0.1", Port = 62031, PeerId = 9000002, Rid = "9000002", Password = "x" };
            CliPeer cp = new CliPeer(cs, new KeyStore(), verbose: false);
            bool before = !cp.Connected;
            cp.PeerForTest.Information.State = fnecore.ConnectionState.RUNNING;
            bool up = cp.Connected;
            cp.PeerForTest.Information.State = fnecore.ConnectionState.WAITING_LOGIN;     // what fnecore sets on a lost connection
            Check(before && up && !cp.Connected, "FNE peer: connected only while logged in; a lost connection (fnecore logging in again) shows as not connected");
            string sw = cp.PeerForTest.Information.Details.Software;
            Version mine = typeof(CliPeer).Assembly.GetName().Version;
            Check(sw == $"SIDETONE_R{mine.Major:D2}A{mine.Minor:D2}_{CliPeer.OsTag()}" && CliPeer.SoftwareId(new Version(1, 2, 3), "AND") == "SIDETONE_R01A02_AND" &&
                  CliPeer.SoftwareId(new Version(0, 31, 6), "WIN") == "SIDETONE_R00A31_WIN" && CliPeer.SoftwareId(new Version(0, 31), "MAC") == "SIDETONE_R00A31_MAC",
                  $"FNE peer: tells the FNE its version and platform, in the Windows console's form ({sw})");
        }

        private static void SettingsRecoveryTests(string tmp)
        {
            // an unreadable settings file is kept before the defaults are used, since the next save writes over it
            string bad = Path.Combine(tmp, "broken-settings.json");
            File.WriteAllText(bad, "{ \"Theme\": \"dark\", }}");
            AppSettings got = AppSettings.Load(bad, out string why);
            Check(got != null && why != null && why.Contains("kept as") && File.Exists(bad + ".bad") && File.ReadAllText(bad + ".bad").Contains("\"dark\""),
                  $"settings: an unreadable settings file gives defaults, says why, and is kept as .bad before the next save writes over it ({why})");
        }

        private static void AddressTests()
        {
            Check(CliPeer.ResolveAddress(" 10.0.0.5 ").ToString() == "10.0.0.5" && CliPeer.ResolveAddress("localhost") != null,
                  "FNE address: an IP address (spaces around it ignored) or a host name that can be looked up");
            string Why(string address)
            {
                try { CliPeer.ResolveAddress(address); return null; }
                catch (FneAddressException ex) { return ex.Host == address.Trim() && !ex.Message.Contains(address.Trim()) ? ex.Message : "host in the message"; }
            }
            string unknown = Why("sidetone-selftest.invalid"), scheme = Why("http://fne.example.org"), port = Why("fne.example.org:62031");
            Check(unknown != null && unknown.StartsWith("couldn't find the FNE's address") && unknown.Contains("VPN") &&
                  scheme != null && scheme.Contains("http://") && port != null && port.Contains("port in it"),
                  $"FNE address: a name that can't be found, a scheme or a port each get a plain reason, without the address in it (for the debug log) ({unknown})");
        }

        private static void DebugLogTests(string tmp)
        {
            string dir = Path.Combine(tmp, "debug"), consoleLogs = Path.Combine(tmp, "debug-console-logs");
            Directory.CreateDirectory(consoleLogs);
            File.WriteAllText(LogFiles.FileFor(consoleLogs, DateTime.UtcNow), "12:00:00 Tac 1: RX Unit 12\n");
            File.WriteAllText(LogFiles.FileFor(consoleLogs, DateTime.UtcNow.AddDays(-10)), "old\n");
            File.SetLastWriteTime(LogFiles.FileFor(consoleLogs, DateTime.UtcNow.AddDays(-10)), DateTime.UtcNow.AddDays(-10));
            DebugLog.StopForTest();
            DebugLog.Start(dir, "self-test");
            try
            {
                DebugLog.Mark("codeplug", "loading test.yml (1234 bytes)");
                try { throw new InvalidOperationException("outer", new FormatException("inner")); }
                catch (InvalidOperationException ex) { DebugLog.Exception("codeplug", "test.yml wasn't opened", ex); }
                string today = File.ReadAllText(Directory.GetFiles(dir, "debug-*.log").Single());
                Check(today.Contains("start: Sidetone") && today.Contains("codeplug: loading test.yml (1234 bytes)") && DebugLog.Step == "codeplug: loading test.yml (1234 bytes)" &&
                      today.Contains("System.InvalidOperationException: outer") && today.Contains("caused by System.FormatException: inner") && today.Contains("at Sidetone"),
                      "debug log: each line is on disk at once (it survives a crash), with the step it's on and exceptions with their stack traces");

                // a UI that stops answering is noticed, with what it was doing, and noted again when it answers
                DebugLog.FreezeAfter = TimeSpan.FromMilliseconds(300);
                DebugLog.AskEvery = TimeSpan.FromMilliseconds(50);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
                bool frozen = true;
                DebugLog.WatchUi(job => Task.Run(() => { if (Volatile.Read(ref frozen)) release.Wait(); job(); }));
                Thread.Sleep(700);
                Volatile.Write(ref frozen, false);
                release.Set();
                Thread.Sleep(300);
                string later = File.ReadAllText(Directory.GetFiles(dir, "debug-*.log").Single());
                Check(later.Contains("the UI hasn't answered for 0 s; it was on: codeplug: loading test.yml") && later.Contains("freeze: the UI answered again after"),
                      "debug log: a frozen UI is written down with the step it was on, and again when it answers");

                string zip = Path.Combine(tmp, "logs.zip");
                int files = DebugLog.MakeBundle(zip, "about this test", consoleLogs);
                using System.IO.Compression.ZipArchive z = System.IO.Compression.ZipFile.OpenRead(zip);
                List<string> names = z.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
                string about;
                using (StreamReader r = new StreamReader(z.GetEntry("about.txt").Open()))
                    about = r.ReadToEnd();
                Check(files == 2 && names.Count == 3 && names.Contains("about.txt") && names.Any(n => n.StartsWith("debug/debug-")) &&
                      names.Contains($"console/{Path.GetFileName(LogFiles.FileFor(consoleLogs, DateTime.UtcNow))}") && about == "about this test",
                      $"debug log: Save debug logs zips the debug log, the recent console logs (not old ones) and about.txt ({string.Join(", ", names)})");
            }
            finally
            {
                DebugLog.StopForTest();
            }
        }

        private static void PlatformHookTests(string tmp)
        {
            // text to speech: a platform engine wins over Piper and espeak-ng unless one of them is chosen and installed,
            // and its audio is levelled like the rest
            FakePlatformSpeech fake = new FakePlatformSpeech();
            Speech.Platform = fake;
            string[] foldersWere = Speech.programFoldersForTest;
            Speech.programFoldersForTest = new[] { Path.Combine(tmp, "platform-speech-no-programs") };     // Piper chosen but not installed (Android)
            try
            {
                SpeechEngine e = Speech.Choose(Speech.PiperSetting, "/nowhere/voice.onnx", out string why);
                Check(e != null && why == null && e.Kind == SpeechEngineKind.Platform && e.Label == "Test speech, voice test voice",
                      "platform speech: chosen unless the engine chosen is installed, and named for the platform's engine");
                e.Rate = 0.9;
                short[] said = Speech.Synthesize(e, "Shift change\nin ten minutes");
                Check(said.Length >= 7000 && Math.Abs(WavFile.SpeechLevelDb(said) - VoiceProcessor.TargetDb) < 2 && fake.LastText == "Shift change in ten minutes" && fake.LastRate == 0.9,
                      "platform speech: text and speed handed over, the WAV read back and levelled to the microphone's level");
                fake.Fail = true;
                string failed = null;
                try { Speech.Synthesize(e, "hello"); } catch (ArgumentException ex) { failed = ex.Message; }
                Check(failed == "Test speech isn't ready", "platform speech: its failure comes back as the reason");
            }
            finally
            {
                Speech.Platform = null;
                Speech.programFoldersForTest = foldersWere;
            }
            Check(Speech.Choose(Speech.PiperSetting, null, out _)?.Kind != SpeechEngineKind.Platform, "platform speech: unset, the programs are looked for again");

            // key file passwords: the platform's store is used instead of secret-tool
            Dictionary<string, string> kept = new Dictionary<string, string>();
            SecretStore.Platform = (k => kept.TryGetValue(k, out string v) ? v : null, (k, v) => { kept[k] = v; return true; }, k => kept.Remove(k));
            try
            {
                string keyFile = Path.Combine(tmp, "platform.ekc");
                bool stored = SecretStore.Store(keyFile, "pa55");
                string back = SecretStore.Lookup(keyFile);
                bool cleared = SecretStore.Clear(keyFile);
                Check(SecretStore.Available && stored && back == "pa55" && cleared && SecretStore.Lookup(keyFile) == null && kept.Count == 0,
                      "key file passwords: kept in the platform's store when there is one (stored, found, forgotten)");
            }
            finally { SecretStore.Platform = null; }

            // notifications: the platform's own
            List<string> notes = new List<string>();
            DesktopNotify.Platform = (title, body, urgent) => { notes.Add($"{title}|{urgent}"); return true; };
            try
            {
                Check(DesktopNotify.Send("Paged by Unit 12", "body", true) && notes.Count == 1 && notes[0] == "Paged by Unit 12|True",
                      "notifications: shown by the platform when it has its own");
            }
            finally { DesktopNotify.Platform = null; }
        }

        private static void SpeechTests(string tmp, Codeplug cp, Codeplug.System sys)
        {
            PlatformHookTests(tmp);
            DebugLogTests(tmp);
            AddressTests();
            ConnectionTests();
            SettingsRecoveryTests(tmp);

            // the time in words, as every engine will say it
            Check(Speech.TimeWords(14, 20, false) == "two twenty P M" && Speech.TimeWords(14, 5, false) == "two oh five P M" &&
                  Speech.TimeWords(0, 0, false) == "twelve A M" && Speech.TimeWords(12, 0, false) == "twelve P M" && Speech.TimeWords(9, 45, false) == "nine forty five A M",
                  "time announcement: 12-hour time in words (two twenty P M, two oh five P M, twelve A M at midnight)");
            Check(Speech.TimeWords(14, 0, true) == "fourteen hundred" && Speech.TimeWords(9, 30, true) == "zero nine thirty" && Speech.TimeWords(0, 15, true) == "zero zero fifteen" &&
                  Speech.TimeWords(0, 0, true) == "zero zero hundred" && Speech.TimeWords(23, 59, true) == "twenty three fifty nine" && Speech.TimeWords(14, 5, true) == "fourteen oh five",
                  "time announcement: 24-hour time as it's said on the radio (fourteen hundred, zero nine thirty, fourteen oh five)");

            // zones: abbreviations that follow daylight saving, offsets where tzdata has no letters
            DateTime winter = new DateTime(2026, 1, 15, 20, 20, 0, DateTimeKind.Utc), summer = new DateTime(2026, 7, 15, 20, 20, 0, DateTimeKind.Utc);
            TimeZoneInfo la = Speech.Zone("America/Los_Angeles"), dubai = Speech.Zone("Asia/Dubai"), nepal = Speech.Zone("Asia/Kathmandu");
            string laWinter = Speech.ZoneAbbreviation(la, winter), laSummer = Speech.ZoneAbbreviation(la, summer);
            string dubaiAbbr = Speech.ZoneAbbreviation(dubai, winter), nepalAbbr = Speech.ZoneAbbreviation(nepal, winter);
            Check(laWinter == "PST" && laSummer == "PDT" && Speech.SayAbbreviation("PST") == "P S T" && Speech.ZoneAbbreviation(Speech.Zone("UTC"), winter) == "UTC" &&
                  dubaiAbbr == "UTC+4" && Speech.SayAbbreviation(dubaiAbbr) == "U T C plus four" && nepalAbbr == "UTC+5:45" && Speech.SayAbbreviation(nepalAbbr) == "U T C plus five forty five" &&
                  Speech.SayAbbreviation("UTC-3:30") == "U T C minus three thirty",
                  $"time announcement: the zone is said as its abbreviation, PST in winter and PDT in summer, or as an offset (U T C plus four) ({laWinter}, {laSummer}, {dubaiAbbr}, {nepalAbbr})");
            Check(Speech.Zone("Not/AZone") == TimeZoneInfo.Local && !Speech.KnownZone("Not/AZone") && Speech.KnownZone(null) && Speech.KnownZone("America/Chicago") &&
                  Speech.ZoneIds().First() == "UTC" && Speech.ZoneIds().Contains("America/Chicago"),
                  "time announcement: zones are checked, and the list starts with UTC");

            // the words before and after, the clock and the zone
            TimeAnnouncementSettings ctrs = new TimeAnnouncementSettings { Before = "CTRS time is", After = "This is CTRS.", TimeZone = "America/Chicago" };
            string said12 = Speech.TimeAnnouncementText(ctrs, winter), shown12 = Speech.TimeAnnouncementPreview(ctrs, winter);
            TimeAnnouncementSettings named = ctrs.Copy(); named.ZoneName = "Central time";
            TimeAnnouncementSettings h24 = ctrs.Copy(); h24.Hour24 = true; h24.SayZone = false; h24.After = null;
            TimeAnnouncementSettings bare = new TimeAnnouncementSettings { Before = "", TimeZone = "UTC", SayZone = false };
            Check(said12 == "CTRS time is two twenty P M C S T. This is CTRS." && shown12 == "CTRS time is 2:20 PM CST. This is CTRS." &&
                  Speech.TimeAnnouncementText(named, winter) == "CTRS time is two twenty P M Central time. This is CTRS." &&
                  Speech.TimeAnnouncementText(h24, winter) == "CTRS time is fourteen twenty" && Speech.TimeAnnouncementPreview(h24, winter) == "CTRS time is 1420" &&
                  Speech.TimeAnnouncementText(bare, winter) == "eight twenty P M",
                  $"time announcement: words before, the time in the chosen zone, the zone, then words after ({said12})");

            if (OperatingSystem.IsWindows())
                Console.WriteLine("SKIP: text to speech: the stand-in Piper and espeak-ng are shell scripts (Linux and macOS run these checks)");
            else
            {
                // engines: stand-in programs in a folder of their own, so the test doesn't depend on what's installed
                string bin = Path.Combine(tmp, "speech-bin"), voices = Path.Combine(tmp, "speech-voices"), heard = Path.Combine(tmp, "speech-heard.txt");
                Directory.CreateDirectory(bin);
                Directory.CreateDirectory(voices);
                string spokenWav = Path.Combine(tmp, "speech-out.wav");
                WriteTestWav(spokenWav, 22050, 1, 16, false, 1.5, (t, ch) => 0.3 * Math.Sin(2 * Math.PI * 220 * t) * Math.Sin(2 * Math.PI * 3 * t));
                void FakeEngine(string name, string outFlag, bool fail = false)
                {
                    string script = Path.Combine(bin, name);
                    File.WriteAllText(script, "#!/bin/sh\n" +
                        $"echo \"$@\" > '{heard}.args'\ncat > '{heard}'\n" +
                        (fail ? "echo 'voice model is broken' >&2\nexit 3\n"
                              : $"while [ $# -gt 0 ]; do if [ \"$1\" = \"{outFlag}\" ]; then cp '{spokenWav}' \"$2\"; fi; shift; done\n"));
                    File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                Speech.programFoldersForTest = new[] { bin };
                Speech.voiceFoldersForTest = new[] { voices };
                try
                {
                    SpeechEngine none = Speech.Choose(null, null, out string noneWhy);
                    FakeEngine("espeak-ng", "-w");
                    SpeechEngine espeakOnly = Speech.Choose(null, null, out _);
                    FakeEngine("piper", "-f");
                    SpeechEngine piperNoVoice = Speech.Choose(null, null, out _), piperForced = Speech.Choose(Speech.PiperSetting, null, out string forcedWhy);
                    string deepVoice = Path.Combine(voices, "en", "en_US", "lessac", "medium", "en_US-lessac-medium.onnx");
                    Directory.CreateDirectory(Path.GetDirectoryName(deepVoice));
                    File.WriteAllText(deepVoice, "model");
                    File.WriteAllText(deepVoice + ".json", "{}");
                    File.WriteAllText(Path.Combine(voices, "half-downloaded.onnx"), "no json beside it");
                    SpeechEngine piper = Speech.Choose(null, null, out _), espeakChosen = Speech.Choose(Speech.EspeakSetting, "en-gb", out _);
                    Check(none == null && noneWhy.Contains("install Piper") && noneWhy.Contains("espeak-ng") &&
                          espeakOnly?.Kind == SpeechEngineKind.EspeakNg && espeakOnly.Voice == "en-us" &&
                          piperNoVoice?.Kind == SpeechEngineKind.EspeakNg && piperForced == null && forcedWhy.Contains("no voice") &&
                          piper?.Kind == SpeechEngineKind.Piper && piper.Voice == deepVoice && Speech.PiperVoices().Count == 1 && piper.Label == "Piper, voice en_US-lessac-medium" &&
                          espeakChosen?.Kind == SpeechEngineKind.EspeakNg && espeakChosen.Voice == "en-gb",
                          "text to speech: Piper with a voice (found in sub-folders) is chosen first, else espeak-ng; with neither, it says what to install");

                    short[] made = Speech.Synthesize(piper, "Shift change in ten minutes.\nAll units.");
                    string piperArgs = File.ReadAllText(heard + ".args").Trim(), piperText = File.ReadAllText(heard).Trim();
                    short[] madeEspeak = Speech.Synthesize(espeakChosen, "Radio check");
                    string espeakArgs = File.ReadAllText(heard + ".args").Trim();
                    Check(Math.Abs(made.Length - 12000) <= 2 && madeEspeak.Length == made.Length && piperText == "Shift change in ten minutes. All units." &&
                          piperArgs.StartsWith($"-m {deepVoice} -f ") && espeakArgs.StartsWith("--stdin -v en-gb -w "),
                          $"text to speech: the engine gets the text on its input (not through a shell) and its WAV becomes 8 kHz announcement audio ({made.Length} samples; {piperArgs.Split(' ')[0]} …)");

                    // the speed: Piper's length scale (the voice's own, divided by the speed), espeak-ng's words per minute
                    piper.Rate = 0.9;
                    espeakChosen.Rate = 0.9;
                    Speech.Synthesize(piper, "slower");
                    string slowArgs = File.ReadAllText(heard + ".args").Trim();
                    File.WriteAllText(deepVoice + ".json", "{\"audio\": {\"sample_rate\": 22050}, \"inference\": {\"noise_scale\": 0.667, \"length_scale\": 1.2}}");
                    Speech.Synthesize(piper, "slower still");
                    string slowOwnArgs = File.ReadAllText(heard + ".args").Trim();
                    Speech.Synthesize(espeakChosen, "slower");
                    string slowEspeak = File.ReadAllText(heard + ".args").Trim();
                    piper.Rate = 1;
                    espeakChosen.Rate = 1;
                    Check(slowArgs.StartsWith($"-m {deepVoice} --length_scale 1.111 -f ") && slowOwnArgs.Contains("--length_scale 1.333 ") &&
                          slowEspeak.StartsWith("--stdin -v en-gb -s 158 -w ") && !piperArgs.Contains("length_scale") && !espeakArgs.Contains(" -s "),
                          $"text to speech: a slower speed lengthens Piper's own pace (1.2 becomes 1.333 at 90%) and slows espeak-ng's words per minute; normal passes nothing ({slowOwnArgs.Split(" -f ")[0].Split(' ').Last()})");

                    // through the console: the engine runs on a worker thread, and the audio is sent from the UI thread
                    Tui worker = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
                    List<(TonePlan Plan, List<ConsoleChannel> Targets)> workerSent = new List<(TonePlan, List<ConsoleChannel>)>();
                    worker.sendAnnouncementForTest = (plan, targets) => workerSent.Add((plan, targets));
                    string workerWhy = worker.SpeakAnnouncement("Radio check", allListening: false);
                    bool preparing = worker.SpeechPreparing, busyRefused = worker.SpeakAnnouncement("Again", false)?.Contains("still being made") == true;
                    for (int i = 0; i < 500 && workerSent.Count == 0; i++)
                    {
                        Thread.Sleep(10);
                        worker.Tick();
                    }
                    Check(workerWhy == null && preparing && busyRefused && workerSent.Count == 1 && !worker.SpeechPreparing && workerSent[0].Plan.Frames.Length > 75 &&
                          worker.SpokenLength("Radio check") == TimeSpan.FromSeconds(1.5),
                          "spoken announcements: the engine runs off the UI thread (another is refused until it has finished), then the audio is sent");

                    FakeEngine("piper", "-f", fail: true);
                    string failed = null;
                    try { Speech.Synthesize(piper, "hello"); } catch (ArgumentException ex) { failed = ex.Message; }
                    string empty = null;
                    try { Speech.Synthesize(piper, "  "); } catch (ArgumentException ex) { empty = ex.Message; }
                    Check(failed != null && failed.Contains("Piper failed") && failed.Contains("exit code 3") && failed.Contains("voice model is broken") && empty != null,
                          $"text to speech: an engine that fails says why ({failed})");
                }
                finally
                {
                    Speech.programFoldersForTest = null;
                    Speech.voiceFoldersForTest = null;
                }

            }

            // a real engine, when one is installed (CI installs espeak-ng; Piper here when it has a voice)
            SpeechEngine real = Speech.Choose(null, null, out _);
            if (real != null)
            {
                System.Diagnostics.Stopwatch took = System.Diagnostics.Stopwatch.StartNew();
                short[] realSpeech = Speech.Synthesize(real, Speech.TimeAnnouncementText(new TimeAnnouncementSettings { TimeZone = "UTC" }, winter));
                double realPeak = 20 * Math.Log10(realSpeech.Max(v => Math.Abs((int)v)) / 32768.0), realLevel = WavFile.SpeechLevelDb(realSpeech);
                Check(realSpeech.Length > 8000 && realSpeech.Length < 8000 * 10 && realPeak <= WavFile.AnnouncementCeilingDb + 0.1 && realLevel > VoiceProcessor.TargetDb - 4 && realLevel < VoiceProcessor.TargetDb + 1,
                      $"text to speech: {real.Label} says a time announcement at about the microphone's level ({realSpeech.Length / 8000.0:F1} s of audio, speech {realLevel:F1} dBFS, peak {realPeak:F1} dBFS, in {took.ElapsedMilliseconds} ms)");
            }
            else
                Console.WriteLine("(no text-to-speech engine installed: the real-engine check is skipped)");

            // the system's own voice: macOS's say, Windows's SAPI, registered as the consoles do at start-up
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
            {
                IPlatformSpeech speechWas = Speech.Platform;
                var secretsWere = SecretStore.Platform;
                var notifyWas = DesktopNotify.Platform;
                Speech.Platform = null;
                if (OperatingSystem.IsMacOS())
                    MacPlatform.Register();
                else
                    WindowsPlatform.Register();
                try
                {
                    SpeechEngine own = Speech.Platform == null ? null : Speech.Choose(null, null, out _);
                    if (own == null)
                        Console.WriteLine("SKIP: text to speech: this system has no voice installed, so its own engine can't be checked here");
                    else
                    {
                        string spokeError = null;
                        short[] ownSpeech = Array.Empty<short>();
                        System.Diagnostics.Stopwatch ownTook = System.Diagnostics.Stopwatch.StartNew();
                        try { ownSpeech = Speech.Synthesize(own, "Sidetone self test. The time is two twenty P M."); }
                        catch (Exception ex) { spokeError = ex.Message; }
                        double ownLevel = ownSpeech.Length > 0 ? WavFile.SpeechLevelDb(ownSpeech) : -120;
                        Check(spokeError == null && own.Kind == SpeechEngineKind.Platform && ownSpeech.Length > 8000 && ownLevel > VoiceProcessor.TargetDb - 4 && ownLevel < VoiceProcessor.TargetDb + 1,
                              $"text to speech: the system's own voice ({own.Label}) says a sentence, levelled like any announcement " +
                              $"({(spokeError ?? $"{ownSpeech.Length / 8000.0:F1} s of audio, speech {ownLevel:F1} dBFS, in {ownTook.ElapsedMilliseconds} ms")})");
                    }
                }
                finally
                {
                    Speech.Platform = speechWas;
                    SecretStore.Platform = secretsWere;
                    DesktopNotify.Platform = notifyWas;
                }
            }

            // the console: spoken announcements and the time, sent like WAV announcements
            string sset = Path.Combine(tmp, "speech-settings.json");
            List<(TonePlan Plan, List<ConsoleChannel> Targets)> sent = new List<(TonePlan, List<ConsoleChannel>)>();
            List<string> synthesized = new List<string>();
            DateTime clock = new DateTime(2026, 3, 2, 10, 7, 0, DateTimeKind.Utc);
            Tui NewSpeaker(TimeSpan limit, double seconds)
            {
                Tui t = new Tui(cp, sys, limit, null, settingsPath: sset) { NoAudio = true };
                t.sendAnnouncementForTest = (plan, targets) => sent.Add((plan, targets));
                t.synthesizeForTest = text => { synthesized.Add(text); return Enumerable.Range(0, (int)(seconds * 50)).SelectMany(VoicedFrame).ToArray(); };
                t.clockForTest = () => clock;
                return t;
            }
            Tui sp = NewSpeaker(TimeSpan.FromSeconds(120), 2);
            string blank = sp.AddSpokenAnnouncement("   "), added = sp.AddSpokenAnnouncement("Shift change in ten minutes"), tooLongText = sp.AddSpokenAnnouncement(new string('x', DispatchConsole.MaxSpokenLength + 1));
            sp.SaveSettings();
            string spokeWhy = sp.SpeakAnnouncement("Shift change in ten minutes", allListening: false);
            ConsoleChannel focusedChan = sp.Channels[sp.FocusIndex];
            bool spokeOnFocused = spokeWhy == null && sent.Count == 1 && sent[0].Plan.Name.Contains("spoken announcement") && sent[0].Targets.SequenceEqual(new[] { focusedChan }) &&
                                  sent[0].Plan.Frames.Length >= 100 && synthesized.Last() == "Shift change in ten minutes";
            int madeBefore = synthesized.Count;
            sp.SpeakAnnouncement("Shift change in ten minutes", allListening: false);
            TimeSpan? spokenFor = sp.SpokenLength("Shift change in ten minutes");
            bool cached = synthesized.Count == madeBefore && sent.Count == 2 && spokenFor == TimeSpan.FromSeconds(2);
            Tui sp2 = NewSpeaker(TimeSpan.FromSeconds(120), 2);
            Check(blank != null && added == null && tooLongText != null && sp2.SpokenAnnouncements.SequenceEqual(new[] { "Shift change in ten minutes" }) && spokeOnFocused && cached,
                  "spoken announcements: remembered, spoken by the engine once, and sent on the focused channel like a WAV announcement");

            ConsoleChannel listenA = sp.Channels.First(c => c.IsP25 && !c.Cfg.RxOnly);
            ConsoleChannel listenB = sp.Channels.Where(c => c.IsP25 && !c.Cfg.RxOnly).Skip(1).First();
            sp.Select(listenA);
            sp.Select(listenB);
            sent.Clear();
            string timeWhy = sp.PlayTimeAnnouncement(allListening: true);
            Check(timeWhy == null && sent.Count == 1 && sent[0].Targets.Contains(listenA) && sent[0].Targets.Contains(listenB) && sent[0].Plan.Name == "time announcement" &&
                  synthesized.Last().StartsWith("The time is ") && synthesized.Last().Contains(Speech.TimeWords(TimeZoneInfo.ConvertTimeFromUtc(clock, TimeZoneInfo.Local).Hour, 7, false)),
                  $"time announcement: played on every listening channel, saying the time now ({synthesized.Last()})");

            Tui shortLimit = NewSpeaker(TimeSpan.FromSeconds(1), 3);
            sent.Clear();
            string acceptedThenRefused = shortLimit.SpeakAnnouncement("a long speech", allListening: false);
            bool refusedLong = acceptedThenRefused == null && sent.Count == 0 && shortLimit.SnapshotLog(5).Any(l => l.Contains("longer than the 1 s transmit time limit"));
            Tui noHook = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
            noHook.synthesizeForTest = _ => new short[8000];
            string offline = noHook.SpeakAnnouncement("hello", false);
            Speech.programFoldersForTest = new[] { Path.Combine(tmp, "speech-nothing-here") };
            Speech.voiceFoldersForTest = new[] { Path.Combine(tmp, "speech-nothing-here") };
            Tui noEngine = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
            string noEngineWhy = noEngine.SpeakAnnouncement("hello", false);
            string statusLine = noEngine.SpeechStatus;
            Speech.programFoldersForTest = null;
            Speech.voiceFoldersForTest = null;
            Check(refusedLong && offline != null && offline.Contains("not connected") && noEngineWhy != null && noEngineWhy.Contains("no text-to-speech engine") &&
                  statusLine.StartsWith("Text to speech: no text-to-speech engine"),
                  "spoken announcements: refused (saying why) when too long for the time limit, not connected, or with no engine installed");

            // the speed: remembered, checked, and speech made again at the new speed
            int madeAtNormal = synthesized.Count;
            string slower = sp.SetSpeechRate(0.9), oddSpeed = sp.SetSpeechRate(0.5);
            sp.SpeakAnnouncement("Shift change in ten minutes", allListening: false);
            bool remade = synthesized.Count == madeAtNormal + 1;
            sp.SaveSettings();
            bool speedKept = NewSpeaker(TimeSpan.FromSeconds(120), 2).SpeechRate == 0.9;
            sp.SetSpeechRate(1.0);
            sp.SaveSettings();
            Check(slower == null && oddSpeed != null && sp.SpeechRate == 1.0 && remade && speedKept && Speech.RateName(0.9) == "Slower (90%)",
                  "text to speech: the speed is remembered, only the offered speeds are taken, and a spoken announcement is made again at a new speed");

            // the level: all announcements go out that much quieter (or louder), and Hear it follows it
            sent.Clear();
            sp.SpeakAnnouncement("Shift change in ten minutes", allListening: false);
            string quieter = sp.SetAnnouncementLevel(-6), oddLevel = sp.SetAnnouncementLevel(-5);
            sp.SpeakAnnouncement("Shift change in ten minutes", allListening: false);
            double Rms(TonePlan plan) { double sum = 0; long n = 0; foreach (short[] fr in plan.MonitorPcm()) foreach (short v in fr) { sum += (double)v * v; n++; } return 20 * Math.Log10(Math.Sqrt(sum / Math.Max(1, n)) / 32768.0); }
            double drop = sent.Count == 2 ? Rms(sent[0].Plan) - Rms(sent[1].Plan) : 0;
            sp.SaveSettings();
            bool levelKept = NewSpeaker(TimeSpan.FromSeconds(120), 2).AnnouncementLevel == -6;
            sp.SetAnnouncementLevel(0);
            sp.SaveSettings();
            Check(quieter == null && oddLevel != null && Math.Abs(drop - 6) < 0.2 && levelKept && sp.AnnouncementLevel == 0 && DispatchConsole.AnnouncementLevelName(-3) == "3 dB quieter",
                  $"announcements: the level setting sends them that much quieter ({drop:F1} dB at -6), is remembered, and takes only the offered steps");

            string badZone = sp.SetTimeAnnouncement(new TimeAnnouncementSettings { TimeZone = "Not/AZone" });
            string badEvery = sp.SetTimeAnnouncement(new TimeAnnouncementSettings { EveryMinutes = 7 });
            string goodTime = sp.SetTimeAnnouncement(new TimeAnnouncementSettings { Before = "CTRS time is", TimeZone = "UTC", Hour24 = true, SayZone = false, EveryMinutes = 15 });
            sp.SaveSettings();
            Tui reread = NewSpeaker(TimeSpan.FromSeconds(120), 2);
            Check(badZone != null && badZone.Contains("unknown time zone") && badEvery != null && goodTime == null &&
                  reread.TimeAnnouncement.Before == "CTRS time is" && reread.TimeAnnouncement.EveryMinutes == 15 && reread.TimeAnnouncement.Hour24 &&
                  File.ReadAllText(sset).Contains("CTRS time is"),
                  "time announcement: its settings are checked, then remembered for the system");

            // the schedule: on the clock, waiting for quiet channels, never twice for one slot
            sent.Clear();
            synthesized.Clear();
            clock = new DateTime(2026, 3, 2, 10, 7, 0, DateTimeKind.Utc);
            sp.Tick();
            bool notMidSlot = sent.Count == 0 && sp.NextTimeAnnouncement == new DateTime(2026, 3, 2, 10, 15, 0, DateTimeKind.Utc);
            clock = new DateTime(2026, 3, 2, 10, 15, 4, DateTimeKind.Utc);
            sp.Tick();
            sp.Tick();
            bool onTheQuarter = sent.Count == 1 && synthesized.SequenceEqual(new[] { "CTRS time is ten fifteen" }) && sent[0].Targets.Contains(listenA) && sent[0].Targets.Contains(listenB);
            clock = new DateTime(2026, 3, 2, 10, 30, 2, DateTimeKind.Utc);
            listenB.Active = true;
            listenB.ActiveLast = clock;     // frames keep coming (a busy channel with no frames for 3 s is taken as ended)
            sp.Tick();
            bool waitsForQuiet = sent.Count == 1;
            clock = new DateTime(2026, 3, 2, 10, 31, 30, DateTimeKind.Utc);
            listenB.Active = false;
            sp.Tick();
            bool thenSays = sent.Count == 2 && synthesized.Last() == "CTRS time is ten thirty one";
            clock = new DateTime(2026, 3, 2, 10, 45, 1, DateTimeKind.Utc);
            listenA.Active = true;
            listenA.ActiveLast = clock;
            sp.Tick();
            clock = new DateTime(2026, 3, 2, 10, 48, 30, DateTimeKind.Utc);
            listenA.ActiveLast = clock;     // a long call: still sending frames
            sp.Tick();
            listenA.Active = false;
            clock = new DateTime(2026, 3, 2, 10, 49, 0, DateTimeKind.Utc);
            sp.Tick();
            bool skippedAfterWait = sent.Count == 2 && sp.SnapshotLog(10).Any(l => l.Contains("time announcement skipped after waiting 3 minutes") && l.Contains($"{listenA.Cfg.Name} stayed busy"));
            Check(notMidSlot && onTheQuarter && waitsForQuiet && thenSays && skippedAfterWait,
                  "time announcement schedule: plays on the quarter hour (not mid-slot at start), waits while a channel is busy, and gives up after 3 minutes");

            ConsoleChannel notListening = listenB;
            sp.Deselect(listenB, quiet: true);
            sp.SetTimeAnnouncement(new TimeAnnouncementSettings { TimeZone = "UTC", EveryMinutes = 60, Channel = notListening.Cfg.Name });
            clock = new DateTime(2026, 3, 2, 10, 50, 0, DateTimeKind.Utc);
            sp.Tick();
            clock = new DateTime(2026, 3, 2, 11, 0, 3, DateTimeKind.Utc);
            sp.Tick();
            bool needsListening = sent.Count == 2 && sp.SnapshotLog(10).Any(l => l.Contains($"not listening to {notListening.Cfg.Name}"));
            sp.Select(notListening);
            sp.SetTimeAnnouncement(new TimeAnnouncementSettings { TimeZone = "UTC", EveryMinutes = 30, Channel = notListening.Cfg.Name });
            clock = new DateTime(2026, 3, 2, 11, 10, 0, DateTimeKind.Utc);
            sp.Tick();
            clock = new DateTime(2026, 3, 2, 11, 30, 1, DateTimeKind.Utc);
            sp.Tick();
            bool onlyThere = sent.Count == 3 && sent[2].Targets.SequenceEqual(new[] { notListening });
            Check(needsListening && onlyThere, "time announcement schedule: on one chosen channel, which has to be listening (so the console knows when it's quiet)");

            // turned on a few seconds after the hour (just after playing it by hand): nothing until the next hour
            sp.SetTimeAnnouncement(new TimeAnnouncementSettings { TimeZone = "UTC", EveryMinutes = 0 });
            clock = new DateTime(2026, 3, 2, 12, 0, 1, DateTimeKind.Utc);
            sp.PlayTimeAnnouncement(allListening: false);
            int byHand = sent.Count;
            clock = new DateTime(2026, 3, 2, 12, 0, 9, DateTimeKind.Utc);
            sp.SetTimeAnnouncement(new TimeAnnouncementSettings { TimeZone = "UTC", EveryMinutes = 60, Channel = notListening.Cfg.Name });
            sp.Tick();
            clock = new DateTime(2026, 3, 2, 12, 20, 0, DateTimeKind.Utc);
            sp.Tick();
            bool noRepeat = sent.Count == byHand;
            clock = new DateTime(2026, 3, 2, 13, 0, 2, DateTimeKind.Utc);
            sp.Tick();
            Check(noRepeat && sent.Count == byHand + 1,
                  "time announcement schedule: turned on just after the hour (after playing it by hand), it waits for the next hour instead of playing again");

            // Hear it: the words through the vocoder on the speakers, nothing sent
            List<short[]> previewed = new List<short[]>();
            sp.replaySinkForTest = () => new ListSink(previewed);
            sent.Clear();
            string heardWhy = sp.PreviewSpeech("Radio check");
            Check(heardWhy == null && sent.Count == 0 && previewed.Count == 100 && previewed.Any(f => f.Any(v => v != 0)),
                  $"hear it: a spoken announcement plays through the vocoder on the speakers, and nothing is sent ({previewed.Count} frames)");

            // the TUI's box: add a text, then the speech and time settings
            Tui tb = NewSpeaker(TimeSpan.FromSeconds(120), 2);
            tb.Render(120, 40);
            void Key(InputKey k, char c = '\0') => tb.HandleInput(new InputEvent(InputKind.Key, k, c));
            void Type(string text) { foreach (char c in text) Key(c == ' ' ? InputKey.Space : InputKey.Char, c); }
            Key(InputKey.Char, 'n');
            Key(InputKey.Char, 't');
            Type("Radio check");
            Key(InputKey.Enter);
            bool textAdded = tb.SpokenAnnouncements.Contains("Radio check");
            tb.Render(120, 40);
            string listShown = tb.ScreenText;
            Key(InputKey.Char, 'e');
            tb.Render(120, 40);
            string formShown = tb.ScreenText;
            if (Environment.GetEnvironmentVariable("SIDETONE_SHOW_TUI") == "1") Console.WriteLine(listShown + formShown);
            bool formOpen = tb.AnnounceFormOpen && listShown.Contains("\"Radio check\"") && listShown.Contains($"Time: {tb.TimeAnnouncementPreview}") &&
                            formShown.Contains("Announcement settings") && formShown.Contains("Same as the microphone") && formShown.Contains($"Will say: {tb.TimeAnnouncementPreview}");
            Key(InputKey.Left);                                    // Level: same as the microphone to 3 dB quieter
            Key(InputKey.Down); Key(InputKey.Down); Key(InputKey.Down);   // Speed
            Key(InputKey.Left);                                    // Normal to a little slower
            Key(InputKey.Down);                                    // Before the time
            for (int i = 0; i < 40; i++) Key(InputKey.Backspace);
            Type("Centrunk time is");
            Key(InputKey.Down); Key(InputKey.Down);                // Clock
            Key(InputKey.Right);                                   // 12-hour
            Key(InputKey.Down);                                    // Time zone
            for (int i = 0; i < 40; i++) Key(InputKey.Backspace);
            Type("Not/AZone");
            tb.Render(120, 40);
            Key(InputKey.Enter);
            bool zoneRefused = tb.AnnounceFormOpen && (tb.AnnounceFormError ?? string.Empty).Contains("Unknown time zone");
            for (int i = 0; i < 9; i++) Key(InputKey.Backspace);
            Type("America/Chicago");
            Key(InputKey.Enter);
            TimeAnnouncementSettings fromForm = tb.TimeAnnouncement;
            Key(InputKey.Escape);
            Check(textAdded && formOpen && zoneRefused && !tb.AnnounceFormOpen && !tb.AnnounceDialogOpen && fromForm.Before == "Centrunk time is" &&
                  !fromForm.Hour24 && fromForm.TimeZone == "America/Chicago" && tb.SpeechRate == 0.95 && tb.AnnouncementLevel == -3 && formShown.Contains("Speed"),
                  "TUI announcements box: t adds a text to say; e opens the speech and time settings (speed too), which refuse an unknown zone and save the rest");
        }

        private static void ListenKeyTests(string tmp)
        {
            string lekc = Path.Combine(tmp, "listen-keys.ekc");
            WriteEkc(lekc, "listen pass", new[] { (true, (int)P25Defines.P25_ALGO_AES, (int)TestKeyId, Convert.ToHexString(TestKey)) });
            Queue<string> answers = new Queue<string>(new[] { "wrong", "listen pass" });
            KeyStore lks = new KeyStore();
            LocalKeyFile opened = Program.OpenListenKeyFile(lekc, lks, _ => answers.Count > 0 ? answers.Dequeue() : string.Empty);
            bool unlocked = opened != null && lks.TryGet(P25Defines.P25_ALGO_AES, TestKeyId, out byte[] got) && got.SequenceEqual(TestKey) && lks.IsLocal(P25Defines.P25_ALGO_AES, TestKeyId);
            opened?.Dispose();
            KeyStore skipped = new KeyStore();
            LocalKeyFile none = Program.OpenListenKeyFile(lekc, skipped, _ => string.Empty);
            LocalKeyFile missing = Program.OpenListenKeyFile(Path.Combine(tmp, "no-such.ekc"), new KeyStore(), _ => "x");
            Check(unlocked && answers.Count == 0 && none == null && !skipped.TryGet(P25Defines.P25_ALGO_AES, TestKeyId, out _) && missing == null &&
                  Program.OpenListenKeyFile(null, new KeyStore(), _ => "x") == null,
                  "listen: a key file is unlocked (after a wrong try) and its keys are used; skipping or a missing file just means the FNE's keys");
        }

        private static void HistoryTests(string tmp, Codeplug cp, Codeplug.System sys)
        {
            string histDir = Path.Combine(tmp, "history"), histSettings = Path.Combine(tmp, "history-settings.json");
            Tui NewConsole()
            {
                Tui t = new Tui(cp, sys, TimeSpan.FromSeconds(120), null, settingsPath: histSettings) { NoAudio = true, HistoryFolder = histDir };
                t.Keys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey);
                return t;
            }
            void CallOn(Tui t, ConsoleChannel ch, int frames)
            {
                KeyStore talkerKeys = new KeyStore();
                talkerKeys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey);
                CaptureSink air = new CaptureSink(new CliPeer(sys, new KeyStore(), verbose: false));
                P25Transmitter talker = new P25Transmitter(air, talkerKeys, 4321, ch.Tg, ch.Encrypted ? P25Defines.P25_ALGO_AES : P25Defines.P25_ALGO_UNENCRYPT, ch.Encrypted ? TestKeyId : (ushort)0);
                talker.Start(out _);
                for (int i = 0; i < frames; i++) talker.PushAudio(VoicedFrame(i));
                uint stream = talker.StreamId;
                talker.Stop();
                foreach (var v in air.Voice)
                    ch.Rx.Handle(ch.Tg, 4321, (P25DUID)v.Frame[22], stream, v.Frame);
                ch.Rx.Handle(ch.Tg, 4321, P25DUID.TDU, stream, new byte[24]);
            }

            Tui h1 = NewConsole();
            ConsoleChannel hClear = h1.Channels.First(ch => ch.IsP25 && !ch.Encrypted), hSecure = h1.Channels.First(ch => ch.Encrypted);
            h1.Select(hClear);
            h1.Select(hSecure);
            CallOn(h1, hClear, 27);
            CallOn(h1, hSecure, 18);
            bool heard = h1.RecalledCalls(hClear).Count == 1 && h1.RecalledCalls(hSecure).Count == 1 && h1.SnapshotRecent(10).Count == 2;
            h1.SaveHistory();
            string histFile = h1.HistoryPath;
            string histJson = File.Exists(histFile) ? File.ReadAllText(histFile) : string.Empty;
            UnixFileMode histMode = File.Exists(histFile) && !OperatingSystem.IsWindows() ? File.GetUnixFileMode(histFile) : 0;
            Check(heard && histFile == Path.Combine(histDir, "Test_FNE.json") && histJson.Length > 0 &&
                  (histMode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0 && !Directory.GetFiles(histDir, "*.part").Any(),
                  "history: saved to one file per system, readable only by the user");

            Tui h2 = NewConsole();
            h2.LoadHistory();
            ConsoleChannel h2Clear = h2.Channels.First(ch => ch.IsP25 && !ch.Encrypted), h2Secure = h2.Channels.First(ch => ch.Encrypted);
            List<RecalledCall> back = h2.RecalledCalls(h2Clear);
            List<short[]> replayed = new List<short[]>();
            h2.replaySinkForTest = () => new ListSink(replayed);
            h2.Select(h2Clear);
            h2.ReplayLast(h2Clear);
            Check(back.Count == 1 && back[0].Length == h1.RecalledCalls(hClear)[0].Length && back[0].Who == h1.RecalledCalls(hClear)[0].Who &&
                  replayed.Count == 27 && h2.SnapshotRecent(10).Count == 2 && h2.SnapshotLog(10).Any(l => l.Contains("history: 2 recent calls and 1 calls")),
                  "history: after a restart, Recent calls and the kept calls come back, and a call from before replays in full");
            Check(h2.RecalledCalls(h2Secure).Count == 0 && h2.SnapshotRecent(10).Any(r => r.Chan.Contains($":{hSecure.Cfg.Name}:")),
                  "history: a secure call's audio isn't saved unless chosen, but its Recent calls entry is");

            h1.Settings.HistoryKeepSecure = true;
            h1.SaveHistory();
            Tui h3 = NewConsole();
            h3.LoadHistory();
            Check(h3.RecalledCalls(h3.Channels.First(ch => ch.Encrypted)).Count == 1, "history: with secure calls kept, their audio comes back too");

            // older entries show their date
            string dated = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(histFile), "\"When\":\"\\d{4}-\\d{2}-\\d{2}", "\"When\":\"2026-01-02");
            File.WriteAllText(histFile, dated);
            Tui h4 = NewConsole();
            h4.LoadHistory();
            Check(h4.SnapshotRecent(10).All(r => r.Time.StartsWith("01-02 ")), "history: Recent calls from an earlier day show the date");

            h1.Settings.KeepHistory = false;
            h1.HistorySettingsChanged();
            bool deleted = !File.Exists(histFile);
            Tui h5 = NewConsole();
            h5.Settings.KeepHistory = false;
            h5.LoadHistory();
            Check(deleted && h5.SnapshotRecent(10).Count == 0 && new Tui(cp, sys, TimeSpan.FromSeconds(120), null).HistoryPath == null,
                  "history: turning it off deletes the saved history; without a history folder nothing is kept");
        }

        private static void OutputTests(string tmp)
        {
            string zonePath = Path.Combine(tmp, "zones.yml");
            Codeplug ocp = Program.LoadCodeplug(zonePath);
            Codeplug.System osys = ocp.Systems[0];
            AudioDeviceList Devices(params string[] names)
            {
                AudioDeviceList l = new AudioDeviceList();
                uint id = 50;
                foreach (string n in names)
                    l.Outputs.Add(new AudioDevice { Id = id++, Name = n, Description = n + " (desc)" });
                return l;
            }
            FakeAudio outFake = new FakeAudio();
            IAudioBackend outReal = AudioBackend.Current;
            string outSettings = Path.Combine(tmp, "output-settings.json");
            try
            {
                AudioBackend.Current = outFake;
                Tui ot = new Tui(ocp, osys, TimeSpan.FromSeconds(120), null, settingsPath: outSettings) { NoAudio = false, deviceQueryForTest = () => Devices("dev.speakers", "dev.headset") };
                ConsoleZone alpha = ot.Zones[0];
                ConsoleChannel a1 = ot.Channels[0], a2 = ot.Channels[1], n1 = ot.Channels[2];
                ot.Select(a1);
                bool mainFirst = outFake.Targets.LastOrDefault() == null && ot.OutputSource(a1) == "main";
                ot.SetZoneOutput(alpha, "dev.headset");
                bool zoneMoved = outFake.Targets.LastOrDefault() == "dev.headset" && ot.OutputFor(a2) == "dev.headset" && ot.OutputFor(n1) == null && ot.OutputSource(a2) == "tab";
                ot.SetChannelOutput(a1, "dev.speakers");
                bool ownWins = outFake.Targets.LastOrDefault() == "dev.speakers" && ot.OutputSource(a1) == "own";
                int opened = outFake.Targets.Count;
                ot.SetZoneOutput(alpha, "dev.speakers");
                ot.SetZoneOutput(alpha, "dev.headset");
                bool ownStays = ot.OutputFor(a1) == "dev.speakers";
                ot.SetChannelOutput(a1, null);
                Check(mainFirst && zoneMoved && ownWins && ownStays && ot.OutputFor(a1) == "dev.headset" && outFake.Targets.LastOrDefault() == "dev.headset" &&
                      outFake.Outputs.Count(o => o != null) >= opened,
                      "outputs: a tab's output moves its listening channels at once; a channel's own output wins; clearing it follows the tab again");

                ot.SetChannelOutput(n1, "dev.speakers");
                ot.SaveSettings();
                string savedJson = File.ReadAllText(outSettings);
                Tui ot2 = new Tui(ocp, osys, TimeSpan.FromSeconds(120), null, settingsPath: outSettings) { NoAudio = true, deviceQueryForTest = () => Devices("dev.speakers") };
                ot2.RestoreSettings();
                Check(savedJson.Contains("\"ZoneOutputs\"") && savedJson.Contains("dev.headset") && ot2.Zones[0].Output == "dev.headset" && ot2.Channels[2].Output == "dev.speakers" &&
                      ot2.OutputFor(ot2.Channels[1]) == null && ot2.OutputFor(ot2.Channels[2]) == "dev.speakers" &&
                      ot2.SnapshotLog(20).Any(l => l.Contains("'dev.headset'") && l.Contains("isn't connected")),
                      "outputs: remembered per system; one that isn't connected at start is skipped with a warning (the main output plays) but stays saved");
                ot2.SaveSettings();
                Check(File.ReadAllText(outSettings).Contains("dev.headset"), "outputs: a missing output isn't forgotten when the settings are saved again");

                // the TUI's outputs box
                ot.Render(120, 40);
                ot.FocusTo(1);      // A2, on tab Alpha
                ot.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'o'));
                bool boxOpen = ot.OutputsDialogOpen;
                ot.HandleInput(new InputEvent(InputKind.Key, InputKey.Right));     // A2: own output, the first device
                string afterStep = a2.Output;
                ot.HandleInput(new InputEvent(InputKind.Key, InputKey.Left));      // back to "same as tab"
                ot.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 'm'));
                ot.HandleInput(new InputEvent(InputKind.Key, InputKey.Escape));
                Check(boxOpen && afterStep == "dev.speakers" && a2.Output == null && !ot.OutputsDialogOpen && !a2.Muted,
                      "TUI outputs box: o opens it on the focused channel, ←/→ step through the outputs, Esc closes it");
                ot.Shutdown();
            }
            finally
            {
                AudioBackend.Current = outReal;
            }
        }

        private const string StreamCodeplug = @"
systems:
  - name: ""Test FNE""
    identity: ""TEST CLI""
    address: ""127.0.0.1""
    port: 62031
    peerId: 9000001
    rid: ""9000001""
    password: ""PASSWORD""
zones:
  - name: ""Alpha""
    channels:
      - { name: ""A1"", system: ""Test FNE"", tgid: ""601"", mode: ""p25"" }
    web_streams:
      - { name: ""Live"", url: ""http://127.0.0.1:PORT/live?token=feedtoken"", authUsername: ""streamuser"", authPassword: ""streampass"", idleColor: ""#150282"" }
      - { name: ""NoAuth"", url: ""http://127.0.0.1:PORT/live"" }
      - { name: ""Aac"", url: ""http://127.0.0.1:PORT/aac"" }
      - { name: ""Page"", url: ""http://127.0.0.1:PORT/page"" }
      - { name: ""Zeros"", url: ""http://127.0.0.1:PORT/zeros"" }
      - { name: ""Down"", url: ""http://127.0.0.1:DOWN/live"" }
      - { name: ""Stall"", url: ""http://127.0.0.1:PORT/stall"" }
  - name: ""Bravo""
    channels:
      - { name: ""B1"", system: ""Test FNE"", tgid: ""621"", mode: ""p25"" }
    web_streams:
      - { name: ""Bravo feed"", url: ""http://127.0.0.1:PORT/live"", authUsername: ""streamuser"", authPassword: ""streampass"" }
  - name: ""Streams only""
    web_streams:
      - { name: ""Orphan"", url: ""http://127.0.0.1:PORT/live"" }
";

        /// <summary>Collects what a web stream plays, with the stream's state at each block.</summary>
        private sealed class StreamSink : IAudioOutput
        {
            public readonly List<short> Samples = new List<short>();
            public readonly HashSet<StreamState> States = new HashSet<StreamState>();
            public Func<StreamState> State;
            public event Action<string> Failed { add { } remove { } }
            public void Write(short[] samples)
            {
                lock (Samples)
                {
                    Samples.AddRange(samples);
                    if (State != null)
                        States.Add(State());
                }
            }
            public int Count { get { lock (Samples) return Samples.Count; } }
            public void Dispose() { }
        }

        private static int FreePort()
        {
            System.Net.Sockets.TcpListener l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static bool WaitFor(Func<bool> done, double seconds = 10)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done())
            {
                if (DateTime.UtcNow > until)
                    return false;
                Thread.Sleep(10);
            }
            return true;
        }

        private static void StreamTests(string tmp)
        {
            byte[] mp3;
            using (Stream res = typeof(SelfTest).Assembly.GetManifestResourceStream("voice.mp3"))
            using (MemoryStream ms = new MemoryStream())
            {
                res.CopyTo(ms);
                mp3 = ms.ToArray();
            }

            // the local server: /live wants Basic auth and sends the file 3 times, then closes
            int port = FreePort(), down = FreePort();
            string expectedAuth = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("streamuser:streampass"));
            List<string> auths = new List<string>();
            int liveConnections = 0, stallConnections = 0;
            ManualResetEventSlim stallRelease = new ManualResetEventSlim();
            System.Net.HttpListener server = new System.Net.HttpListener();
            server.Prefixes.Add($"http://127.0.0.1:{port}/");
            server.Start();
            Thread serving = new Thread(() =>
            {
                while (server.IsListening)
                {
                    System.Net.HttpListenerContext ctx;
                    try { ctx = server.GetContext(); }
                    catch { return; }
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            System.Net.HttpListenerResponse r = ctx.Response;
                            string path = ctx.Request.Url.AbsolutePath;
                            if (path == "/live")
                            {
                                string auth = ctx.Request.Headers["Authorization"];
                                lock (auths)
                                    auths.Add(auth);
                                if (auth != expectedAuth)
                                {
                                    r.StatusCode = 401;
                                    r.Close();
                                    return;
                                }
                                Interlocked.Increment(ref liveConnections);
                                r.ContentType = "audio/mpeg";
                                r.SendChunked = true;
                                for (int i = 0; i < 3; i++)
                                    r.OutputStream.Write(mp3, 0, mp3.Length);
                            }
                            else if (path == "/aac")
                            {
                                r.ContentType = "audio/aac";
                                r.OutputStream.Write(new byte[100]);
                            }
                            else if (path == "/page")
                            {
                                r.ContentType = "text/html";
                                r.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes("<html>sign in</html>"));
                            }
                            else if (path == "/stall")
                            {
                                // some audio, then nothing on a connection that stays open (a half-dead link)
                                Interlocked.Increment(ref stallConnections);
                                r.ContentType = "audio/mpeg";
                                r.SendChunked = true;
                                r.OutputStream.Write(mp3, 0, mp3.Length);
                                r.OutputStream.Flush();
                                stallRelease.Wait(TimeSpan.FromSeconds(30));
                            }
                            else if (path == "/zeros")
                            {
                                r.ContentType = "audio/mpeg";
                                r.OutputStream.Write(new byte[WebStreamPlayer.SyncLimit * 2]);
                            }
                            else
                                r.StatusCode = 404;
                            r.Close();
                        }
                        catch { /* the player hung up */ }
                    });
                }
            }) { IsBackground = true };
            serving.Start();

            TimeSpan retryWas = WebStreamPlayer.RetryDelay;
            WebStreamPlayer.RetryDelay = TimeSpan.FromMilliseconds(50);
            string cpPath = Path.Combine(tmp, "streams.yml");
            File.WriteAllText(cpPath, StreamCodeplug.Replace("PORT", port.ToString()).Replace("DOWN", down.ToString()));
            string stSettings = Path.Combine(tmp, "stream-settings.json");
            try
            {
                Codeplug scp = Program.LoadCodeplug(cpPath);
                Dictionary<string, StreamSink> sinks = new Dictionary<string, StreamSink>();
                DispatchConsole sc = new DispatchConsole(scp, scp.Systems[0], TimeSpan.FromSeconds(120), null, null, stSettings) { NoAudio = true };
                sc.streamOutputForTest = s =>
                {
                    StreamSink k = new StreamSink { State = () => s.Player?.State ?? StreamState.Off };
                    lock (sinks)
                        sinks[s.Name] = k;
                    return k;
                };
                ConsoleStream S(string name) => sc.Streams.First(x => x.Name == name);
                StreamSink Sink(string name) { lock (sinks) return sinks.TryGetValue(name, out StreamSink k) ? k : null; }

                Check(string.Join(",", sc.Streams.Select(x => x.Name)) == "Live,NoAuth,Aac,Page,Zeros,Down,Stall,Bravo feed" &&
                      sc.StreamsIn(sc.Zones[0]).Count == 7 && sc.StreamsIn(sc.Zones[1]).Single().Name == "Bravo feed" &&
                      sc.Streams.All(x => x.State == StreamState.Off && !x.On && x.StateText == "Off") &&
                      S("Live").IdleColor == "#150282" && S("Live").Host == "127.0.0.1",
                      "web streams: read per tab from the codeplug, all off at start; a zone with no channels (no tab) is left out");

                // playing: decoded, mixed to mono and resampled to 8 kHz, with the activity shown
                ConsoleStream live = S("Live");
                sc.ToggleStream(live);
                bool playedLoop = WaitFor(() => Sink("Live")?.Count >= 3 * 32000 - 4000);
                StreamSink ls = Sink("Live");
                short[] got;
                lock (ls.Samples)
                    got = ls.Samples.Take(32000).ToArray();
                double Rms(IEnumerable<short> xs) { double sum = 0; int n = 0; foreach (short x in xs) { sum += (double)x * x; n++; } return n == 0 ? 0 : Math.Sqrt(sum / n); }
                double toneRms = Rms(got.Skip(4000).Take(8000)), quietRms = Rms(got.Skip(20000).Take(8000));
                Check(playedLoop && toneRms > 3000 && quietRms < 50,
                      $"web streams: an MP3 stream is played as 8 kHz mono (tone {toneRms:F0} rms, then silence {quietRms:F0}; {ls.Count} samples)");
                Check(ls.States.Contains(StreamState.Receiving) && ls.States.Contains(StreamState.Idle),
                      "web streams: RX while there's audio, Idle again after 1.4 s of silence (audio time)");
                bool authOk;
                lock (auths)
                    authOk = auths.Count > 0 && auths.All(a => a == expectedAuth);
                Check(authOk, "web streams: the codeplug's user name and password are sent as HTTP Basic auth");

                // the server closing a stream that was playing: it connects again
                bool again = WaitFor(() => Volatile.Read(ref liveConnections) >= 2);
                Check(again && live.On && string.Join("\n", sc.SnapshotLog(40)).Contains("the stream ended; trying again"),
                      "web streams: a stream that drops while playing connects again by itself");

                // Mute all silences it (it keeps playing)
                sc.ToggleGlobalMute();
                int mutedFrom = ls.Count + 1600;
                bool mutedRan = WaitFor(() => ls.Count > mutedFrom + 16000);
                short[] after;
                lock (ls.Samples)
                    after = ls.Samples.Skip(mutedFrom).ToArray();
                Check(mutedRan && after.All(x => x == 0) && live.On, "web streams: Mute all silences them");
                sc.ToggleGlobalMute();

                // volume, saved by name
                sc.StreamLouder(live);
                sc.StreamLouder(live);
                double louder = live.Volume;
                sc.SaveSettings();

                // hiding its tab stops it; turning it off stops it
                ConsoleStream bravo = S("Bravo feed");
                sc.ToggleStream(bravo);
                WaitFor(() => Sink("Bravo feed")?.Count > 8000);
                sc.SetZoneHidden(sc.Zones[1], true);
                bool hiddenOff = !bravo.On && bravo.State == StreamState.Off;
                sc.ToggleStream(bravo);
                Check(hiddenOff && !bravo.On && string.Join("\n", sc.SnapshotLog(10)).Contains("zone Bravo is hidden"),
                      "web streams: hiding a tab stops its streams, and they can't start while it's hidden");
                sc.SetZoneHidden(sc.Zones[1], false);
                sc.ToggleStream(live);
                Check(!live.On && live.State == StreamState.Off, "web streams: tapping a playing stream stops it");

                // refusals fail at once with a plain reason; a server that isn't there gets 3 attempts
                foreach (string n in new[] { "NoAuth", "Aac", "Page", "Zeros", "Down" })
                    sc.ToggleStream(S(n));
                bool allFailed = WaitFor(() => new[] { "NoAuth", "Aac", "Page", "Zeros", "Down" }.All(n => S(n).State == StreamState.Failed));
                string Why(string n) => S(n).Reason ?? "(none)";
                Check(allFailed && Why("NoAuth").Contains("needs a user name and password (401)") && Why("Aac").Contains("sends audio/aac; Sidetone plays MP3 streams only") &&
                      Why("Page").Contains("web page") && Why("Zeros").Contains("isn't MP3") && Why("Down").Contains("(3 attempts)") &&
                      S("Down").StateText == "Failed" && !S("Down").On,
                      $"web streams: refused streams fail with a plain reason, an unreachable one after 3 attempts ({Why("NoAuth")}; {Why("Aac")}; {Why("Page")}; {Why("Zeros")}; {Why("Down")})");
                // a stream that stops sending without closing (a phone changing networks): given up on, and connected again
                TimeSpan readWas = WebStreamPlayer.QuietTimeout;
                WebStreamPlayer.QuietTimeout = TimeSpan.FromMilliseconds(500);
                ConsoleStream stall = S("Stall");
                sc.ToggleStream(stall);
                bool reconnected = WaitFor(() => Volatile.Read(ref stallConnections) >= 2);
                bool stallLogged = string.Join("\n", sc.SnapshotLog(40)).Contains("stream Stall (127.0.0.1): no answer (timed out); trying again");
                sc.ToggleStream(stall);
                WebStreamPlayer.QuietTimeout = readWas;
                stallRelease.Set();
                Check(reconnected && stallLogged && !stall.On,
                      $"web streams: a stream that goes silent on an open connection times out and connects again ({stallConnections} connections, logged {stallLogged})");

                string streamLog = string.Join("\n", sc.SnapshotLog(200));
                Check(streamLog.Contains("stream Live (127.0.0.1)") && !streamLog.Contains("streampass") && !streamLog.Contains("streamuser") &&
                      !streamLog.Contains("feedtoken") && !streamLog.Contains("/live"),
                      "web streams: the log names a stream and its host only, never its path, token, user name or password");
                sc.Shutdown();

                // the terminal console's box (s): Enter starts the stream, + raises its volume, Esc closes it
                Tui stui = new Tui(scp, scp.Systems[0], TimeSpan.FromSeconds(120), null) { NoAudio = true };
                stui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, 's'));
                bool boxOpen = stui.StreamsDialogOpen;
                string boxText = string.Join("\n", Enumerable.Range(0, 40).Select(y => stui.Render(120, 40).RowText(y)));
                stui.HandleInput(new InputEvent(InputKind.Key, InputKey.Char, '+'));
                stui.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));
                bool tuiOn = stui.Streams[0].On;
                string titleText = stui.Render(200, 40).RowText(1);
                stui.HandleInput(new InputEvent(InputKind.Key, InputKey.Enter));
                stui.HandleInput(new InputEvent(InputKind.Key, InputKey.Escape));
                Check(boxOpen && boxText.Contains("Web streams: Alpha") && boxText.Contains("Live") && boxText.Contains("127.0.0.1") &&
                      tuiOn && titleText.Contains("streams: 1 on") && stui.Streams[0].Volume > 1.2 && !stui.Streams[0].On && !stui.StreamsDialogOpen,
                      $"web streams: the TUI's s box lists the tab's streams; Enter plays and stops, + is louder, Esc closes (box {boxOpen}, listed {boxText.Contains("Live")}, played {tuiOn}, title {titleText.Contains("streams: 1 on")}: {titleText.Trim()})");
                stui.Shutdown();

                DispatchConsole sc2 = new DispatchConsole(scp, scp.Systems[0], TimeSpan.FromSeconds(120), null, null, stSettings) { NoAudio = true };
                sc2.RestoreSettings();
                Check(Math.Abs(sc2.Streams.First(x => x.Name == "Live").Volume - louder) < 0.001 && louder > 1.5 &&
                      sc2.Streams.First(x => x.Name == "Bravo feed").Volume == 1.0 && !sc2.Streams.Any(x => x.On),
                      "web streams: volumes are saved by stream name; streams start off again next time");
            }
            finally
            {
                WebStreamPlayer.RetryDelay = retryWas;
                stallRelease.Set();
                server.Stop();
            }

            // resampling in pieces matches resampling in one go (no clicks at the edges)
            float[] sweep = Enumerable.Range(0, 22050).Select(i => (float)Math.Sin(2 * Math.PI * (100 + i / 40.0) * i / 22050.0) * 0.5f).ToArray();
            float[] whole = WavFile.Resample(sweep, 22050, 8000);
            WavFile.StreamResampler rs = new WavFile.StreamResampler(22050, 8000);
            List<float> pieces = new List<float>();
            for (int i = 0; i < sweep.Length; i += 1000)
                pieces.AddRange(rs.Push(sweep.AsSpan(i, Math.Min(1000, sweep.Length - i))));
            double worst = 0;
            for (int i = 200; i < Math.Min(pieces.Count, whole.Length) - 200; i++)
                worst = Math.Max(worst, Math.Abs(pieces[i] - whole[i]));
            Check(pieces.Count > whole.Length - 100 && worst < 1e-4, $"web streams: resampling in pieces matches resampling in one go (worst {worst:E1})");
        }

        private const string MultiCodeplug = @"
systems:
  - { name: ""Main FNE"", identity: ""MAIN"", address: ""127.0.0.1"", port: 62031, peerId: 9000001, rid: ""9000001"", password: ""PASSWORD"" }
  - { name: ""DMR FNE"", identity: ""DMR"", address: ""127.0.0.1"", port: 62041, peerId: 9000002, rid: ""9000002"", password: ""PASSWORD"" }
  - { name: ""Spare FNE"", identity: ""SPARE"", address: ""127.0.0.1"", port: 62051, peerId: 9000003, rid: ""9000003"", password: ""PASSWORD"" }
zones:
  - name: ""Alpha""
    channels:
      - { name: ""Main 601"", system: ""Main FNE"", tgid: ""601"", mode: ""p25"" }
      - { name: ""Other 601"", system: ""DMR FNE"", tgid: ""601"", mode: ""p25"" }
      - { name: ""No system"", tgid: ""602"", mode: ""p25"" }
  - name: ""Bravo""
    channels:
      - { name: ""Other secure"", system: ""DMR FNE"", tgid: ""700"", mode: ""p25"", algo: ""aes"", keyId: ""50"" }
";

        private static void MultiSystemTests(string tmp)
        {
            string mPath = Path.Combine(tmp, "multi.yml");
            File.WriteAllText(mPath, MultiCodeplug);
            Codeplug mcp = Program.LoadCodeplug(mPath);
            string mSettings = Path.Combine(tmp, "multi-settings.json");

            // which systems connect: every one with channels, unless --system names one or Settings turned some off
            AppSettings none = new AppSettings();
            List<string> mNotes = new List<string>();
            string Names(List<Codeplug.System> l) => string.Join(",", l.Select(x => x.Name));
            string all = Names(CodeplugLoader.SystemsToConnect(mcp, none, null, mNotes));
            string only = Names(CodeplugLoader.SystemsToConnect(mcp, none, "dmr fne"));
            AppSettings offOne = new AppSettings();
            offOne.For("Main FNE").Off = true;
            List<string> offNotes = new List<string>();
            string withOff = Names(CodeplugLoader.SystemsToConnect(mcp, offOne, null, offNotes));
            AppSettings offAll = new AppSettings();
            offAll.For("Main FNE").Off = true;
            offAll.For("DMR FNE").Off = true;
            List<string> allOffNotes = new List<string>();
            string allOff = Names(CodeplugLoader.SystemsToConnect(mcp, offAll, null, allOffNotes));
            Check(all == "Main FNE,DMR FNE" && mNotes.Count == 0 && only == "DMR FNE" && withOff == "DMR FNE" && offNotes.Any(n => n.Contains("turned off in Settings: Main FNE")) &&
                  allOff == "Main FNE" && allOffNotes.Any(n => n.Contains("connects anyway")),
                  $"systems: every system with channels connects (one without channels doesn't), --system picks one, Settings can turn one off, and one always stays ({all}; {withOff}; {allOff})");

            DispatchConsole mc = new DispatchConsole(mcp, CodeplugLoader.SystemsToConnect(mcp, none, null), TimeSpan.FromSeconds(120), null, null, mSettings) { NoAudio = true };
            FneLink main = mc.Links[0], other = mc.Links[1];
            ConsoleChannel m601 = mc.Channels.First(c => c.Cfg.Name == "Main 601"), o601 = mc.Channels.First(c => c.Cfg.Name == "Other 601"),
                           noSys = mc.Channels.First(c => c.Cfg.Name == "No system"), oSecure = mc.Channels.First(c => c.Cfg.Name == "Other secure");
            Check(mc.MultiSystem && mc.Links.Count == 2 && m601.Link == main && o601.Link == other && noSys.Link == main && oSecure.Link == other &&
                  mc.SystemConfig.Name == "Main FNE",
                  "systems: one console has every system's channels; a channel without a system is on the first one");

            // the same talkgroup on two FNEs is two different channels
            mc.OnFrame(other, new fnecore.P25DataReceivedEvent(1, 3012, 601, fnecore.CallType.GROUP, P25DUID.LDU1, fnecore.FrameType.VOICE, 0, 91, new byte[300]));
            bool onlyOther = o601.Active && !m601.Active;
            mc.OnFrame(other, new fnecore.P25DataReceivedEvent(1, 3012, 601, fnecore.CallType.GROUP, P25DUID.TDU, fnecore.FrameType.TERMINATOR, 0, 91, new byte[300]));
            mc.OnFrame(new fnecore.P25DataReceivedEvent(1, 3013, 601, fnecore.CallType.GROUP, P25DUID.LDU1, fnecore.FrameType.VOICE, 0, 92, new byte[300]));
            Check(onlyOther && m601.Active && !o601.Active, "systems: a call on one FNE's TG 601 shows only on that system's channel, not the same TG on another");

            // keys and radio IDs are per system
            other.Keys.Store(P25Defines.P25_ALGO_AES, 0x50, TestKey);
            main.RidForTest = 9000001;
            other.RidForTest = 9000002;
            Check(mc.HasKey(oSecure) && !main.Keys.TryGet(P25Defines.P25_ALGO_AES, 0x50, out _) && m601.Link.ConsoleRid == 9000001 && o601.Link.ConsoleRid == 9000002,
                  "systems: each has its own keys and its own console radio ID");

            // radio commands and digital pages go to the focused channel's system
            mc.SetFocus(mc.chans.IndexOf(o601));
            string refused = mc.SendRadioCommand(0, 1234);
            Check(mc.CommandLink == other && refused == "not connected to DMR FNE yet", $"systems: a radio command goes to the focused channel's system ({refused})");

            // a page to this console on one system is named as that system's
            List<string> pagedBodies = new List<string>();
            mc.Notifications = true;
            mc.notifyForTest = (title, body) => pagedBodies.Add(body);
            mc.OnP25Control(other, P25Defines.TSBK_IOSP_CALL_ALRT, 5555, 9000002, null);
            mc.OnP25Control(other, P25Defines.TSBK_IOSP_CALL_ALRT, 5556, 9000001, null);     // the other system's RID: not this console here
            Check(pagedBodies.Count == 1 && pagedBodies[0].Contains("(DMR FNE,"), "systems: a call alert to the console's RID on one system is that system's page");

            // tones go out on each channel's own system, as its own RID
            CliPeer hdr = new CliPeer(mcp.Systems[0], new KeyStore(), verbose: false);
            CaptureSink mainOut = new CaptureSink(hdr), otherOut = new CaptureSink(hdr);
            main.SinkForTest = mainOut;
            other.SinkForTest = otherOut;
            mc.Select(m601);
            mc.Select(o601);
            mc.SendAlert(1);
            bool bothSent = WaitFor(() => Volatile.Read(ref mainOut.TduCount) > 0 && Volatile.Read(ref otherOut.TduCount) > 0, 5);
            mc.CancelTones(wait: true);
            Check(bothSent && mainOut.Tdus[0].Src == 9000001 && otherOut.Tdus[0].Src == 9000002 && mainOut.Tdus.All(t => t.Dst == 601) && otherOut.Tdus.All(t => t.Dst == 601),
                  "systems: an alert tone goes out on each listening channel's own FNE, as the console's RID there");

            // a patch across systems: sent on the other FNE as the console's RID there
            int mainBefore = mainOut.Tdus.Count, otherBefore = otherOut.Tdus.Count;
            mc.patchManualPump = true;
            mc.SetPatch(m601, 1);
            mc.SetPatch(o601, 1);
            CaptureSink air = new CaptureSink(hdr);
            P25Transmitter radio = new P25Transmitter(air, new KeyStore(), 4321, 601, P25Defines.P25_ALGO_UNENCRYPT, 0);
            radio.Start(out _);
            for (int i = 0; i < 36; i++) radio.PushAudio(VoicedFrame(i));
            uint radioStream = radio.StreamId;      // (Stop clears it)
            radio.Stop();
            foreach (var v in air.Voice)
                m601.Rx.Handle(601, 4321, (P25DUID)v.Frame[22], v.Stream, v.Frame);
            m601.Rx.Handle(601, 4321, P25DUID.TDU, radioStream, new byte[24]);
            for (int ms = 0; ms <= 30000; ms += 10)
                mc.PatchPump(TimeSpan.FromMilliseconds(ms));     // the frames are stamped by the patch's own clock, seconds in by now
            List<(uint Src, uint Dst, uint Stream, bool Grant)> patched = otherOut.Tdus.Skip(otherBefore).ToList();
            Check(patched.Count > 1 && patched[0].Grant && !patched[^1].Grant && patched.All(t => t.Src == 9000002 && t.Dst == 601) && mainOut.Tdus.Count == mainBefore && otherOut.Voice.Count > 0,
                  $"systems: a patch across systems sends the call on the other FNE, as the console's RID there ({patched.Count} TDUs)");
            mc.SetPatch(m601, 0);
            mc.SetPatch(o601, 0);

            // log lines from a system are named when there are several
            Check(mc.Tagged(other, "logged in") == "DMR FNE: logged in", "systems: a system's log lines are named when there are several");

            // channel settings are kept under their own system
            mc.Deselect(m601);
            mc.SaveSettings();
            AppSettings savedMulti = AppSettings.Load(mSettings, out _);
            bool underOwn = savedMulti.For("DMR FNE").Channels.Values.Count(x => x.Listening) == 1 && savedMulti.For("Main FNE").Channels.Values.All(x => !x.Listening);
            DispatchConsole mc2 = new DispatchConsole(mcp, CodeplugLoader.SystemsToConnect(mcp, savedMulti, null), TimeSpan.FromSeconds(120), null, null, mSettings) { NoAudio = true };
            mc2.RestoreSettings();
            ConsoleChannel o601Again = mc2.Channels.First(c => c.Cfg.Name == "Other 601");
            Check(underOwn && o601Again.Selected && !mc2.Channels.First(c => c.Cfg.Name == "Main 601").Selected && mc2.Channels[mc2.FocusIndex] == o601Again,
                  "systems: each channel's settings are kept under its own system, and the focus comes back to the right one");
            mc.Shutdown();
            mc2.Shutdown();

            // call history: a channel of the same name on two FNEs keeps its own calls after a restart (filed as SYSTEM:CHANNEL)
            {
                string twinPath = Path.Combine(tmp, "twins.yml");
                File.WriteAllText(twinPath, MultiCodeplug.Replace(@"  - name: ""Bravo""", @"  - name: ""Twins""
    channels:
      - { name: ""Dispatch"", system: ""Main FNE"", tgid: ""610"", mode: ""p25"" }
      - { name: ""Dispatch"", system: ""DMR FNE"", tgid: ""610"", mode: ""p25"" }
  - name: ""Bravo"""));
                Codeplug tcp = Program.LoadCodeplug(twinPath);
                string twinHist = Path.Combine(tmp, "twin-history"), twinSettings = Path.Combine(tmp, "twin-settings.json");
                DispatchConsole NewTwins() => new DispatchConsole(tcp, CodeplugLoader.SystemsToConnect(tcp, new AppSettings(), null), TimeSpan.FromSeconds(120), null, null, twinSettings)
                                              { NoAudio = true, HistoryFolder = twinHist };
                DispatchConsole t1 = NewTwins();
                ConsoleChannel dMain = t1.Channels.First(c => c.Cfg.Name == "Dispatch" && c.Link == t1.Links[0]), dOther = t1.Channels.First(c => c.Cfg.Name == "Dispatch" && c.Link == t1.Links[1]);
                t1.AddDemoCall(dMain, 1111, "Medic 1", 1, false, "clear", DateTime.Now.AddMinutes(-2));
                t1.AddDemoCall(dOther, 2222, "2222", 1, false, "clear", DateTime.Now.AddMinutes(-1));
                t1.SaveHistory();
                t1.Shutdown();
                DispatchConsole t2 = NewTwins();
                t2.LoadHistory();
                List<RecalledCall> backMain = t2.RecalledCalls(t2.Channels.First(c => c.Cfg.Name == "Dispatch" && c.Link == t2.Links[0]));
                List<RecalledCall> backOther = t2.RecalledCalls(t2.Channels.First(c => c.Cfg.Name == "Dispatch" && c.Link == t2.Links[1]));
                Check(backMain.Count == 1 && backMain[0].Source == 1111 && backMain[0].Label == "Main FNE:Dispatch:1111/Medic 1" &&
                      backOther.Count == 1 && backOther[0].Source == 2222 && backOther[0].Label == "DMR FNE:Dispatch:2222",
                      $"history: a channel of the same name on two FNEs keeps its own calls after a restart ({backMain.Count}: {backMain.FirstOrDefault()?.Label}; {backOther.Count}: {backOther.FirstOrDefault()?.Label})");
                t2.Shutdown();

                // a history file from before 0.36.2 (version 1: "zone/name/tg", the first system's only) still loads
                DispatchConsole t3 = NewTwins();
                string frames = Convert.ToBase64String(new byte[P25VoiceReceiver.ImbeLength * 50]);
                File.WriteAllText(t3.HistoryPath, "{\"Version\":1,\"Recent\":[],\"Calls\":[{\"Key\":\"Twins/Dispatch/610\",\"Channel\":\"Dispatch\",\"Talkgroup\":610," +
                                                  "\"Source\":3333,\"Who\":\"3333\",\"Started\":\"2026-11-20T08:00:00\",\"Tx\":false,\"Crypt\":\"clear\",\"Cut\":false,\"Frames\":\"" + frames + "\"}]}");
                t3.LoadHistory();
                List<RecalledCall> v1Main = t3.RecalledCalls(t3.Channels.First(c => c.Cfg.Name == "Dispatch" && c.Link == t3.Links[0]));
                List<RecalledCall> v1Other = t3.RecalledCalls(t3.Channels.First(c => c.Cfg.Name == "Dispatch" && c.Link == t3.Links[1]));
                Check(v1Main.Count == 1 && v1Main[0].Source == 3333 && v1Other.Count == 0,
                      $"history: a file from before 0.36.2 still loads, onto the first system's channel ({v1Main.Count}, {v1Other.Count})");
                t3.Shutdown();
            }

            // the terminal console's title shows each system with its own dot
            Tui mt = new Tui(mcp, CodeplugLoader.SystemsToConnect(mcp, none, null), TimeSpan.FromSeconds(120), null) { NoAudio = true };
            string mTitle = mt.Render(200, 40).RowText(1);
            Check(mTitle.Contains("Main FNE ○") && mTitle.Contains("DMR FNE ○") && !mTitle.Contains("connecting…") && !mTitle.Contains("peer 9000001"),
                  $"systems: the TUI's title shows each system and whether it's connected ({mTitle.Trim()})");
            mt.Shutdown();

            // one system: no names added anywhere
            DispatchConsole single = new DispatchConsole(mcp, mcp.Systems[0], TimeSpan.FromSeconds(120), null) { NoAudio = true };
            Check(!single.MultiSystem && single.Channels.All(c => c.Link == single.Links[0]) && single.Tagged(single.Links[0], "logged in") == "logged in" &&
                  single.Channels.Count == 2,
                  "systems: a console for one system has only its channels, and nothing is named");
        }

        private static void OneWayPatchTests(Codeplug cp, Codeplug.System sys)
        {
            const uint owRid = 9000001, owRadio = 4321;
            Tui ow = new Tui(cp, sys, TimeSpan.FromSeconds(120), null) { NoAudio = true };
            CliPeer owHeaders = new CliPeer(sys, new KeyStore(), verbose: false);
            CaptureSink owOut = new CaptureSink(owHeaders);
            ow.sinkForTest = owOut;
            ow.consoleRidForTest = owRid;
            ow.patchManualPump = true;
            ow.Keys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey);
            ConsoleChannel calling = ow.Channels.First(ch => ch.IsP25 && !ch.Encrypted), secure = ow.Channels.First(ch => ch.Encrypted);
            ow.SetPatch(calling, 1);
            ow.SetPatch(secure, 1);
            double owClock = 0;

            // a call on a channel, played into the console; returns the talkgroups the patch sent it to
            List<uint> CallOn(ConsoleChannel ch)
            {
                KeyStore talkerKeys = new KeyStore();
                talkerKeys.Store(P25Defines.P25_ALGO_AES, TestKeyId, TestKey);
                CaptureSink air = new CaptureSink(owHeaders);
                P25Transmitter talker = new P25Transmitter(air, talkerKeys, owRadio, ch.Tg, ch.Encrypted ? P25Defines.P25_ALGO_AES : P25Defines.P25_ALGO_UNENCRYPT, ch.Encrypted ? TestKeyId : (ushort)0);
                talker.Start(out _);
                for (int i = 0; i < 27; i++) talker.PushAudio(VoicedFrame(i));
                uint stream = talker.StreamId;
                talker.Stop();
                int before = owOut.Tdus.Count;
                foreach (var v in air.Voice)
                    ch.Rx.Handle(ch.Tg, owRadio, (P25DUID)v.Frame[22], stream, v.Frame);
                ch.Rx.Handle(ch.Tg, owRadio, P25DUID.TDU, stream, new byte[24]);
                for (int n = 0; n < 300; n++)
                    ow.PatchPump(TimeSpan.FromMilliseconds(owClock += 10));
                return owOut.Tdus.Skip(before).Where(t => t.Grant).Select(t => t.Dst).ToList();
            }

            List<uint> twoWayA = CallOn(calling), twoWayB = CallOn(secure);
            Check(twoWayA.SequenceEqual(new[] { secure.Tg }) && twoWayB.SequenceEqual(new[] { calling.Tg }) && calling.PatchRole == PatchRole.Both,
                  "one-way patch: a patch starts both ways, each member's calls going to the other");

            ow.SetPatchRole(secure, PatchRole.TargetOnly);
            string described = string.Join("\n", ow.SnapshotLog(3));
            List<uint> oneWayA = CallOn(calling), oneWayB = CallOn(secure);
            Check(oneWayA.SequenceEqual(new[] { secure.Tg }) && oneWayB.Count == 0 && DispatchConsole.PatchMark(secure) == "←" &&
                  described.Contains($"one-way, {calling.Cfg.Name} → {secure.Cfg.Name}"),
                  "one-way patch: a target only member gets the patch's calls, but its own aren't passed on (and the log says one-way)");

            CaptureSink owMain = new CaptureSink(owHeaders);
            P25Transmitter owTx = new P25Transmitter(owMain, ow.Keys, owRid, secure.Tg, P25Defines.P25_ALGO_AES, TestKeyId);
            owTx.Start(out _);
            int tdusBefore = owOut.Tdus.Count;
            ow.StartPatchTx(secure, owTx);
            for (int i = 0; i < 18; i++) owTx.PushAudio(VoicedFrame(i));
            ow.StopPatchTx();
            owTx.Stop();
            Check(owOut.Tdus.Count == tdusBefore, "one-way patch: the console transmitting on a target only member stays on that channel");

            ow.SetPatchRole(secure, PatchRole.Both);
            ow.SetPatchRole(calling, PatchRole.SourceOnly);
            List<uint> srcA = CallOn(calling), srcB = CallOn(secure);
            Check(srcA.SequenceEqual(new[] { secure.Tg }) && srcB.Count == 0 && DispatchConsole.PatchMark(calling) == "→",
                  "one-way patch: a source only member's calls go out, and nothing is sent on it");

            ow.CyclePatchRole(calling);
            bool cycled = calling.PatchRole == PatchRole.TargetOnly;
            ow.SetPatch(calling, 0);
            ow.SetPatchRole(calling, PatchRole.SourceOnly);
            Check(cycled && calling.PatchRole == PatchRole.Both && calling.Patch == 0 && ow.SnapshotLog(1).Any(l => l.Contains("not in a patch")),
                  "one-way patch: the direction cycles both ways → source → target, resets on leaving the patch, and needs a patch");
            ow.ClearPatches();
        }

        private static void RecordingPruneTests(string tmp)
        {
            // the Recordings list (Android's share sheet): names written by RecordingPath, read back newest first
            {
                string listRoot = Path.Combine(tmp, "list-recordings");
                string Make(string system, RecalledCall call, string format = DispatchConsole.FormatOpus)
                {
                    string path = DispatchConsole.RecordingPath(listRoot, system, call, format);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, "x");
                    return path;
                }
                DateTime d1 = new DateTime(2026, 11, 21, 23, 12, 5), d2 = new DateTime(2026, 11, 22, 17, 38, 40);
                string a = Make("CTRS", new RecalledCall { Channel = "Fire_Dispatch 2", Talkgroup = 301, Source = 7, Who = "Engine 7", Started = d1 });
                string b = Make("CTRS", new RecalledCall { Channel = "Tac 1", Talkgroup = 101, Source = 9000001, Who = "9000001", Started = d2, Tx = true }, DispatchConsole.FormatWav);
                string c = Make("CTRS", new RecalledCall { Channel = "Tac 1", Talkgroup = 101, Source = 9000001, Who = "9000001", Started = d2, Tx = true }, DispatchConsole.FormatWav);  // same second: -2
                string other = Make("Other FNE", new RecalledCall { Channel = "Link", Talkgroup = 100, Source = 12, Who = "Unit 12", Started = d2.AddMinutes(-30) });
                // one named the way before 0.36.2 still lists
                string oldDay = Path.Combine(listRoot, "Old_FNE", "2026-11-20");
                Directory.CreateDirectory(oldDay);
                string old = Path.Combine(oldDay, "081500_Fire_Dispatch_TG301_RX_Engine_7.opus");
                File.WriteAllText(old, "x");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(b), "unfinished.opus.part"), "x");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(b), "notes.txt"), "x");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(b), "random_name.opus"), "x");
                List<RecordingFile> listed = Recordings.List(listRoot);
                RecordingFile first = listed.FirstOrDefault(), fire = listed.FirstOrDefault(r => r.Path == a), was = listed.LastOrDefault();
                Check(listed.Count == 5 && listed.Select(r => r.Path).Take(2).OrderBy(x => x).SequenceEqual(new[] { b, c }.OrderBy(x => x)) && listed[2].Path == other && listed[3].Path == a &&
                      Path.GetFileName(a) == "231205_CTRS_Fire-Dispatch-2_7_Engine-7.opus" && Path.GetFileName(c) == "173840_CTRS_Tac-1_9000001-2.wav" &&
                      first.Started == d2 && first.RadioId == 9000001 && first.Who == null && first.Channel == "Tac-1" && first.System == "CTRS" && first.Label == "CTRS:Tac-1:9000001" &&
                      fire != null && fire.Channel == "Fire-Dispatch-2" && fire.Who == "Engine-7" && fire.RadioId == 7 && fire.Started == d1 && fire.Label == "CTRS:Fire-Dispatch-2:7/Engine-7" &&
                      listed[2].System == "Other-FNE" && listed[2].Who == "Unit-12" &&
                      was.Path == old && was.Talkgroup == 301 && !was.Tx && was.Channel == "Fire_Dispatch" && was.Who == "Engine_7" && was.System == "Old_FNE" &&
                      Recordings.List(Path.Combine(tmp, "no-such-folder")).Count == 0,
                      $"recordings list: read back from their names (TIME_SYSTEM_CHANNEL_RADIOID_ALIAS, and the old way), newest first, across systems and days; unfinished and other files left out ({listed.Count}: {string.Join(", ", listed.Select(r => Path.GetFileName(r.Path)))})");
            }

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
            string todays = LogFiles.FileFor(liveLogs, DateTime.UtcNow);
            string written = File.Exists(todays) ? File.ReadAllText(todays) : string.Empty;
            string fixedFile = Path.Combine(tmp, "fixed.log");
            Tui fixedLog = new Tui(cp, sys, TimeSpan.FromSeconds(120), fixedFile) { NoAudio = true, LogFolder = liveLogs };
            fixedLog.AddLog("goes to the --log file");
            Check(written.Contains("a line for the log file") && written.StartsWith(DateTime.UtcNow.ToString("yyyy-MM-dd ")) &&
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
            Check(StartupChoices.KeyFile(cp, null, chosen) == Path.GetFullPath("/keys/saved.ekc") && StartupChoices.KeyFile(cp, "/keys/option.ekc", chosen) == Path.GetFullPath("/keys/option.ekc") &&
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
            public readonly List<string> Targets = new List<string>();     // the device each output was opened on
            public IAudioOutput OpenOutput(string device) { Out o = new Out(); lock (Outputs) { Outputs.Add(o); Targets.Add(device); } return o; }
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
