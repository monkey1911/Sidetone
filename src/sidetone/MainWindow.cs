// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The desktop window: a frame around the ConsoleView, which is the whole console (the same view
* is the single view of the Android app). The window gives it a title, the icon, the keyboard,
* and closes the console when it closes. On Windows, a page flashes its taskbar button (Windows has no
* notifications Sidetone can use without a Start-menu identity; toasts may come later).
*/

using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Sidetone.Core;

namespace Sidetone.Gui
{
    public sealed class MainWindow : Window
    {
        /// <summary>The console inside.</summary>
        public ConsoleView View { get; }

        public MainWindow(GuiOptions opt)
        {
            Title = "Sidetone";
            Width = opt.Width;
            Height = opt.Height;
            MinWidth = 360;     // narrower than ConsoleView.NarrowWidth is the phone layout
            MinHeight = 420;

            try
            {
                using Stream icon = AppLauncher.OpenIcon("sidetone-256.png");
                if (icon != null)
                    Icon = new WindowIcon(icon);
            }
            catch (Exception) { /* a window without an icon is fine */ }

            View = new ConsoleView(opt);
            Title = View.Title;
            View.TitleChanged += title => Title = title;
            View.QuitRequested += Close;
            View.ListenToKeys(this);
            Content = View;

            Closing += (s, e) => View.CloseConsole();
            Deactivated += (s, e) => View.KeyboardLost();      // the key-up would go to another window

            if (OperatingSystem.IsWindows())
                DesktopNotify.Platform ??= (title, body, urgent) =>
                {
                    Dispatcher.UIThread.Post(Flash);
                    return true;
                };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags, Count, Timeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FlashInfo info);

        /// <summary>Flashes the taskbar button until the window comes to the front (Windows).</summary>
        private void Flash()
        {
            if (IsActive || TryGetPlatformHandle()?.Handle is not IntPtr hwnd)
                return;
            const uint flashAll = 0x3, untilForeground = 0xC;
            FlashInfo info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = hwnd, Flags = flashAll | untilForeground };
            FlashWindowEx(ref info);
        }
    } // public sealed class MainWindow
}
