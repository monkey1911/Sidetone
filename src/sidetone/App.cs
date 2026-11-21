// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The Avalonia application: the Fluent theme in its light and dark variants, and the colours
* the console adds on top of it (receive green, transmit red and so on) for each variant.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Sidetone.Gui
{
    public sealed class App : Application
    {
        /// <summary>Command line options, set by Main before the app starts.</summary>
        public static GuiOptions Options = new GuiOptions();

        /// <summary>Makes the desktop window (set by the desktop program; the Android app has none).</summary>
        public static Func<GuiOptions, Window> CreateWindow;

        /// <summary>Raised with the console view when it is the single view (Android).</summary>
        public static event Action<ConsoleView> ViewCreated;

        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = CreateWindow?.Invoke(Options);
            else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            {
                // Android: the console view is the whole app
                ConsoleView view = new ConsoleView(Options);
                single.MainView = view;
                view.AttachedToVisualTree += (s, e) =>
                {
                    if (TopLevel.GetTopLevel(view) is TopLevel top)
                        view.ListenToKeys(top);
                };
                ViewCreated?.Invoke(view);
            }
            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>The theme variant for a setting: "light", "dark", or anything else to follow the desktop.</summary>
        public static ThemeVariant VariantFor(string theme) => (theme ?? string.Empty).ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    } // public sealed class App

    /// <summary>
    /// The console's colours for one theme variant.
    /// </summary>
    public sealed class Palette
    {
        public bool IsDark;
        public IBrush Window, Panel, Card, Edge, Text, Dim, Accent, Rx, Tx, Warn, Busy, OnStrong, Track, Hover;

        private static IBrush B(string hex) => Brush.Parse(hex);

        public static readonly Palette Dark = new Palette
        {
            IsDark = true,
            Window = B("#14161b"),
            Panel = B("#1b1e25"),
            Card = B("#20242c"),
            Edge = B("#363c49"),
            Text = B("#e7e9ee"),
            Dim = B("#8d95a3"),
            Accent = B("#4cc2ff"),
            Rx = B("#3fb950"),
            Tx = B("#f85149"),
            Warn = B("#e3b341"),
            Busy = B("#58a6ff"),
            OnStrong = B("#0d1117"),
            Track = B("#2c313b"),
            Hover = B("#2a2f39")
        };

        public static readonly Palette Light = new Palette
        {
            IsDark = false,
            Window = B("#eef0f3"),
            Panel = B("#ffffff"),
            Card = B("#ffffff"),
            Edge = B("#cfd4dc"),
            Text = B("#1b1f24"),
            Dim = B("#5c6675"),
            Accent = B("#0969da"),
            Rx = B("#1a7f37"),
            Tx = B("#cf222e"),
            Warn = B("#9a6700"),
            Busy = B("#0550ae"),
            OnStrong = B("#ffffff"),
            Track = B("#dde1e7"),
            Hover = B("#e6e9ee")
        };

        public static Palette For(ThemeVariant variant) => variant == ThemeVariant.Dark ? Dark : Light;
    } // public sealed class Palette
}
