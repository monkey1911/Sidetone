// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmgui - the graphical front end of the Linux DVM console.
*
* Usage:
*   dvmgui [--codeplug FILE] [--system NAME] [--theme system|light|dark] [--wayland]
*          [--output DEVICE] [--input DEVICE] [--ptt-device NAME [--ptt-button B] [--mute-button B|none]]
*          [--socket PATH | --no-socket] [--tx-limit SECONDS] [--log FILE]
*          [--settings FILE | --no-settings] [--space hold|toggle]
*   dvmgui --demo                      a made-up system, nothing is connected
*   dvmgui --screenshot FILE.png       render the window off-screen and exit (with --demo, --theme, --size WxH)
*   dvmgui --selfcheck                 press the keyboard shortcuts on an off-screen window and check what they do
*/

using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

// Linux only, like dvmcore
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace dvmgui
{
    /// <summary>
    /// Command line options.
    /// </summary>
    public sealed class GuiOptions
    {
        public string Codeplug, SystemName, Output, Input, PttDevice, PttButton, MuteButton, Socket, LogFile, Theme, Screenshot, SettingsFile;
        public bool NoSocket, Wayland, Demo, NoSettings, SpaceToggle, SelfCheck;
        public int TxLimitSeconds = 120;
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
                        if (!int.TryParse(Next(), out o.TxLimitSeconds) || o.TxLimitSeconds < 5 || o.TxLimitSeconds > 600)
                            throw new ArgumentException("--tx-limit must be 5-600 seconds");
                        break;
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
                Console.WriteLine($"dvmgui {Assembly.GetExecutingAssembly().GetName().Version}");
                return 0;
            }
            if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h" || args[0] == "help"))
            {
                PrintUsage();
                return 0;
            }

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

        private static int Screenshot(GuiOptions opt)
        {
            SetupHeadless();

            MainWindow window = new MainWindow(opt);
            window.Show();
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
            Console.WriteLine($"dvmgui: wrote {path} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
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

            Console.WriteLine("dvmgui keyboard self-check");
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

            w.KeyPressQwerty(PhysicalKey.Q, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Check(closed, "Ctrl+Q closes the window");

            Console.WriteLine(failures == 0 ? "ALL GUI CHECKS PASSED" : $"{failures} GUI CHECK(S) FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("dvmgui - DVM dispatch console for Linux (graphical)");
            Console.WriteLine();
            Console.WriteLine("  dvmgui [--codeplug FILE] [--system NAME] [--theme system|light|dark] [--wayland]");
            Console.WriteLine("         [--output DEVICE] [--input DEVICE]");
            Console.WriteLine("         [--ptt-device NAME [--ptt-button BUTTON] [--mute-button BUTTON|none]]");
            Console.WriteLine("         [--socket PATH | --no-socket] [--tx-limit SECONDS] [--log FILE]");
            Console.WriteLine("         [--settings FILE | --no-settings] [--space hold|toggle]");
            Console.WriteLine("  dvmgui --demo");
            Console.WriteLine("  dvmgui --screenshot FILE.png [--demo] [--theme light|dark] [--size 1280x800]");
            Console.WriteLine();
            Console.WriteLine("Without --codeplug, the codeplug opened last time is used. --demo shows a made-up system");
            Console.WriteLine("without connecting to anything. The options are the same as 'dvmcli tui'; see the README.");
            Console.WriteLine("On Wayland desktops the window runs through XWayland unless you pass --wayland.");
            Console.WriteLine();
            Console.WriteLine("Keys: Space push-to-talk (hold; --space toggle makes it start/stop), Esc stop transmitting,");
            Console.WriteLine("arrows move between cards, Enter listen, M mute channel, Shift+M mute all, +/- volume,");
            Console.WriteLine("U mute mic, V mic check, K request key, A reload aliases, T talk permit tone, Ctrl+Q quit.");
        }
    } // public static class Program
}
