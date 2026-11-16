// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* Usage:
*   sidetone [--codeplug FILE] [--system NAME] [--theme system|light|dark] [--wayland]
*          [--output DEVICE] [--input DEVICE] [--ptt-device NAME [--ptt-button B] [--mute-button B|none]]
*          [--socket PATH | --no-socket] [--tx-limit SECONDS] [--log FILE]
*          [--settings FILE | --no-settings] [--space hold|toggle]
*   sidetone --demo                      a made-up system, nothing is connected
*   sidetone --screenshot FILE.png       render the window off-screen and exit (with --demo, --theme, --size WxH,
*                                      --show page|digital|radio)
*   sidetone --selfcheck                 press the keyboard shortcuts on an off-screen window and check what they do
*/

using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

// Linux only, like the core
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Sidetone.Gui
{
    /// <summary>
    /// Command line options.
    /// </summary>
    public sealed class GuiOptions
    {
        public string Codeplug, SystemName, Output, Input, PttDevice, PttButton, MuteButton, Socket, LogFile, Theme, Screenshot, SettingsFile, Show, KeyFile;
        public bool NoSocket, Wayland, Demo, NoSettings, SelfCheck, NoNotify, NoProcessing;
        public string RecordDir;
        /// <summary>Given on the command line; null leaves it to the saved settings.</summary>
        public bool? SpaceToggle;
        public int? TxLimit;
        public double? Scale;
        public int Width = 1280, Height = 800;

        public static GuiOptions Parse(string[] args)
        {
            GuiOptions o = new GuiOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--codeplug": o.Codeplug = Next(); break;
                    case "--system": o.SystemName = Next(); break;
                    case "--output": o.Output = Next(); break;
                    case "--input": o.Input = Next(); break;
                    case "--ptt-device": o.PttDevice = Next(); break;
                    case "--key-file": o.KeyFile = Next(); break;
                    case "--record-dir": o.RecordDir = Next(); break;
                    case "--no-notify": o.NoNotify = true; break;
                    case "--no-processing": o.NoProcessing = true; break;
                    case "--scale":
                    {
                        string text = Next().TrimEnd('%');
                        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale))
                            throw new ArgumentException("--scale must be a number, like 2 or 1.5 (or a percentage, like 150%)");
                        if (scale > DisplayScale.Max)
                            scale /= 100;       // 150 means 150%
                        if (scale < DisplayScale.Min || scale > DisplayScale.Max)
                            throw new ArgumentException("--scale must be 0.5 to 4 (50% to 400%)");
                        o.Scale = scale;
                        break;
                    }
                    case "--ptt-button": o.PttButton = Next(); break;
                    case "--mute-button": o.MuteButton = Next(); break;
                    case "--socket": o.Socket = Next(); break;
                    case "--no-socket": o.NoSocket = true; break;
                    case "--log": o.LogFile = Next(); break;
                    case "--settings": o.SettingsFile = Next(); break;
                    case "--no-settings": o.NoSettings = true; break;
                    case "--wayland": o.Wayland = true; break;
                    case "--demo": o.Demo = true; break;
                    case "--screenshot": o.Screenshot = Next(); break;
                    case "--selfcheck": o.SelfCheck = true; break;
                    case "--show":
                        o.Show = Next().ToLowerInvariant();
                        if (o.Show is not ("page" or "digital" or "radio" or "settings" or "keyfile" or "patch" or "recall"))
                            throw new ArgumentException("--show must be page, digital, radio, settings, keyfile, patch or recall");
                        break;
                    case "--space":
                    {
                        string mode = Next().ToLowerInvariant();
                        if (mode is not ("hold" or "toggle"))
                            throw new ArgumentException("--space must be hold or toggle");
                        o.SpaceToggle = mode == "toggle";
                        break;
                    }
                    case "--theme":
                        o.Theme = Next().ToLowerInvariant();
                        if (o.Theme is not ("system" or "light" or "dark"))
                            throw new ArgumentException("--theme must be system, light or dark");
                        break;
                    case "--tx-limit":
                    {
                        if (!int.TryParse(Next(), out int limit) || limit < SettingsOverlay.MinTxLimit || limit > SettingsOverlay.MaxTxLimit)
                            throw new ArgumentException("--tx-limit must be 5-600 seconds");
                        o.TxLimit = limit;
                        break;
                    }
                    case "--size":
                    {
                        string[] wh = Next().ToLowerInvariant().Split('x');
                        if (wh.Length != 2 || !int.TryParse(wh[0], out o.Width) || !int.TryParse(wh[1], out o.Height) ||
                            o.Width < 400 || o.Height < 300 || o.Width > 4000 || o.Height > 3000)
                            throw new ArgumentException("--size must look like 1280x800");
                        break;
                    }
                    default:
                        throw new ArgumentException($"unknown option '{args[i]}'");
                }
            }

            if (o.PttDevice == null && (o.PttButton != null || o.MuteButton != null))
                throw new ArgumentException("--ptt-button and --mute-button need --ptt-device");
            return o;
        }
    } // public sealed class GuiOptions

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--version" || args[0] == "version"))
            {
                Console.WriteLine($"sidetone {Assembly.GetExecutingAssembly().GetName().Version}");
                return 0;
            }
            if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h" || args[0] == "help"))
            {
                PrintUsage();
                return 0;
            }

            if (args.Length == 1 && (args[0] == "--install-launcher" || args[0] == "--remove-launcher"))
                return LauncherCommand(args[0] == "--install-launcher");

            GuiOptions opt;
            try
            {
                opt = GuiOptions.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                PrintUsage();
                return 2;
            }
            App.Options = opt;

            if (opt.SelfCheck)
                return SelfCheck(opt);
            if (opt.Screenshot != null)
                return Screenshot(opt);

            // high-DPI screens: decided before the window system starts
            string settingsFile = opt.NoSettings || opt.Demo ? null : opt.SettingsFile ?? AppSettings.DefaultPath;
            DisplayScale.Apply(opt.Scale, AppSettings.Load(settingsFile, out _).UiScale, opt.Wayland);

            if (opt.Wayland && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                Console.Error.WriteLine("error: --wayland needs a Wayland session (WAYLAND_DISPLAY isn't set)");
                return 2;
            }

            return BuildAvaloniaApp(opt).StartWithClassicDesktopLifetime(Array.Empty<string>());
        }

        /// <summary>
        /// The desktop app: X11 (XWayland on Wayland desktops) by default, Avalonia's native Wayland backend with --wayland.
        /// </summary>
        public static AppBuilder BuildAvaloniaApp(GuiOptions opt)
        {
            AppBuilder builder = AppBuilder.Configure<App>().UsePlatformDetect();
            if (opt.Wayland)
                builder = builder.UseWayland();
            return builder.WithInterFont().LogToTrace();
        }

        /// <summary>
        /// Renders the main window off-screen to a PNG and exits. No display is needed.
        /// </summary>
        private static void SetupHeadless() =>
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHarfBuzz()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

        /// <summary>sidetone --install-launcher / --remove-launcher: the app launcher entry, without opening a window.</summary>
        private static int LauncherCommand(bool install)
        {
            try
            {
                if (install)
                {
                    string entry = AppLauncher.Install(AppLauncher.DataHome, Environment.ProcessPath);
                    Console.WriteLine($"sidetone: added to the app launcher ({entry})");
                }
                else
                {
                    Console.WriteLine(AppLauncher.Remove(AppLauncher.DataHome) ? "sidetone: removed from the app launcher" : "sidetone: it wasn't in the app launcher");
                }
                return 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        private static int Screenshot(GuiOptions opt)
        {
            SetupHeadless();

            MainWindow window = new MainWindow(opt);
            window.Show();
            if (opt.Show != null)
                window.OpenBox(opt.Show);
            window.RefreshNow();
            Dispatcher.UIThread.RunJobs();

            using var frame = window.CaptureRenderedFrame();
            if (frame == null)
            {
                Console.Error.WriteLine("error: nothing was rendered");
                return 1;
            }
            string path = Path.GetFullPath(opt.Screenshot);
#pragma warning disable CS0618 // the simple PNG overload is all this needs
            frame.Save(path);
#pragma warning restore CS0618
            Console.WriteLine($"sidetone: wrote {path} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
            window.CloseConsole();
            return 0;
        }

        /// <summary>
        /// Presses the keyboard shortcuts on an off-screen window showing the demo system and checks
        /// that each one reaches the console. Run by CI; needs no display and connects to nothing.
        /// </summary>
        private static int SelfCheck(GuiOptions opt)
        {
            opt.Demo = true;
            SetupHeadless();

            // never hang a CI job: give up after half a minute
            new Thread(() =>
            {
                Thread.Sleep(30000);
                Console.Error.WriteLine("error: the self-check didn't finish in 30 s");
                Environment.Exit(3);
            }) { IsBackground = true }.Start();

            MainWindow w = new MainWindow(opt);
            bool closed = false;
            w.Closed += (s, e) => closed = true;
            w.Show();
            Dispatcher.UIThread.RunJobs();
            DispatchConsole c = w.ActiveConsole;

            int failures = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {what}");
                if (!ok)
                    failures++;
            }
            void Press(PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None)
            {
                w.KeyPressQwerty(key, mods);
                w.KeyReleaseQwerty(key, mods);
                Dispatcher.UIThread.RunJobs();
            }

            Console.WriteLine("sidetone keyboard self-check");
            Check(c != null && c.FocusIndex == 1, "the demo console is up with the second card focused");
            if (c == null)
                return 1;
            ConsoleChannel focus = c.Channels[1];

            Press(PhysicalKey.ArrowRight);
            bool right = c.FocusIndex == 2;
            Press(PhysicalKey.ArrowLeft);
            Check(right && c.FocusIndex == 1, "Right and Left move the focus between cards");

            bool was = focus.Selected;
            Press(PhysicalKey.Enter);
            bool flipped = focus.Selected != was;
            Press(PhysicalKey.Enter);
            Check(flipped && focus.Selected == was, "Enter starts and stops listening on the focused card");

            Press(PhysicalKey.M);
            bool muted = focus.Muted;
            Press(PhysicalKey.M);
            Check(muted && !focus.Muted, "M mutes and unmutes the focused channel");

            Press(PhysicalKey.M, RawInputModifiers.Shift);
            bool all = c.GlobalMute;
            Press(PhysicalKey.M, RawInputModifiers.Shift);
            Check(all && !c.GlobalMute, "Shift+M mutes and unmutes everything");

            Press(PhysicalKey.U);
            bool mic = c.MicMuted;
            Press(PhysicalKey.U);
            Check(mic && !c.MicMuted, "U mutes and unmutes the microphone");

            double gain = focus.Gain;
            Press(PhysicalKey.Equal);
            bool louder = focus.Gain > gain;
            Press(PhysicalKey.Minus);
            Check(louder && Math.Abs(focus.Gain - gain) < 0.001, "+ and - change the focused channel's volume");

            // Space is hold-to-talk. A held key can arrive as repeated presses, or (X11) as release/press pairs.
            w.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            w.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            w.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            w.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Check(w.SpacePresses == 1 && w.SpaceReleases == 0, $"holding Space is one press, however the repeats arrive ({w.SpacePresses} / {w.SpaceReleases})");
            Check(c.SnapshotLog(5).Any(l => l.Contains("not connected to the FNE yet")), "Space reaches the console's push-to-talk (refused here: the demo isn't connected)");

            // the release is decided by a timer, so run the real event loop for a moment
            w.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            using (CancellationTokenSource stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
                Dispatcher.UIThread.MainLoop(stop.Token);
            Check(w.SpacePresses == 1 && w.SpaceReleases == 1, $"letting go of Space releases once, shortly after ({w.SpacePresses} / {w.SpaceReleases})");

            // tones, the marker, and the page and radio command boxes
            Press(PhysicalKey.C);
            bool marker = c.MarkerOn;
            Press(PhysicalKey.C);
            Check(marker && !c.MarkerOn, "C turns the channel marker on and off");

            Press(PhysicalKey.Digit1);
            Check(c.SnapshotLog(1).Any(l => l.Contains("not connected to the FNE yet")), "1 asks the console for alert tone 1 (refused here: not connected)");

            CommandOverlay box = w.Overlay;
            Press(PhysicalKey.R);
            Check(box.Open == CommandOverlay.Kind.Radio, "R opens the radio command box");
            w.KeyTextInput("1234");
            Dispatcher.UIThread.RunJobs();
            Check(box.RadioRid.Text == "1234", $"typing goes into the radio ID field ('{box.RadioRid.Text}')");
            Press(PhysicalKey.M);
            Press(PhysicalKey.Space);
            Check(!focus.Muted && w.SpacePresses == 1 && box.Open == CommandOverlay.Kind.Radio, "while a box is open, shortcut keys don't act on the console");

            box.SelectRadioAction(1);
            Press(PhysicalKey.Enter);
            bool asked = box.Open == CommandOverlay.Kind.Radio && box.Message.Contains("again to inhibit 1234");
            Press(PhysicalKey.Enter);
            Check(asked && box.Open == CommandOverlay.Kind.Radio && box.Message.Contains("ot connected"),
                  $"inhibit asks to be confirmed, and a command that can't be sent keeps the box open and says why ('{box.Message}')");
            Press(PhysicalKey.Escape);
            Check(box.Open == CommandOverlay.Kind.None, "Esc closes the box");

            Press(PhysicalKey.P);
            bool pageOpen = box.Open == CommandOverlay.Kind.Page && !box.Digital;
            w.KeyTextInput("100");
            Dispatcher.UIThread.RunJobs();
            Press(PhysicalKey.Enter);       // tone B is empty: Enter moves on to it
            w.KeyTextInput("1000");
            Dispatcher.UIThread.RunJobs();
            Press(PhysicalKey.Enter);
            Check(pageOpen && box.ToneA.Text == "100" && box.ToneB.Text == "1000" && box.Message.Contains("one A must be"),
                  $"P opens the two-tone page box; Enter goes from tone A to tone B; a tone out of range is refused ('{box.ToneA.Text}' / '{box.ToneB.Text}' / '{box.Message}')");
            Press(PhysicalKey.Escape);

            Press(PhysicalKey.D);
            bool digitalOpen = box.Open == CommandOverlay.Kind.Page && box.Digital;
            Press(PhysicalKey.Enter);
            Check(digitalOpen && box.Message.Contains("radio ID"), "D opens the digital page box, which needs a radio ID");
            Press(PhysicalKey.Escape);
            Check(box.Open == CommandOverlay.Kind.None, "the boxes are closed again");

            // on-screen PTT: hold-to-talk with the pointer
            int refusals = c.SnapshotLog(200).Count(l => l.Contains("not connected to the FNE yet"));
            Point onPtt = w.PttTap.Root.TranslatePoint(new Point(10, 10), w) ?? new Point(-1, -1);
            w.MouseDown(onPtt, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            bool pressed = w.PointerPresses == 1 && w.PointerReleases == 0 &&
                           c.SnapshotLog(200).Count(l => l.Contains("not connected to the FNE yet")) == refusals + 1;
            w.MouseUp(onPtt, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check(pressed && w.PointerReleases == 1, $"pressing the PTT button keys the console and letting go releases it ({w.PointerPresses} / {w.PointerReleases})");

            // selectable encryption: E on a channel the codeplug allows it on
            Press(PhysicalKey.E);
            bool clearChannel = !focus.TxClear && c.SnapshotLog(1).Any(l => l.Contains("no encryption to switch"));
            Press(PhysicalKey.ArrowLeft);
            ConsoleChannel secure = c.Channels[0];
            Press(PhysicalKey.E);
            bool nowClear = secure.Selectable && secure.TxClear && !secure.TxSecure && secure.ActiveTxAlg == 0x80;
            Press(PhysicalKey.E);
            Press(PhysicalKey.ArrowRight);
            Check(clearChannel && nowClear && secure.TxSecure && c.FocusIndex == 1,
                  "E switches a selectable channel's transmit between secure and clear, and does nothing on a clear channel");

            // patches: X moves the focused channel through them
            Press(PhysicalKey.X);
            bool inFirst = focus.Patch == 1 && c.PatchName(focus) == "Patch 1" && c.PatchNames.Count == 3;
            Press(PhysicalKey.X);
            Press(PhysicalKey.X);
            bool inThird = focus.Patch == 3;
            Press(PhysicalKey.X);
            Check(inFirst && inThird && focus.Patch == 0 && c.PatchName(focus) == null, "X moves the focused channel through the patches and back to none");

            // instant recall: the demo has calls on Tac 1, the focused channel
            Press(PhysicalKey.L, RawInputModifiers.Shift);
            RecallOverlay recall = w.RecallBox;
            bool listed = recall.IsOpen && recall.Channel == c.Channels[1] && recall.Count == 3;
            recall.PlayRow(0);
            bool playing = c.Replaying != null && c.ReplayChannel == c.Channels[1] && c.Replaying.Who == "Unit 12";
            Press(PhysicalKey.L);
            bool stopped = c.Replaying == null;
            Press(PhysicalKey.Escape);
            Check(listed && playing && stopped && !recall.IsOpen,
                  $"Shift+L lists the focused channel's calls, Play replays one and L stops it ({recall.Count} calls)");
            Press(PhysicalKey.L);
            bool again = c.Replaying?.Who == "Unit 12";
            Press(PhysicalKey.L);
            Check(again && c.Replaying == null, "L replays the focused channel's last call, and stops it");
            bool recBefore = focus.Record;
            Press(PhysicalKey.W);
            bool recAfter = focus.Record;
            Press(PhysicalKey.W);
            Check(recAfter != recBefore && focus.Record == recBefore, "W turns recording on the focused channel on and off");

            // key file password box
            w.OpenBox("keyfile");
            Dispatcher.UIThread.RunJobs();
            Press(PhysicalKey.Enter);
            bool needsPassword = w.KeyBox.IsOpen && w.KeyBox.Message.Contains("password");
            Press(PhysicalKey.Escape);
            Check(needsPassword && !w.KeyBox.IsOpen, "the key file box wants a password; Esc is 'not now'");

            // display scale
            const string monitors = "[{\"name\":\"eDP-1\",\"scale\":2.44,\"focused\":true},{\"name\":\"DP-1\",\"scale\":1.0,\"focused\":false}]";
            Check(DisplayScale.FromHyprland("{\"option\":\"xwayland:force_zero_scaling\",\"int\":1,\"set\":true}", monitors) == 2.44 &&
                  DisplayScale.FromHyprland("{\"option\":\"xwayland:force_zero_scaling\",\"bool\":true}", monitors) == 2.44 &&
                  DisplayScale.FromHyprland("{\"option\":\"xwayland:force_zero_scaling\",\"int\":0,\"set\":false}", monitors) == null &&
                  DisplayScale.FromHyprland("{\"int\":1}", "[{\"scale\":1.0,\"focused\":true}]") == null &&
                  DisplayScale.FromHyprland("not json", monitors) == null && DisplayScale.FromHyprland(null, null) == null,
                  "display scale: taken from Hyprland's focused monitor, only when it leaves XWayland windows unscaled");

            // Settings
            SettingsOverlay set = w.SettingsBox;
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            Check(set.IsOpen && set.Output.ItemCount == 3 && set.Input.ItemCount == 3 && set.PttDevice.ItemCount == 3,
                  $"Ctrl+, opens Settings with the audio and PTT devices listed ({set.Output.ItemCount} / {set.Input.ItemCount} / {set.PttDevice.ItemCount})");
            Check(!set.PttButton.IsEnabled && set.PttButton.ItemCount == 1, "with no PTT device chosen, there are no buttons to choose");
            set.PttDevice.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Check(set.PttButton.IsEnabled && set.PttButton.ItemCount == 3 && set.MuteButton.ItemCount == 4,
                  $"choosing a PTT device lists its buttons ({set.PttButton.ItemCount} / {set.MuteButton.ItemCount})");
            Press(PhysicalKey.M);
            Check(!focus.Muted && set.IsOpen, "while Settings is open, shortcut keys don't act on the console");
            set.TxLimit.Text = "3";
            set.Save();
            Check(set.IsOpen && set.Message.Contains("time limit"), "a time limit that's too short is refused, and says so");
            set.TxLimit.Text = "90";
            set.Output.SelectedIndex = 2;
            Press(PhysicalKey.Escape);
            Check(!set.IsOpen && w.TxLimitSeconds == 120, "Esc closes Settings without saving");

            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            set.TxLimit.Text = "90";
            set.Save();
            Check(!set.IsOpen && w.TxLimitSeconds == 90, "Save keeps the new time limit");
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            bool defaults = set.Processing == (true, true, false, true, false);
            set.FlipProcessing(2);
            set.FlipProcessing(4);
            set.Save();
            VoiceProcessing mic = c.MicProcessing;
            Check(defaults && !set.IsOpen && mic.NoiseGate && mic.LowCut && mic.AutoLevel && mic.Limiter && c.RxLeveling &&
                  c.Channels.All(ch => ch.RxLevel != null) && c.SnapshotLog(3).Any(l => l.Contains("audio processing: microphone low cut, noise gate, auto level, limiter; receive leveling on")),
                  "mic processing starts at its defaults; turning on the gate and receive leveling applies at once");
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            bool automatic = set.Scale.SelectedIndex == 0 && w.SavedScale == null;
            set.Scale.SelectedIndex = 5;        // 200%
            set.Save();
            Check(automatic && w.SavedScale == 2.0 && c.SnapshotLog(1).Any(l => l.Contains("next time Sidetone starts")),
                  "the display scale is automatic until one is chosen; a new one is saved for the next start");
            bool holdBefore = !w.SpaceIsToggle;
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            Check(set.IsOpen && set.TxLimit.Text == "90", "Settings reopens on what was saved");
            Press(PhysicalKey.Escape);

            // the app launcher entry, in a scratch folder
            string data = Path.Combine(Path.GetTempPath(), $"sidetone-launcher-{Environment.ProcessId}");
            try
            {
                string entry = AppLauncher.Install(data, "/opt/side tone/sidetone");
                string text = File.ReadAllText(entry);
                Check(AppLauncher.IsInstalled(data) && text.Contains("Exec=\"/opt/side tone/sidetone\"") && text.Contains("Icon=sidetone") && text.Contains("Name=Sidetone") &&
                      new FileInfo(Path.Combine(data, "icons", "hicolor", "scalable", "apps", "sidetone.svg")).Length > 200 &&
                      new FileInfo(Path.Combine(data, "icons", "hicolor", "256x256", "apps", "sidetone.png")).Length > 1000,
                      "launcher: the entry and both icons are written, with the program path quoted");
                bool same = AppLauncher.Refresh(data, "/opt/side tone/sidetone");
                bool moved = AppLauncher.Refresh(data, "/opt/sidetone-next/sidetone");
                Check(!same && moved && File.ReadAllText(entry).Contains("Exec=\"/opt/sidetone-next/sidetone\""),
                      "launcher: an entry follows the program to a new folder, and is left alone otherwise");
                File.WriteAllText(entry, "[Desktop Entry]\nName=Mine\n");
                Check(!AppLauncher.Refresh(data, "/opt/elsewhere/sidetone"), "launcher: an entry someone else wrote is left alone");
                Check(AppLauncher.Remove(data) && !AppLauncher.IsInstalled(data) && !AppLauncher.Remove(data), "launcher: removing takes the entry and icons away");
                Check(AppLauncher.QuoteExec("/a/50% \"x\" $y") == "\"/a/50%% \\\\\"x\\\\\" \\\\$y\"", "launcher: reserved characters in the path are escaped");
            }
            finally
            {
                try { Directory.Delete(data, true); } catch (Exception) { }
            }
            Check(holdBefore, "Space is hold-to-talk unless Settings or --space says otherwise");

            w.KeyPressQwerty(PhysicalKey.Q, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Check(closed, "Ctrl+Q closes the window");

            Console.WriteLine(failures == 0 ? "ALL GUI CHECKS PASSED" : $"{failures} GUI CHECK(S) FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("sidetone - Sidetone, an IP dispatch console for DVMProject FNE networks (graphical)");
            Console.WriteLine();
            Console.WriteLine("  sidetone [--codeplug FILE] [--system NAME] [--theme system|light|dark] [--wayland]");
            Console.WriteLine("         [--output DEVICE] [--input DEVICE]");
            Console.WriteLine("         [--ptt-device NAME [--ptt-button BUTTON] [--mute-button BUTTON|none]]");
            Console.WriteLine("         [--socket PATH | --no-socket] [--tx-limit SECONDS] [--log FILE]");
            Console.WriteLine("         [--settings FILE | --no-settings] [--space hold|toggle]");
            Console.WriteLine("                  [--scale FACTOR] [--key-file FILE.ekc] [--record-dir DIR] [--no-notify] [--no-processing]");
            Console.WriteLine("  sidetone --install-launcher | --remove-launcher");
            Console.WriteLine("  sidetone --demo");
            Console.WriteLine("  sidetone --screenshot FILE.png [--demo] [--theme light|dark] [--size 1280x800] [--show page|digital|radio|settings|keyfile|patch|recall]");
            Console.WriteLine();
            Console.WriteLine("Without --codeplug, the codeplug opened last time is used. --demo shows a made-up system");
            Console.WriteLine("without connecting to anything. The options are the same as 'sidetone-cli tui'; see the README.");
            Console.WriteLine("On Wayland desktops the window runs through XWayland unless you pass --wayland.");
            Console.WriteLine();
            Console.WriteLine("Keys: Space push-to-talk (hold; --space toggle makes it start/stop), Esc stop transmitting,");
            Console.WriteLine("arrows move between cards, Enter listen, M mute channel, Shift+M mute all, +/- volume,");
            Console.WriteLine("U mute mic, V mic check, K request key, A reload aliases, T talk permit tone, 1-6 alert tones,");
            Console.WriteLine("P page, D digital page, R radio command, C channel marker, Ctrl+Q quit.");
        }
    } // public static class Program
}
