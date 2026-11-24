// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* Usage:
*   sidetone [--codeplug FILE] [--system NAME] [--theme system|light|dark] [--wayland]
*          [--output DEVICE] [--input DEVICE] [--ptt-device NAME [--ptt-button B] [--mute-button B|none]]
*          [--socket PATH | --no-socket] [--tx-limit SECONDS] [--log FILE | --no-log]
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
using Avalonia.VisualTree;
using Avalonia.Controls;

// Linux only, like the core
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Sidetone.Gui
{
    /// <summary>A stand-in for Android's platform, for the self-check.</summary>
    internal sealed class FakeDevice : IGuiPlatform
    {
        public Action<int, string> Learning;
        public int? AppliedKey;
        public string AppliedName;
        public bool? AppliedWake, AppliedBroadcasts;

        public void LearnPttKey(Action<int, string> learned) => Learning = learned;
        public void CancelLearn() => Learning = null;
        public void Apply(AppSettings settings, DispatchConsole console) { AppliedKey = settings.HardwarePttKey; AppliedName = settings.HardwarePttKeyName; AppliedWake = settings.WakeOnCall != false; AppliedBroadcasts = settings.PttFromBroadcasts == true; }
        public void ConsoleStarted(DispatchConsole console) { }
        public void ConsoleStopped() { }
        public void OpenBatterySettings() { }
        public string DeviceDescription => "Test device";
        /// <summary>What the last share offered.</summary>
        public List<string> Shared;
        public bool Share(IReadOnlyList<string> files, string subject) { Shared = files.ToList(); return true; }
    }

    public static class Program
    {
        [STAThread]
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        public static int Main(string[] args)
        {
            // Windows builds it as a GUI program (no console window); started from a terminal, its output (--version,
            // --selfcheck, errors) goes to that terminal. Output that's redirected (CI) needs nothing.
            if (OperatingSystem.IsWindows() && Console.IsOutputRedirected == false)
                AttachConsole(-1);      // ATTACH_PARENT_PROCESS; fails quietly when started from the Start menu
            DesktopPlatform.Setup();    // macOS and Windows: their own folders, speech, password store and notifications
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
            if (!opt.SelfCheck && opt.Screenshot == null && !opt.NoLog && !opt.Demo)
            {
                DebugLog.Start(DebugLog.DefaultFolder, $"on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
                DebugLog.CatchCrashes();
            }
            App.CreateWindow = o => new MainWindow(o);

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
                    string entry = AppLauncher.Install(AppLauncher.DataHome, AppLauncher.ProgramPath);
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
                window.View.OpenBox(opt.Show);
            window.View.RefreshNow();
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
            window.View.CloseConsole();
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
            DispatchConsole c = w.View.ActiveConsole;

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
            Check(w.View.SpacePresses == 1 && w.View.SpaceReleases == 0, $"holding Space is one press, however the repeats arrive ({w.View.SpacePresses} / {w.View.SpaceReleases})");
            Check(c.SnapshotLog(5).Any(l => l.Contains("not connected to the FNE yet")), "Space reaches the console's push-to-talk (refused here: the demo isn't connected)");

            // the release is decided by a timer, so run the real event loop for a moment
            w.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            using (CancellationTokenSource stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
                Dispatcher.UIThread.MainLoop(stop.Token);
            Check(w.View.SpacePresses == 1 && w.View.SpaceReleases == 1, $"letting go of Space releases once, shortly after ({w.View.SpacePresses} / {w.View.SpaceReleases})");

            // tones, the marker, and the page and radio command boxes
            Press(PhysicalKey.C);
            bool marker = c.MarkerOn;
            Press(PhysicalKey.C);
            Check(marker && !c.MarkerOn, "C turns the channel marker on and off");

            Press(PhysicalKey.Digit1);
            Check(c.SnapshotLog(1).Any(l => l.Contains("not connected to the FNE yet")), "1 asks the console for alert tone 1 (refused here: not connected)");

            CommandOverlay box = w.View.Overlay;
            Press(PhysicalKey.R);
            Check(box.Open == CommandOverlay.Kind.Radio, "R opens the radio command box");
            w.KeyTextInput("1234");
            Dispatcher.UIThread.RunJobs();
            Check(box.RadioRid.Text == "1234", $"typing goes into the radio ID field ('{box.RadioRid.Text}')");
            Press(PhysicalKey.M);
            Press(PhysicalKey.Space);
            Check(!focus.Muted && w.View.SpacePresses == 1 && box.Open == CommandOverlay.Kind.Radio, "while a box is open, shortcut keys don't act on the console");

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
            Point onPtt = w.View.PttTap.Root.TranslatePoint(new Point(10, 10), w) ?? new Point(-1, -1);
            w.MouseDown(onPtt, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            bool pressed = w.View.PointerPresses == 1 && w.View.PointerReleases == 0 &&
                           c.SnapshotLog(200).Count(l => l.Contains("not connected to the FNE yet")) == refusals + 1;
            w.MouseUp(onPtt, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check(pressed && w.View.PointerReleases == 1, $"pressing the PTT button keys the console and letting go releases it ({w.View.PointerPresses} / {w.View.PointerReleases})");

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
            Press(PhysicalKey.X);
            Press(PhysicalKey.X, RawInputModifiers.Shift);
            bool source = focus.PatchRole == PatchRole.SourceOnly;
            Press(PhysicalKey.X, RawInputModifiers.Shift);
            bool target = focus.PatchRole == PatchRole.TargetOnly && DispatchConsole.PatchMark(focus) == "←";
            Press(PhysicalKey.X, RawInputModifiers.Shift);
            Press(PhysicalKey.X);
            Press(PhysicalKey.X);
            Press(PhysicalKey.X);
            Check(source && target && focus.PatchRole == PatchRole.Both && focus.Patch == 0,
                  "Shift+X switches a patched channel between both ways, source only and target only");

            // the Audio outputs box
            Press(PhysicalKey.O);
            RoutingOverlay routing = w.View.RoutingBox;
            bool routingOpen = routing.IsOpen;
            Avalonia.Controls.ComboBox tabCombo = routing.ZoneBox(c.CurrentZone), chanCombo = routing.ChannelBox(focus);
            tabCombo.SelectedIndex = 2;         // demo.headset
            chanCombo.SelectedIndex = 1;        // demo.speakers
            bool routed = c.CurrentZone.Output == "demo.headset" && focus.Output == "demo.speakers" && c.OutputSource(focus) == "own";
            chanCombo.SelectedIndex = 0;
            tabCombo.SelectedIndex = 0;
            Press(PhysicalKey.Escape);
            Check(routingOpen && routed && focus.Output == null && c.CurrentZone.Output == null && !routing.IsOpen,
                  "O opens Audio outputs: a tab's and a channel's output are chosen there at once; Esc closes it");

            // Announcements: the demo has two files, a spoken text and the time announcement
            Press(PhysicalKey.N);
            AnnouncementOverlay announce = w.View.AnnounceBox;
            bool annOpen = announce.IsOpen && announce.FileCount == 2 && announce.SpokenCount == 1 && announce.Count == 4;
            Press(PhysicalKey.ArrowDown);
            Press(PhysicalKey.Enter);
            bool refused = announce.IsOpen && announce.Message.Contains("ot connected") && announce.Row == 1;
            Press(PhysicalKey.Escape);
            Check(annOpen && refused && !announce.IsOpen,
                  "N opens Announcements with the remembered files; Enter plays one (refused here, not connected, saying why); Esc closes it");

            // typing a text to say: keys go to the text box (N doesn't close the box), Enter adds it; the time's clock switches
            Press(PhysicalKey.N);
            announce.NewText.Text = "Radio check";
            announce.NewText.Focus();
            Dispatcher.UIThread.RunJobs();
            Press(PhysicalKey.N);
            bool stillOpen = announce.IsOpen;
            Press(PhysicalKey.Enter);
            bool textAdded = announce.SpokenCount == 2 && c.SpokenAnnouncements.Contains("Radio check") && announce.NewText.Text == string.Empty;
            announce.Use24Hour(true);
            string said24 = announce.WillSay;
            bool clock24 = c.TimeAnnouncement.Hour24 && !said24.Contains(" AM") && !said24.Contains(" PM") && said24.StartsWith("Demo system time is ");
            announce.Use24Hour(false);
            bool clock12 = !c.TimeAnnouncement.Hour24 && (announce.WillSay.Contains(" AM") || announce.WillSay.Contains(" PM"));
            announce.ChooseLevel(-3);
            bool levelled = c.AnnouncementLevel == -3;
            announce.ChooseLevel(0);
            levelled &= c.AnnouncementLevel == 0;
            announce.ChooseSpeed(0.9);
            bool slowed = c.SpeechRate == 0.9;
            announce.ChooseSpeed(1.0);
            bool normal = c.SpeechRate == 1.0;
            announce.NewText.Focus();
            Dispatcher.UIThread.RunJobs();
            Press(PhysicalKey.Escape);
            Check(stillOpen && textAdded && clock24 && clock12 && slowed && normal && levelled && !announce.IsOpen,
                  $"Announcements: a text to say is typed and added with Enter (N types, it doesn't close the box); the level, the speed and the time announcement's clock switch ({said24})");

            // instant recall: the demo has calls on Tac 1, the focused channel
            Press(PhysicalKey.L, RawInputModifiers.Shift);
            RecallOverlay recall = w.View.RecallBox;
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
            w.View.OpenBox("keyfile");
            Dispatcher.UIThread.RunJobs();
            Press(PhysicalKey.Enter);
            bool needsPassword = w.View.KeyBox.IsOpen && w.View.KeyBox.Message.Contains("password");
            Press(PhysicalKey.Escape);
            Check(needsPassword && !w.View.KeyBox.IsOpen, "the key file box wants a password; Esc is 'not now'");

            // display scale
            const string monitors = "[{\"name\":\"eDP-1\",\"scale\":2.44,\"focused\":true},{\"name\":\"DP-1\",\"scale\":1.0,\"focused\":false}]";
            Check(DisplayScale.FromHyprland("{\"option\":\"xwayland:force_zero_scaling\",\"int\":1,\"set\":true}", monitors) == 2.44 &&
                  DisplayScale.FromHyprland("{\"option\":\"xwayland:force_zero_scaling\",\"bool\":true}", monitors) == 2.44 &&
                  DisplayScale.FromHyprland("{\"option\":\"xwayland:force_zero_scaling\",\"int\":0,\"set\":false}", monitors) == null &&
                  DisplayScale.FromHyprland("{\"int\":1}", "[{\"scale\":1.0,\"focused\":true}]") == null &&
                  DisplayScale.FromHyprland("not json", monitors) == null && DisplayScale.FromHyprland(null, null) == null,
                  "display scale: taken from Hyprland's focused monitor, only when it leaves XWayland windows unscaled");

            // Settings
            SettingsOverlay set = w.View.SettingsBox;
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            Check(set.IsOpen && set.Output.ItemCount == 3 && set.Input.ItemCount == 3 && set.PttDevice.ItemCount == 3,
                  $"Ctrl+, opens Settings with the audio and PTT devices listed ({set.Output.ItemCount} / {set.Input.ItemCount} / {set.PttDevice.ItemCount})");
            Check(!set.PttButton.IsEnabled && set.PttButton.ItemCount == 1, "with no PTT device chosen, there are no buttons to choose");
            Check(set.RecordingFormat == DispatchConsole.FormatOpus && set.KeepDays.ItemCount == DispatchConsole.KeepDayChoices.Length + 1 && set.KeepDays.SelectedIndex == 0,
                  $"Settings: recordings are Opus by default, kept until a 'delete after' is chosen ({set.KeepDays.ItemCount} choices)");
            Check(set.KeepHistory && !set.HistorySecure, "Settings: the call history is kept between runs, without secure calls' audio, unless chosen");
            set.PressCopyLog();
            Check(set.LogResult.StartsWith("Copied ") && (w.View.LastCopiedLog ?? string.Empty).Contains("demo: a made-up system"),
                  $"Settings: Copy log puts the console's log on the clipboard ('{set.LogResult}')");
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
            Check(!set.IsOpen && w.View.TxLimitSeconds == 180, "Esc closes Settings without saving (the limit stays the codeplug's: 180 s when it doesn't say, as the Windows console)");

            // the Systems row: only for a codeplug with several systems; each can be turned off, but not the last one on
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            bool noRowForOne = set.IsOpen && !set.SystemsRowShown;
            set.Show(new SettingsOverlay.Values { Systems = new List<(string, bool)> { ("Main FNE", true), ("DMR FNE", true) } }, null, new List<InputDeviceInfo>(),
                     "multi.yml", string.Empty, false, "None", false, false, "/tmp", false);
            bool rowShown = set.SystemsRowShown && set.SystemNames.SequenceEqual(new[] { "Main FNE", "DMR FNE" }) && set.SystemOn("DMR FNE");
            set.PressSystem("DMR FNE");
            bool dmrOff = !set.SystemOn("DMR FNE");
            set.PressSystem("Main FNE");
            Check(noRowForOne && rowShown && dmrOff && set.SystemOn("Main FNE") && set.Message.Contains("At least one system"),
                  "Settings: a codeplug with several systems gets a Systems row; each can be turned off, but not the last one on");
            set.PressSystem("DMR FNE");
            Press(PhysicalKey.Escape);

            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            set.TxLimit.Text = "90";
            set.Save();
            Check(!set.IsOpen && w.View.TxLimitSeconds == 90, "Save keeps the new time limit");
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            bool chimesShownOn = set.IsOpen && set.Chimes;
            set.PressChimes(false);
            set.Save();
            bool chimesOff = c.Settings?.ConnectionChimes == false && !c.ConnectionChimes;
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            bool chimesShownOff = set.IsOpen && !set.Chimes;
            set.PressChimes(true);
            set.Save();
            Check(chimesShownOn && chimesOff && chimesShownOff && c.Settings?.ConnectionChimes == null && c.ConnectionChimes,
                  "Settings: connection chimes are on by default, and turn off and on again");
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            bool defaults = set.Processing == (true, true, false, true, false);
            set.FlipProcessing(2);
            set.FlipProcessing(4);
            set.Save();
            VoiceProcessing micStages = c.MicProcessing;
            Check(defaults && !set.IsOpen && micStages.NoiseGate && micStages.LowCut && micStages.AutoLevel && micStages.Limiter && c.RxLeveling &&
                  c.Channels.All(ch => ch.RxLevel != null) && c.SnapshotLog(3).Any(l => l.Contains("audio processing: microphone low cut, noise gate, auto level, limiter; receive leveling on")),
                  "mic processing starts at its defaults; turning on the gate and receive leveling applies at once");
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            bool automatic = set.Scale.SelectedIndex == 0 && w.View.SavedScale == null;
            set.Scale.SelectedIndex = 5;        // 200%
            set.Save();
            Check(automatic && w.View.SavedScale == 2.0 && c.SnapshotLog(1).Any(l => l.Contains("next time Sidetone starts")),
                  "the display scale is automatic until one is chosen; a new one is saved for the next start");
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            set.SetFormat(DispatchConsole.FormatWav);
            set.KeepDays.SelectedIndex = 3;     // 30 days
            set.Save();
            bool savedRec = c.RecordingFormat == DispatchConsole.FormatWav && c.RecordingKeepDays == 30;
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            bool reopened = set.RecordingFormat == DispatchConsole.FormatWav && set.KeepDays.SelectedIndex == 3;
            set.SetFormat(DispatchConsole.FormatOpus);
            set.KeepDays.SelectedIndex = 0;
            set.Save();
            Check(savedRec && reopened && c.RecordingFormat == DispatchConsole.FormatOpus && c.RecordingKeepDays == null,
                  "Settings: the recording format and 'delete after' are saved, shown again, and can go back to Opus and keeping them");
            bool holdBefore = !w.View.SpaceIsToggle;
            Press(PhysicalKey.Comma, RawInputModifiers.Control);
            Check(set.IsOpen && set.TxLimit.Text == "90", "Settings reopens on what was saved");
            Press(PhysicalKey.Escape);

            // the app launcher entry, in a scratch folder (a Linux desktop entry; Windows paths don't fit it, and it isn't offered there)
            if (OperatingSystem.IsWindows())
                Console.WriteLine("  SKIP: launcher: the app launcher entry is a Linux desktop file (the installer adds the Start menu entry)");
            else
            {
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

                    // an AppImage: the entry must start the .AppImage file, not the temporary mount it runs from
                    string fakeAppImage = Path.Combine(data, "Sidetone-x86_64.AppImage");
                    File.WriteAllText(fakeAppImage, "x");
                    string wasAppImage = Environment.GetEnvironmentVariable("APPIMAGE");
                    Environment.SetEnvironmentVariable("APPIMAGE", fakeAppImage);
                    string inAppImage = AppLauncher.ProgramPath;
                    Environment.SetEnvironmentVariable("APPIMAGE", Path.Combine(data, "gone.AppImage"));
                    string goneAppImage = AppLauncher.ProgramPath;
                    Environment.SetEnvironmentVariable("APPIMAGE", wasAppImage);
                    Check(inAppImage == fakeAppImage && goneAppImage == Environment.ProcessPath,
                          "launcher: from an AppImage, the entry starts the AppImage file itself");

                    // a package's entry in a system data folder: found, so Settings doesn't add a second one
                    string sysShare = Path.Combine(data, "usr-share"), emptyShare = Path.Combine(data, "empty-share");
                    Directory.CreateDirectory(Path.Combine(sysShare, "applications"));
                    Directory.CreateDirectory(emptyShare);
                    File.WriteAllText(Path.Combine(sysShare, "applications", AppLauncher.DesktopFileName), "[Desktop Entry]");
                    Check(AppLauncher.SystemEntry($"{emptyShare}:{sysShare}") == Path.Combine(sysShare, "applications", AppLauncher.DesktopFileName) &&
                          AppLauncher.SystemEntry(emptyShare) == null,
                          "launcher: a launcher entry installed by a package is found in the system data folders");
                    Check(AppLauncher.QuoteExec("/a/50% \"x\" $y") == "\"/a/50%% \\\\\"x\\\\\" \\\\$y\"", "launcher: reserved characters in the path are escaped");
                }
                finally
                {
                    try { Directory.Delete(data, true); } catch (Exception) { }
                }
            }
            Check(holdBefore, "Space is hold-to-talk unless Settings or --space says otherwise");

            // zone tabs: the demo has four zones, one of them hidden
            ZoneTabs strip = w.View.ZoneStrip;
            List<(ConsoleZone Zone, Tap Tap)> tabList = strip.Tabs.ToList();
            w.View.RefreshNow();
            Check(strip.Root.IsVisible && tabList.Count == 4 && tabList.Count(t => t.Tap.Root.IsVisible) == 3 && c.Zones[3].Hidden &&
                  w.View.CardShown(1) && !w.View.CardShown(4),
                  $"tabs: one per zone, the hidden zone's tab not shown, and only the tab's own cards ({tabList.Count} tabs)");
            c.SetFocus(3);
            Press(PhysicalKey.ArrowRight);
            bool edge = c.FocusIndex == 3;
            Press(PhysicalKey.PageDown, RawInputModifiers.Control);
            bool regional = c.CurrentZone == c.Zones[1] && c.FocusIndex == 4 && w.View.CardShown(4) && !w.View.CardShown(1);
            Press(PhysicalKey.BracketRight);
            bool fire = c.CurrentZone == c.Zones[2];
            Press(PhysicalKey.BracketLeft);
            bool back = c.CurrentZone == c.Zones[1];
            Press(PhysicalKey.Digit1, RawInputModifiers.Control);
            Check(edge && regional && fire && back && c.CurrentZone == c.Zones[0] && c.FocusIndex == 3,
                  "tabs: arrows stay on the tab; Ctrl+PgDn, ] and [ switch tabs; Ctrl+1 goes back to the first, to the card focused there");
            Check(c.SnapshotLog(3).All(l => !l.Contains("not connected")), "tabs: Ctrl+1 picks a tab, not alert tone 1");
            Press(PhysicalKey.Tab, RawInputModifiers.Control);
            bool ctrlTab = c.CurrentZone == c.Zones[1];
            Press(PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
            Press(PhysicalKey.PageUp, RawInputModifiers.Control);
            Check(ctrlTab && c.CurrentZone == c.Zones[2], "tabs: Ctrl+Tab, Ctrl+Shift+Tab and Ctrl+PgUp go through the tabs (round from the first to the last)");

            c.ShowZone(c.Zones[0]);     // so the click has something to do
            w.View.RefreshNow();
            Point onFire = tabList[2].Tap.Root.TranslatePoint(new Point(10, 10), w) ?? new Point(-1, -1);
            w.MouseDown(onFire, MouseButton.Left);
            w.MouseUp(onFire, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check(c.CurrentZone == c.Zones[2] && w.View.CardShown(7), "tabs: clicking a tab shows it");

            ZoneOverlay zbox = w.View.ZoneBox;
            Press(PhysicalKey.Z);
            bool zOpen = zbox.IsOpen && zbox.Row == 2;
            Press(PhysicalKey.ArrowDown);
            Press(PhysicalKey.Space);
            bool aidShown = !c.Zones[3].Hidden && tabList[3].Tap.Root.IsVisible;
            Press(PhysicalKey.Space);
            Press(PhysicalKey.ArrowUp);
            Press(PhysicalKey.Space);
            bool fireHidden = c.Zones[2].Hidden && c.CurrentZone != c.Zones[2] && !tabList[2].Tap.Root.IsVisible && zbox.Message.Length == 0;
            Press(PhysicalKey.Space);
            Press(PhysicalKey.Escape);
            Check(zOpen && aidShown && c.Zones[3].Hidden && fireHidden && !c.Zones[2].Hidden && !zbox.IsOpen,
                  "tabs: Z opens the Tabs box; arrows and Space hide and show zones (hiding the tab on show moves to another); Esc closes it");
            c.SetFocus(1);

            // DMR: the demo's DMR channel is a working card with its timeslot and PTT
            ConsoleChannel dmrCh = c.Channels.First(x => x.IsDmr);
            c.ShowZone(dmrCh.InZone);
            w.View.RefreshNow();
            ChannelCard dmrCard = w.View.CardFor(dmrCh);
            Check(dmrCard != null && dmrCard.TgLine.Contains("TS1") && dmrCard.PttShown && c.GetStatus(dmrCh).Activity != ChannelActivity.Unsupported,
                  $"DMR: the demo's DMR channel is a card like any other, with its timeslot and PTT ({dmrCard?.TgLine})");
            c.SetFocus(1);

            // the Tones box: make a tone preset and a DTMF preset, a bad entry is refused with its reason, Send tries them
            w.View.OpenBox("tones");
            TonePresetOverlay tb = w.View.TonesBox;
            bool tonesOpen = tb.IsOpen;
            tb.StartNew(false);
            tb.Fill("Bad", steps: "1000:0.5, 9000:1");
            tb.Save();
            bool badRefused = tb.Editing && tb.Hint.Contains("outside 281–2500 Hz") && tb.Counts.Tones == 0;
            tb.Fill("Two tone", steps: "1000:0.5, 0:0.2, 1500:1");
            tb.Save();
            tb.StartNew(true);
            tb.Fill("Call", digits: "911#");
            tb.Save();
            tb.Send(false, 0);
            string sendSaid = tb.Message;      // the demo isn't connected: the reason shows in the box
            Press(PhysicalKey.Escape);
            Check(tonesOpen && badRefused && tb.Counts == (1, 1) && c.Settings?.TonePresets?.Single().Steps.Count == 3 && c.Settings?.DtmfPresets?.Single().Digits == "911#" &&
                  sendSaid.Contains("not connected") && !tb.IsOpen,
                  $"Tones box: a tone preset and a DTMF preset are made and kept, a bad step is refused with its reason, Send says why it can't, Esc closes ({sendSaid})");

            // folding Recent calls and the log down to one line, and back
            w.View.ToggleBottom();
            w.View.RefreshNow();
            (string foldedRecent, string foldedLog) = w.View.BottomTitles;
            bool folded = w.View.BottomCollapsed && c.Settings?.BottomCollapsed == true && foldedRecent.StartsWith("▸ Recent: ") && foldedLog.StartsWith("▸ Log: ");
            w.View.ToggleBottom();
            Check(folded && !w.View.BottomCollapsed && w.View.BottomTitles.Recent == "▾ Recent calls" && c.Settings?.BottomCollapsed == null,
                  $"bottom panels: a title folds Recent calls and the log to one line each (the newest of each), and back ({foldedRecent})");

            // web streams: the demo's one is a card after the Dispatch tab's channels, off until Play
            c.ShowZone(c.Zones[0]);
            w.View.RefreshNow();
            StreamCard scard = w.View.StreamCards.Single();
            bool streamOnTab = scard.Root.IsVisible && scard.Stream.Name == "County scanner" && scard.PlayTap.Text == "Play" && !scard.Stream.On;
            Point onPlay = scard.PlayTap.Root.TranslatePoint(new Point(5, 5), w) ?? new Point(-1, -1);
            w.MouseDown(onPlay, MouseButton.Left);
            w.MouseUp(onPlay, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            w.View.RefreshNow();
            bool streamStarted = scard.Stream.On && scard.PlayTap.Text == "Stop" && (scard.Stream.State == StreamState.Connecting || scard.Stream.State == StreamState.Failed);
            c.ShowZone(c.Zones[1]);
            w.View.RefreshNow();
            bool streamHiddenOnOtherTab = !scard.Root.IsVisible;
            c.ShowZone(c.Zones[0]);
            w.View.RefreshNow();
            w.MouseDown(onPlay, MouseButton.Left);
            w.MouseUp(onPlay, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check(streamOnTab && streamStarted && streamHiddenOnOtherTab && !scard.Stream.On && scard.Stream.State == StreamState.Off,
                  "web streams: a stream card shows on its own tab only; Play starts it, Stop stops it");
            c.SetFocus(1);

            // the phone layout: narrower than ConsoleView.NarrowWidth
            ConsoleView v = w.View;
            void Resize(double width, double height)
            {
                w.Width = width;
                w.Height = height;
                Dispatcher.UIThread.RunJobs();
                w.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                w.UpdateLayout();
            }
            Resize(412, 915);
            Check(v.Narrow && v.PageShown == ConsoleView.NarrowPage.Channels && v.BigPtt.Root.IsEffectivelyVisible && v.CardsOnShow && !v.PttTap.Root.IsEffectivelyVisible,
                  "phone layout: a narrow window shows the cards in one column with a big PTT at the bottom (the header's PTT hidden)");
            v.RefreshNow();
            Check(v.StreamCards.Single().NarrowWidth is double sw && sw > 300 && Math.Abs(v.StreamCards.Single().Root.Width - sw) < 0.5,
                  "phone layout: stream cards fill the width like the channel cards");

            int pressesBefore = v.PointerPresses, releasesBefore = v.PointerReleases, refusedBefore = c.SnapshotLog(200).Count(l => l.Contains("not connected to the FNE yet"));
            Point onBig = v.BigPtt.Root.TranslatePoint(new Point(20, 20), w) ?? new Point(-1, -1);
            w.MouseDown(onBig, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            bool bigPressed = v.PointerPresses == pressesBefore + 1 && c.SnapshotLog(200).Count(l => l.Contains("not connected to the FNE yet")) == refusedBefore + 1;
            w.MouseUp(onBig, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check(bigPressed && v.PointerReleases == releasesBefore + 1, "phone layout: the big PTT is hold-to-talk (pressed and let go)");

            void Tap(Tap t)
            {
                Point at = t.Root.TranslatePoint(new Point(t.Root.Bounds.Width / 2, t.Root.Bounds.Height / 2), w) ?? new Point(-1, -1);
                w.MouseDown(at, MouseButton.Left);
                w.MouseUp(at, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                w.UpdateLayout();
            }
            Tap(v.NavTap(ConsoleView.NarrowPage.Calls));
            bool callsShown = v.PageShown == ConsoleView.NarrowPage.Calls && !v.CardsOnShow;
            Tap(v.NavTap(ConsoleView.NarrowPage.Log));
            bool logShown = v.PageShown == ConsoleView.NarrowPage.Log;
            Tap(v.NavTap(ConsoleView.NarrowPage.More));
            bool moreShown = v.PageShown == ConsoleView.NarrowPage.More;
            bool backed = v.GoBack() && v.PageShown == ConsoleView.NarrowPage.Channels && v.CardsOnShow && !v.GoBack();
            Check(callsShown && logShown && moreShown && backed, "phone layout: Calls, Log and More pages; Back returns to the channels, then has nothing to go back from");

            Tap(v.MuteAllTap);
            bool allMuted = c.GlobalMute;
            Tap(v.MuteAllTap);
            Check(allMuted && !c.GlobalMute, "phone layout: Mute all mutes and unmutes everything");

            // boxes on a phone: full-screen sheets at full size (PhoneSheet), labels above their fields, nothing cut off
            void Settle()
            {
                for (int i = 0; i < 3; i++)
                {
                    v.RefreshNow();
                    Dispatcher.UIThread.RunJobs();
                    w.UpdateLayout();
                }
            }
            v.OpenBox("settings");
            Settle();
            Control settingsCard = ConsoleView.BoxCard(v.SettingsBox.Root);
            Grid outputRow = v.SettingsBox.Output.Parent as Grid;
            bool sheet = settingsCard.Bounds.Width >= v.Bounds.Width - 1 && settingsCard.Bounds.Height > 600;
            bool stacked = outputRow != null && outputRow.RowDefinitions.Count == 2 && Grid.GetRow(v.SettingsBox.Output) == 1 && v.SettingsBox.Output.Bounds.Width > 340;
            bool closedByBack = v.GoBack() && !v.SettingsBox.IsOpen;
            Check(sheet && stacked && closedByBack,
                  $"phone layout: a box is a full-screen sheet ({settingsCard.Bounds.Width:0}x{settingsCard.Bounds.Height:0} in {v.Bounds.Width:0}), labels above their fields, which fill the width; Back closes it");
            v.OpenBox("patch");
            Settle();
            Control patchCard = ConsoleView.BoxCard(v.PatchBox.Root);
            double widest = patchCard.GetVisualDescendants().OfType<Border>().Where(bd => bd.IsEffectivelyVisible && bd.Bounds.Width > 0)
                                     .Select(bd => bd.TranslatePoint(new Point(bd.Bounds.Width, 0), patchCard)?.X ?? 0).DefaultIfEmpty(0).Max();
            v.GoBack();
            Check(widest > 0 && widest <= patchCard.Bounds.Width + 0.5,
                  $"phone layout: the Patches buttons wrap instead of being cut off (rightmost edge {widest:0} of {patchCard.Bounds.Width:0})");

            Resize(1280, 800);
            v.OpenBox("settings");
            Settle();
            Control tabletCard = ConsoleView.BoxCard(v.SettingsBox.Root);
            Grid tabletRow = v.SettingsBox.Output.Parent as Grid;
            bool desktopBox = Math.Abs(tabletCard.Bounds.Width - 580) < 1 && tabletRow.ColumnDefinitions.Count == 2 && tabletRow.RowDefinitions.Count == 0 &&
                              tabletCard.Bounds.X > 100;
            v.GoBack();
            Check(!v.Narrow && v.PttTap.Root.IsEffectivelyVisible && !v.BigPtt.Root.IsEffectivelyVisible && desktopBox,
                  $"phone layout: widening the window (a tablet) brings the usual layout and the usual centred boxes back ({tabletCard.Bounds.Width:0} wide)");

            // a device platform (Android's, stood in for): Settings shows the PTT key, screen and battery rows
            FakeDevice fakeDevice = new FakeDevice();
            GuiPlatform.Current = fakeDevice;
            try
            {
                MainWindow dw = new MainWindow(opt);
                dw.Show();
                Dispatcher.UIThread.RunJobs();
                ConsoleView dv = dw.View;
                dv.OpenBox("settings");
                Dispatcher.UIThread.RunJobs();
                SettingsOverlay ds = dv.SettingsBox;
                ds.PressLearn();
                bool waiting = ds.Learning && fakeDevice.Learning != null;
                fakeDevice.Learning(25, "KEYCODE_VOLUME_DOWN");
                bool learned = !ds.Learning && ds.PttKey == 25;
                bool wakeByDefault = ds.WakeOnCall;
                bool lockedOffByDefault = !ds.PttFromBroadcasts;
                ds.PressWake(false);
                ds.PressLocked(true);
                ds.PressSave();
                Dispatcher.UIThread.RunJobs();
                Check(lockedOffByDefault && fakeDevice.AppliedBroadcasts == true,
                      "device: PTT key broadcasts (PTT when locked) are off until Settings allows them, and the platform is told");
                Check(wakeByDefault && fakeDevice.AppliedWake == false,
                      "device: waking the screen for incoming calls is on until Settings turns it off, and the platform is told");
                Check(waiting && learned && !ds.IsOpen && fakeDevice.AppliedKey == 25 && fakeDevice.AppliedName == "KEYCODE_VOLUME_DOWN" &&
                      SettingsOverlay.KeyLabel("KEYCODE_VOLUME_DOWN", 25) == "Volume down" && SettingsOverlay.KeyLabel("KEYCODE_1015", 1015) == "key 1015",
                      "device: Settings learns a hardware PTT key by pressing it (Volume down), and Save hands it to the platform");

                // recordings: Instant recall's button opens the Recordings box, which hands calls to the share sheet
                string recDir = Path.Combine(Path.GetTempPath(), $"sidetone-share-{Environment.ProcessId}");
                try
                {
                    DateTime today = DateTime.Today;
                    string Rec(DateTime at, string who, bool tx = false)
                    {
                        string f = DispatchConsole.RecordingPath(recDir, "Demo System", new RecalledCall { Channel = "Tac 1", Talkgroup = 101, Who = who, Started = at, Tx = tx });
                        Directory.CreateDirectory(Path.GetDirectoryName(f));
                        File.WriteAllText(f, "x");
                        return f;
                    }
                    string newest = Rec(today.AddHours(17).AddMinutes(38), "Unit 12"), earlier = Rec(today.AddHours(9), "9000001", tx: true), yesterday = Rec(today.AddDays(-1).AddHours(23), "Engine 7");
                    dv.ActiveConsole.RecordingsFolder = recDir;
                    dv.OpenBox("recall");
                    dv.RefreshNow();
                    bool shareLabel = dv.RecallBox.FolderText == "Recordings…";
                    dv.RecallBox.PressFolder();
                    Dispatcher.UIThread.RunJobs();
                    RecordingsOverlay rb = dv.RecordingsBox;
                    bool recListed = rb.IsOpen && !dv.RecallBox.IsOpen && rb.Files.Count == 3 && rb.Days.Count == 2 && rb.Days[0] == today && rb.Files[0].Path == newest;
                    rb.ShareFile(0);
                    bool one = fakeDevice.Shared != null && fakeDevice.Shared.SequenceEqual(new[] { newest });
                    rb.ShareDay(today);
                    bool day = fakeDevice.Shared != null && fakeDevice.Shared.OrderBy(x => x).SequenceEqual(new[] { newest, earlier }.OrderBy(x => x)) && !fakeDevice.Shared.Contains(yesterday);
                    bool closes = dv.GoBack() && !rb.IsOpen;
                    Check(shareLabel && recListed && one && day && closes && RecordingsOverlay.DayName(today).StartsWith("Today, ") && RecordingsOverlay.DayName(today.AddDays(-1)).StartsWith("Yesterday, "),
                          "device: Instant recall's Recordings… lists the recordings by day, newest first; Share gives one call to the share sheet, Share day all of that day's; Back closes it");
                }
                finally
                {
                    try { Directory.Delete(recDir, true); } catch (Exception) { }
                }
                dw.Close();
            }
            finally { GuiPlatform.Current = null; }
            w.View.OpenBox("recall");
            w.View.RefreshNow();
            Check(w.View.RecallBox.FolderText == "Open recordings folder", "desktop: Instant recall keeps 'Open recordings folder' (sharing is for devices)");
            w.View.RecallBox.Close();

            // touch: a finger acts when it lifts without having moved (so scrolling over cards doesn't choose them)
            TouchTap tt = new TouchTap();
            Avalonia.Input.Pointer finger = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Touch, true), mouse = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
            bool mouseNow = tt.Pressed(mouse, new Point(5, 5));
            bool fingerWaits = !tt.Pressed(finger, new Point(5, 5));
            tt.Moved(finger, new Point(9, 12));
            bool tapped = tt.Released(finger, new Point(9, 12));
            tt.Pressed(finger, new Point(5, 5));
            tt.Moved(finger, new Point(5, 40));
            bool swiped = !tt.Released(finger, new Point(5, 6));
            tt.Pressed(finger, new Point(5, 5));
            tt.Cancel();
            bool taken = !tt.Released(finger, new Point(5, 5));
            Check(mouseNow && fingerWaits && tapped && swiped && taken,
                  "touch: a mouse acts on press; a finger only when it lifts having moved less than 10 px, not when it scrolled or the scroll view took it");

            // Go offline logs out and keeps the codeplug; Go online logs in again with it
            int channelsBefore = w.View.ActiveConsole?.Channels.Count ?? -1;
            void TapOn(Tap t)
            {
                Point at = t.Root.TranslatePoint(new Point(t.Root.Bounds.Width / 2, t.Root.Bounds.Height / 2), w) ?? new Point(-1, -1);
                w.MouseDown(at, MouseButton.Left);
                w.MouseUp(at, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                w.UpdateLayout();
            }
            TapOn(w.View.OfflineTap);
            bool wentOffline = w.View.ActiveConsole == null && w.View.IsOffline && w.View.GoOnlineTap.Root.IsEffectivelyVisible && !w.View.OfflineTap.Root.IsEffectivelyVisible;
            TapOn(w.View.GoOnlineTap);
            Check(wentOffline && w.View.ActiveConsole != null && !w.View.IsOffline && w.View.ActiveConsole.Channels.Count == channelsBefore && w.View.OfflineTap.Root.IsEffectivelyVisible,
                  "Go offline closes the console (logged out) and shows Connect to FNE, which opens the same codeplug again");

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
            Console.WriteLine("         [--socket PATH | --no-socket] [--tx-limit SECONDS] [--log FILE | --no-log]");
            Console.WriteLine("         [--settings FILE | --no-settings] [--space hold|toggle]");
            Console.WriteLine("                  [--scale FACTOR] [--key-file FILE.ekc] [--record-dir DIR] [--no-notify] [--no-processing]");
            Console.WriteLine("  sidetone --install-launcher | --remove-launcher");
            Console.WriteLine("  sidetone --demo");
            Console.WriteLine("  sidetone --screenshot FILE.png [--demo] [--theme light|dark] [--size 1280x800] [--show page|digital|radio|settings|keyfile|patch|recall|recordings|tones|tabs]");
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
