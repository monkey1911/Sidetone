// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The desktop window: a frame around the ConsoleView, which is the whole console (the same view
* is the single view of the Android app). The window gives it a title, the icon, the keyboard,
* and closes the console when it closes.
*/

using Avalonia.Controls;

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
        }
    } // public sealed class MainWindow
}
