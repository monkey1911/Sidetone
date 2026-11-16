// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmgui - the graphical front end of the Linux DVM console.
*
* Usage:
*   dvmgui [--codeplug FILE] [--system NAME] [--theme system|light|dark] [--wayland]
*          [--output DEVICE] [--input DEVICE] [--ptt-device NAME [--ptt-button B] [--mute-button B|none]]
*          [--socket PATH | --no-socket] [--tx-limit SECONDS] [--log FILE]
*          [--settings FILE | --no-settings]
*   dvmgui --demo                      a made-up system, nothing is connected
*   dvmgui --screenshot FILE.png       render the window off-screen and exit (with --demo, --theme, --size WxH)
*/

using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace dvmgui
{
    /// <summary>
    /// Command line options.
    /// </summary>
    public sealed class GuiOptions
    {
        public string Codeplug, SystemName, Output, Input, PttDevice, PttButton, MuteButton, Socket, LogFile, Theme, Screenshot, SettingsFile;
        public bool NoSocket, Wayland, Demo, NoSettings;
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
        private static int Screenshot(GuiOptions opt)
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHarfBuzz()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

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
            frame.Save(path);
            Console.WriteLine($"dvmgui: wrote {path} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
            window.CloseConsole();
            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("dvmgui - DVM dispatch console for Linux (graphical)");
            Console.WriteLine();
            Console.WriteLine("  dvmgui [--codeplug FILE] [--system NAME] [--theme system|light|dark] [--wayland]");
            Console.WriteLine("         [--output DEVICE] [--input DEVICE]");
            Console.WriteLine("         [--ptt-device NAME [--ptt-button BUTTON] [--mute-button BUTTON|none]]");
            Console.WriteLine("         [--socket PATH | --no-socket] [--tx-limit SECONDS] [--log FILE]");
            Console.WriteLine("         [--settings FILE | --no-settings]");
            Console.WriteLine("  dvmgui --demo");
            Console.WriteLine("  dvmgui --screenshot FILE.png [--demo] [--theme light|dark] [--size 1280x800]");
            Console.WriteLine();
            Console.WriteLine("Without --codeplug, the codeplug opened last time is used. --demo shows a made-up system");
            Console.WriteLine("without connecting to anything. The options are the same as 'dvmcli tui'; see the README.");
            Console.WriteLine("On Wayland desktops the window runs through XWayland unless you pass --wayland.");
        }
    } // public static class Program
}
