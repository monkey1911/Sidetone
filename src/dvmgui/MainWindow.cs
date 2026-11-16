// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmgui - the graphical front end of the Linux DVM console.
*
* The main window: a header (system, connection, microphone, PTT, mute, theme), the channel
* cards, a transmit bar, and the recent-calls and log panels. It drives a dvmcore
* DispatchConsole and redraws from its state ten times a second, the same way the TUI does,
* so there is no state of its own to get out of step. Everything is painted from a Palette,
* which is how light and dark themes work.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using dvmconsole;

namespace dvmgui
{
    public sealed class MainWindow : Window
    {
        private const int MaxLogLines = 200;
        private const int MaxRecentRows = 50;
        private const double MeterWidth = 150;

        private readonly GuiOptions opt;
        private readonly string settingsPath;
        private readonly AppSettings appSettings;
        private readonly DispatcherTimer timer;
        private readonly FontFamily mono = new FontFamily("JetBrainsMono Nerd Font,JetBrains Mono,DejaVu Sans Mono,Liberation Mono,monospace");

        private string themeSetting;        // "light", "dark" or null (follow the desktop)
        private Palette palette = Palette.Dark;
        private DispatchConsole console;
        private bool offline;               // demo or screenshot: nothing is connected or saved
        private readonly List<ChannelCard> cards = new List<ChannelCard>();
        private long shownLog = -1, shownRecent = -1;
        private Palette shownLogPalette, shownRecentPalette;

        // shell
        private readonly Grid root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Focusable = true };
        private readonly Border header = new Border { Padding = new Thickness(16, 10) };
        private readonly TextBlock systemName = new TextBlock { FontSize = 20, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock connection = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock details = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock micText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 48 };
        private readonly Border micTrack = new Border { Width = MeterWidth, Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
        private readonly Border micFill = new Border { Height = 10, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        private readonly StackPanel micMeter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        private Tap micCheck, micMute, mute, ptt, themeSystem, themeLight, themeDark, quit;
        private readonly StackPanel consoleButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        // console view
        private readonly Grid consoleView = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,230") };
        private readonly Border toolBar = new Border { Padding = new Thickness(16, 7), BorderThickness = new Thickness(0, 0, 0, 1) };
        private readonly TextBlock alertLabel = new TextBlock { Text = "Alert tones", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        private readonly List<Tap> alertTaps = new List<Tap>();
        private Tap pageTap, radioTap, markerTap;
        private CommandOverlay overlay;
        private readonly WrapPanel cardPanel = new WrapPanel { Margin = new Thickness(10, 8) };
        private readonly Border txBar = new Border { Padding = new Thickness(16, 7) };
        private readonly TextBlock txText = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock keyHints = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        private readonly Border recentPanel = new Border { Margin = new Thickness(12, 8, 6, 12), Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        private readonly Border logPanel = new Border { Margin = new Thickness(6, 8, 12, 12), Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        private readonly TextBlock recentTitle = new TextBlock { Text = "Recent calls", FontSize = 13, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 6) };
        private readonly TextBlock logTitle = new TextBlock { Text = "Log", FontSize = 13, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 6) };
        private readonly StackPanel recentRows = new StackPanel();
        private readonly StackPanel logRows = new StackPanel();
        private readonly ScrollViewer logScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        // welcome view
        private readonly StackPanel welcome = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 620 };
        private readonly TextBlock welcomeTitle = new TextBlock { Text = "DVM Console", FontSize = 28, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock welcomeText = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        private readonly TextBlock welcomeError = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        private Tap openCodeplug;

        public MainWindow(GuiOptions opt)
        {
            this.opt = opt;
            Title = "DVM Console";
            Width = opt.Width;
            Height = opt.Height;
            MinWidth = 640;
            MinHeight = 420;

            offline = opt.Demo || opt.Screenshot != null;
            settingsPath = opt.NoSettings || offline ? null : opt.SettingsFile ?? AppSettings.DefaultPath;
            appSettings = AppSettings.Load(settingsPath, out _);
            themeSetting = Normalize(opt.Theme ?? appSettings.Theme);
            if (Application.Current != null)
                Application.Current.RequestedThemeVariant = App.VariantFor(themeSetting);

            BuildShell();
            ActualThemeVariantChanged += (s, e) => ApplyTheme();

            string path = opt.Demo ? DemoCodeplug.Write() : opt.Codeplug ?? appSettings.LastCodeplug;
            if (path != null)
                OpenCodeplug(path, opt.SystemName ?? (opt.Codeplug == null && !opt.Demo ? appSettings.LastSystem : null));
            else
                ShowWelcome(null);
            ApplyTheme();

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (s, e) => OnTick();
            timer.Start();
            Closing += (s, e) => CloseConsole();

            // keyboard: the window sees every key first, whatever has the focus
            spaceRelease = new DispatcherTimer { Interval = SpaceReleaseDelay };
            spaceRelease.Tick += (s, e) => SpaceLetGo();
            AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, HandleKeyUp, RoutingStrategies.Tunnel);
            Deactivated += (s, e) => SpaceLetGo();      // the key-up would go to another window
        }

        /// <summary>The console being shown, or null (self-check hook).</summary>
        internal DispatchConsole ActiveConsole => console;

        /// <summary>The page and radio-command boxes (self-check hook).</summary>
        internal CommandOverlay Overlay => overlay;

        /// <summary>Opens a box by name: page, digital or radio (for screenshots).</summary>
        internal void OpenBox(string which)
        {
            switch (which)
            {
                case "page": overlay.ShowPage(false); break;
                case "digital": overlay.ShowPage(true); break;
                case "radio": overlay.ShowRadio(); break;
            }
        }

        private static string Normalize(string theme) =>
            theme != null && (theme.Equals("light", StringComparison.OrdinalIgnoreCase) || theme.Equals("dark", StringComparison.OrdinalIgnoreCase))
                ? theme.ToLowerInvariant() : null;

        /*
        ** Building the window
        */

        private void BuildShell()
        {
            // header, left: system and connection
            StackPanel titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            titleRow.Children.Add(systemName);
            titleRow.Children.Add(connection);
            StackPanel left = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(titleRow);
            left.Children.Add(details);

            // header, right: microphone, mute, PTT, theme
            micTrack.Child = micFill;
            micMeter.Children.Add(micTrack);
            micMeter.Children.Add(micText);
            micCheck = new Tap("Mic check", () => Do(c => c.ToggleMicMonitor()), fontSize: 12);
            micMute = new Tap("Mute mic", () => Do(c => c.ToggleMicMute()), fontSize: 12);
            mute = new Tap("MUTE", () => Do(c => c.ToggleGlobalMute()), fontSize: 14, minWidth: 84, padY: 11);
            ptt = new Tap("PTT", () => Do(c => c.ToggleTx()), fontSize: 16, minWidth: 120, padY: 11);
            consoleButtons.Children.Add(micMeter);
            consoleButtons.Children.Add(micCheck.Root);
            consoleButtons.Children.Add(micMute.Root);
            consoleButtons.Children.Add(mute.Root);
            consoleButtons.Children.Add(ptt.Root);

            themeSystem = new Tap("Auto", () => SetTheme(null), fontSize: 12, padX: 9, padY: 4);
            themeLight = new Tap("Light", () => SetTheme("light"), fontSize: 12, padX: 9, padY: 4);
            themeDark = new Tap("Dark", () => SetTheme("dark"), fontSize: 12, padX: 9, padY: 4);
            StackPanel themeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            themeRow.Children.Add(themeSystem.Root);
            themeRow.Children.Add(themeLight.Root);
            themeRow.Children.Add(themeDark.Root);

            quit = new Tap("Quit", () => Close(), fontSize: 12, padX: 10, padY: 4);
            quit.Root.Margin = new Thickness(6, 0, 0, 0);

            StackPanel right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(consoleButtons);
            right.Children.Add(themeRow);
            right.Children.Add(quit.Root);

            Grid headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 1);
            headerGrid.Children.Add(left);
            headerGrid.Children.Add(right);
            header.Child = headerGrid;

            // tool bar: alert tones, page, radio command, channel marker
            StackPanel tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            tools.Children.Add(alertLabel);
            for (int n = 1; n <= 6; n++)
            {
                int number = n;
                Tap t = new Tap(n.ToString(), () => Do(c => c.SendAlert(number)), fontSize: 13, minWidth: 34, padX: 8, padY: 4);
                alertTaps.Add(t);
                tools.Children.Add(t.Root);
            }
            pageTap = new Tap("Page…", () => overlay.ShowPage(false), fontSize: 12, padY: 5);
            pageTap.Root.Margin = new Thickness(14, 0, 0, 0);
            radioTap = new Tap("Radio command…", () => overlay.ShowRadio(), fontSize: 12, padY: 5);
            markerTap = new Tap("Channel marker", () => Do(c => c.ToggleMarker()), fontSize: 12, padY: 5);
            markerTap.Root.Margin = new Thickness(14, 0, 0, 0);
            tools.Children.Add(pageTap.Root);
            tools.Children.Add(radioTap.Root);
            tools.Children.Add(markerTap.Root);
            toolBar.Child = tools;

            overlay = new CommandOverlay(() => console, RefreshNow, () => root.Focus());

            // console view: cards, transmit bar, recent calls and log
            ScrollViewer cardScroll = new ScrollViewer { Content = cardPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            keyHints.Text = $"Space: PTT ({(opt.SpaceToggle ? "start/stop" : "hold")})    Enter: listen    M: mute    U: mute mic    V: mic check    Ctrl+Q: quit";
            Grid txGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(txText, 0);
            Grid.SetColumn(keyHints, 1);
            txGrid.Children.Add(txText);
            txGrid.Children.Add(keyHints);
            txBar.Child = txGrid;

            DockPanel recentDock = new DockPanel();
            DockPanel.SetDock(recentTitle, Dock.Top);
            recentDock.Children.Add(recentTitle);
            recentDock.Children.Add(new ScrollViewer { Content = recentRows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            recentPanel.Child = recentDock;

            DockPanel logDock = new DockPanel();
            DockPanel.SetDock(logTitle, Dock.Top);
            logDock.Children.Add(logTitle);
            logScroll.Content = logRows;
            logDock.Children.Add(logScroll);
            logPanel.Child = logDock;

            Grid bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*") };
            Grid.SetColumn(recentPanel, 0);
            Grid.SetColumn(logPanel, 1);
            bottom.Children.Add(recentPanel);
            bottom.Children.Add(logPanel);

            Grid.SetRow(toolBar, 0);
            Grid.SetRow(cardScroll, 1);
            Grid.SetRow(txBar, 2);
            Grid.SetRow(bottom, 3);
            consoleView.Children.Add(toolBar);
            consoleView.Children.Add(cardScroll);
            consoleView.Children.Add(txBar);
            consoleView.Children.Add(bottom);

            // welcome view
            openCodeplug = new Tap("Open codeplug…", () => _ = PickCodeplug(), fontSize: 15, minWidth: 180, padY: 10);
            openCodeplug.Root.HorizontalAlignment = HorizontalAlignment.Center;
            welcome.Children.Add(welcomeTitle);
            welcome.Children.Add(welcomeText);
            welcome.Children.Add(openCodeplug.Root);
            welcome.Children.Add(welcomeError);

            Grid.SetRow(header, 0);
            Grid.SetRow(consoleView, 1);
            Grid.SetRow(welcome, 1);
            Grid.SetRow(overlay.Root, 1);
            root.Children.Add(header);
            root.Children.Add(consoleView);
            root.Children.Add(welcome);
            root.Children.Add(overlay.Root);     // last, so it's on top
            Content = root;
        }

        /// <summary>Runs a console action from a button and redraws straight away.</summary>
        private void Do(Action<DispatchConsole> action)
        {
            if (console == null)
                return;
            action(console);
            RefreshNow();
        }

        /*
        ** Opening and closing a console
        */

        private void ShowWelcome(string error)
        {
            consoleView.IsVisible = false;
            consoleButtons.IsVisible = false;
            welcome.IsVisible = true;
            systemName.Text = "DVM Console";
            connection.Text = string.Empty;
            details.Text = "no codeplug open";
            welcomeText.Text = "Open the codeplug you use with the Windows console or dvmcli. " +
                               "You can also start with: dvmgui --codeplug FILE";
            welcomeError.Text = error ?? string.Empty;
        }

        private async Task PickCodeplug()
        {
            try
            {
                IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open codeplug",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("Codeplug (YAML)") { Patterns = new[] { "*.yml", "*.yaml" } } }
                });
                if (files == null || files.Count == 0)
                    return;
                string path = files[0].TryGetLocalPath();
                if (path == null)
                {
                    ShowWelcome("That file isn't on this computer's file system.");
                    return;
                }
                OpenCodeplug(path, null);
                ApplyTheme();
            }
            catch (Exception ex)
            {
                ShowWelcome($"Couldn't open a file dialog ({ex.Message}). Start with: dvmgui --codeplug FILE");
            }
        }

        /// <summary>
        /// Loads a codeplug and starts its console; on any problem, shows the welcome view with the reason.
        /// </summary>
        private void OpenCodeplug(string path, string wantedSystem)
        {
            CloseConsole();
            try
            {
                Codeplug cp = CodeplugLoader.Load(path);
                Codeplug.System system;
                try { system = CodeplugLoader.FindSystem(cp, wantedSystem); }
                catch (ArgumentException) when (wantedSystem != null && opt.SystemName == null) { system = cp.Systems[0]; }   // a remembered system that's gone

                if (opt.PttDevice != null)
                    _ = new PttDeviceWatcher(opt.PttDevice, opt.PttButton, opt.MuteButton);    // checks the button names
                AudioSelection audio = offline ? null : AudioSelection.Choose(opt.Output, opt.Input);

                if (settingsPath != null)
                {
                    appSettings.LastCodeplug = Path.GetFullPath(path);
                    appSettings.LastSystem = system.Name;
                    appSettings.Theme = themeSetting;
                    try { appSettings.Save(settingsPath); } catch { /* not worth stopping for */ }
                }

                DispatchConsole c = new DispatchConsole(cp, system, TimeSpan.FromSeconds(opt.TxLimitSeconds),
                                                        string.IsNullOrWhiteSpace(opt.LogFile) ? null : Path.GetFullPath(opt.LogFile),
                                                        null, settingsPath)
                {
                    Audio = audio,
                    NoAudio = offline,
                    ControlSocketPath = offline || opt.NoSocket ? null : opt.Socket != null ? Path.GetFullPath(opt.Socket) : ControlServer.DefaultPath,
                    PttDevice = !offline && opt.PttDevice != null ? (opt.PttDevice, opt.PttButton, opt.MuteButton) : null
                };

                if (offline)
                    DemoCodeplug.Populate(c);
                else
                {
                    if (PipeWireSink.FindPlayer() == null)
                        c.AddLog("warning: pw-play not found; you'll see activity but hear nothing");
                    if (PipeWireCapture.FindRecorder() == null)
                        c.AddLog("warning: pw-record not found; transmit won't work");
                    c.WorkQueued += () => Dispatcher.UIThread.Post(() =>
                    {
                        if (console != c)
                            return;
                        c.RunControlCalls();
                        RefreshNow();
                    });
                    c.Start();
                }
                console = c;

                cards.Clear();
                cardPanel.Children.Clear();
                for (int i = 0; i < c.Channels.Count; i++)
                {
                    ChannelCard card = new ChannelCard(c, i, RefreshNow);
                    cards.Add(card);
                    cardPanel.Children.Add(card.Root);
                }
                shownLog = shownRecent = -1;

                welcome.IsVisible = false;
                consoleView.IsVisible = true;
                consoleButtons.IsVisible = true;
                Title = $"DVM Console: {system.Name}";
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is InvalidOperationException || ex is YamlDotNet.Core.YamlException)
            {
                console = null;
                ShowWelcome($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>Stops the console (when the window closes or another codeplug is opened).</summary>
        public void CloseConsole()
        {
            overlay?.Close();
            DispatchConsole c = console;
            console = null;
            if (c == null || offline)
                return;
            try { c.Shutdown(); }
            catch { /* closing anyway */ }
        }

        /*
        ** Themes
        */

        private void SetTheme(string theme)
        {
            themeSetting = Normalize(theme);
            if (Application.Current != null)
                Application.Current.RequestedThemeVariant = App.VariantFor(themeSetting);

            appSettings.Theme = themeSetting;
            if (console != null && !offline)
            {
                console.Settings.Theme = themeSetting;
                console.SettingsChanged();
            }
            else if (settingsPath != null)
            {
                try { appSettings.Save(settingsPath); } catch { /* not worth stopping for */ }
            }
            ApplyTheme();
        }

        /// <summary>Repaints everything for the theme in effect (chosen here, or the desktop's).</summary>
        private void ApplyTheme()
        {
            palette = Palette.For(ActualThemeVariant);
            Palette p = palette;

            Background = p.Window;
            header.Background = p.Panel;
            header.BorderBrush = p.Edge;
            header.BorderThickness = new Thickness(0, 0, 0, 1);
            systemName.Foreground = p.Text;
            details.Foreground = p.Dim;
            micTrack.Background = p.Track;
            micText.Foreground = p.Dim;
            toolBar.Background = p.Panel;
            toolBar.BorderBrush = p.Edge;
            alertLabel.Foreground = p.Dim;

            foreach (Border panel in new[] { recentPanel, logPanel })
            {
                panel.Background = p.Panel;
                panel.BorderBrush = p.Edge;
            }
            recentTitle.Foreground = p.Text;
            logTitle.Foreground = p.Text;

            welcomeTitle.Foreground = p.Text;
            welcomeText.Foreground = p.Dim;
            welcomeError.Foreground = p.Tx;
            openCodeplug.Paint(p.Accent, p.OnStrong);
            quit.Paint(Brushes.Transparent, p.Dim, p.Edge);

            Tap active = themeSetting == "light" ? themeLight : themeSetting == "dark" ? themeDark : themeSystem;
            foreach (Tap t in new[] { themeSystem, themeLight, themeDark })
            {
                if (t == active)
                    t.Paint(p.Accent, p.OnStrong);
                else
                    t.Paint(Brushes.Transparent, p.Dim, p.Edge);
            }

            RefreshNow();
        }

        /*
        ** Keeping the window up to date
        */

        private void OnTick()
        {
            if (console != null && !offline)
                console.Tick();
            RefreshNow();
        }

        /// <summary>Redraws from the console's current state.</summary>
        public void RefreshNow()
        {
            DispatchConsole c = console;
            Palette p = palette;
            if (c == null || ptt == null)
                return;

            // header
            systemName.Text = c.SystemConfig.Name;
            bool connected = c.Connected;
            connection.Text = offline ? "● demo (not connected)" : connected ? "● connected" : "○ connecting…";
            connection.Foreground = offline ? p.Dim : connected ? p.Rx : p.Warn;
            details.Text = $"peer {c.SystemConfig.PeerId}   RID {c.ConsoleRid}   keys {c.Keys.Count}   {c.Aliases.Summary}";

            bool tx = c.Transmitting;
            ToneJob tone = c.ActiveTone;
            ptt.Text = tone != null ? "STOP TONE" : tx ? "● TX" : "PTT";
            if (tx || tone != null)
                ptt.Paint(p.Tx, p.OnStrong);
            else
                ptt.Paint(p.Accent, p.OnStrong);

            mute.Text = c.GlobalMute ? "MUTED" : "MUTE";
            if (c.GlobalMute)
                mute.Paint(p.Warn, p.OnStrong);
            else
                mute.Paint(Brushes.Transparent, p.Text, p.Edge);

            micMute.Text = c.MicMuted ? "MIC MUTED" : "Mute mic";
            if (c.MicMuted)
                micMute.Paint(p.Tx, p.OnStrong);
            else
                micMute.Paint(Brushes.Transparent, p.Text, p.Edge);

            micCheck.Text = c.MicMonitor ? "Mic check on" : "Mic check";
            if (c.MicMonitor)
                micCheck.Paint(p.Accent, p.OnStrong);
            else
                micCheck.Paint(Brushes.Transparent, p.Text, p.Edge);

            // microphone meter: shown while the mic is open
            bool micOpen = (tx || c.MicMonitor) && !c.MicMuted;
            micMeter.IsVisible = micOpen;
            if (micOpen)
            {
                LevelMeter.Reading m = c.MicLevel.Read();
                double frac = m.Active ? Math.Clamp((m.LevelDb - LevelMeter.FloorDb) / -LevelMeter.FloorDb, 0, 1) : 0;
                micFill.Width = frac * MeterWidth;
                micFill.Background = m.Clipping || m.LevelDb > -6 ? p.Tx : m.LevelDb > -18 ? p.Warn : p.Rx;
                micText.Text = !m.Active ? "--" : m.Clipping ? "CLIP" : $"{Math.Round(m.LevelDb):F0} dB";
                micText.Foreground = m.Clipping ? p.Tx : p.Dim;
            }

            // tool bar
            for (int i = 0; i < alertTaps.Count; i++)
            {
                if (tone != null && tone.Plan.Name == $"alert {i + 1}")
                    alertTaps[i].Paint(p.Tx, p.OnStrong);
                else
                    alertTaps[i].Paint(Brushes.Transparent, p.Text, p.Edge);
            }
            pageTap.Paint(Brushes.Transparent, p.Text, p.Edge);
            radioTap.Paint(Brushes.Transparent, p.Text, p.Edge);
            markerTap.Text = c.MarkerOn ? "Channel marker on" : "Channel marker";
            if (c.MarkerOn)
                markerTap.Paint(p.Warn, p.OnStrong);
            else
                markerTap.Paint(Brushes.Transparent, p.Text, p.Edge);

            foreach (ChannelCard card in cards)
                card.Refresh(p);
            overlay.Refresh(p);

            RefreshTxBar(c, p, tx, tone);
            RefreshRecent(c, p);
            RefreshLog(c, p);
        }

        private void RefreshTxBar(DispatchConsole c, Palette p, bool tx, ToneJob tone)
        {
            if (tone != null)
            {
                txBar.Background = p.Tx;
                txText.Foreground = p.OnStrong;
                txText.Text = $"SENDING {tone.Plan.Name.ToUpperInvariant()} on {string.Join(", ", tone.Channels)}";
                keyHints.Foreground = p.OnStrong;
                return;
            }

            P25Transmitter t = c.Transmitter;
            ConsoleChannel on = c.TxChannel;
            if (tx && t != null)
            {
                string warn = c.MicMuted ? "     MIC MUTED" : c.NoAudioWarning ? "     NO AUDIO FROM THE MICROPHONE" : string.Empty;
                txBar.Background = p.Tx;
                txText.Foreground = p.OnStrong;
                txText.Text = $"● TRANSMITTING   {on?.Cfg.Name}   tg {t.Talkgroup}   {(t.Encrypted ? KeyStore.AlgorithmName(t.AlgorithmId) : "clear")}   " +
                              $"{DateTime.Now - t.StartedAt:mm\\:ss} / {c.TxLimit:mm\\:ss}{warn}";
                keyHints.Foreground = p.OnStrong;
                return;
            }

            ConsoleChannel focus = c.Channels[c.FocusIndex];
            txBar.Background = p.Panel;
            keyHints.Foreground = p.Dim;
            txText.Foreground = c.MicMuted ? p.Tx : p.Dim;
            txText.Text = (focus.IsP25
                ? $"PTT transmits on {focus.Cfg.Name} (tg {focus.Tg}, {(focus.Encrypted ? KeyStore.AlgorithmName(focus.TxAlg) : "clear")})"
                : $"{focus.Cfg.Name}: no transmit on {focus.Cfg.Mode}") + (c.MicMuted ? "     MIC MUTED" : string.Empty);
        }

        /*
        ** Keyboard
        */

        /// <summary>
        /// How long after Space's key-up the release counts. A held key repeats, and on X11 each repeat
        /// arrives as a key-up followed at once by a key-down; waiting this long tells those from letting go.
        /// </summary>
        private static readonly TimeSpan SpaceReleaseDelay = TimeSpan.FromMilliseconds(70);

        private readonly DispatcherTimer spaceRelease;
        private bool spaceHeld;

        /// <summary>How many times Space has been pressed and let go, repeats not counted (self-check hooks).</summary>
        internal int SpacePresses { get; private set; }
        internal int SpaceReleases { get; private set; }

        private void HandleKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (ctrl && e.Key == Key.Q)
            {
                e.Handled = true;
                Close();
                return;
            }

            // a box is open: Esc and Enter are its; everything else is typing
            if (overlay.Open != CommandOverlay.Kind.None)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    overlay.Close();
                }
                else if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    overlay.Enter();
                }
                return;
            }

            DispatchConsole c = console;
            if (c == null || ctrl || e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
                return;
            ConsoleChannel focus = c.Channels[c.FocusIndex];

            int alert = e.Key >= Key.D1 && e.Key <= Key.D6 ? e.Key - Key.D1 + 1
                      : e.Key >= Key.NumPad1 && e.Key <= Key.NumPad6 ? e.Key - Key.NumPad1 + 1 : 0;
            if (alert != 0)
            {
                e.Handled = true;
                c.SendAlert(alert);
                RefreshNow();
                return;
            }

            if (e.Key == Key.Space)
            {
                e.Handled = true;
                spaceRelease.Stop();        // a repeat, not a release
                if (spaceHeld)
                    return;
                spaceHeld = true;
                SpacePresses++;
                if (opt.SpaceToggle)
                    c.ToggleTx();
                else
                    c.HoldToTalk(true);
                RefreshNow();
                return;
            }

            bool plus = e.Key == Key.OemPlus || e.Key == Key.Add || e.KeySymbol == "+" || e.KeySymbol == "=";
            bool minus = e.Key == Key.OemMinus || e.Key == Key.Subtract || e.KeySymbol == "-";
            if (plus)
                c.Louder(focus);
            else if (minus)
                c.Quieter(focus);
            else
            {
                switch (e.Key)
                {
                    case Key.Escape:
                        if (c.ActiveTone != null)
                            c.CancelTones();
                        else if (c.Transmitting)
                            c.StopTx(null);
                        break;
                    case Key.Enter: c.ToggleSelect(focus); break;
                    case Key.Left: MoveFocus(c, -1); break;
                    case Key.Right: MoveFocus(c, 1); break;
                    case Key.Up: MoveFocus(c, -Columns()); break;
                    case Key.Down: MoveFocus(c, Columns()); break;
                    case Key.M:
                        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                            c.ToggleGlobalMute();
                        else
                            c.ToggleMute(focus);
                        break;
                    case Key.U: c.ToggleMicMute(); break;
                    case Key.V: c.ToggleMicMonitor(); break;
                    case Key.K: c.RequestKey(focus); break;
                    case Key.A: c.RefreshAliases(); break;
                    case Key.T: c.ToggleTalkPermit(); break;
                    case Key.C: c.ToggleMarker(); break;
                    case Key.P: overlay.ShowPage(false); break;
                    case Key.D: overlay.ShowPage(true); break;
                    case Key.R: overlay.ShowRadio(); break;
                    default: return;
                }
            }
            e.Handled = true;
            RefreshNow();
        }

        private void HandleKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || !spaceHeld)
                return;
            e.Handled = true;
            spaceRelease.Stop();
            spaceRelease.Start();   // counts as let go unless a key-down follows at once
        }

        /// <summary>Space has really been let go (or the window lost the keyboard).</summary>
        private void SpaceLetGo()
        {
            spaceRelease.Stop();
            if (!spaceHeld)
                return;
            spaceHeld = false;
            SpaceReleases++;
            if (!opt.SpaceToggle)
                console?.HoldToTalk(false);
            RefreshNow();
        }

        /// <summary>How many cards fit across the window.</summary>
        private int Columns() => Math.Max(1, (int)(cardPanel.Bounds.Width / (ChannelCard.CardWidth + 12)));

        private void MoveFocus(DispatchConsole c, int delta)
        {
            int target = c.FocusIndex + delta;
            if (target < 0 || target >= c.Channels.Count)
                return;
            c.SetFocus(target);
            if (target < cards.Count)
                cards[target].Root.BringIntoView();
        }

        private void RefreshRecent(DispatchConsole c, Palette p)
        {
            if (c.RecentVersion == shownRecent && shownRecentPalette == p)
                return;
            shownRecent = c.RecentVersion;
            shownRecentPalette = p;

            recentRows.Children.Clear();
            var rows = c.SnapshotRecent(MaxRecentRows);
            if (rows.Count == 0)
                recentRows.Children.Add(new TextBlock { Text = "none yet", FontSize = 12, Foreground = p.Dim });
            foreach (var call in rows)
            {
                StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 1) };
                row.Children.Add(new TextBlock { Text = call.Time, FontSize = 12, FontFamily = mono, Foreground = p.Dim });
                row.Children.Add(new TextBlock { Text = call.Tx ? "TX" : "RX", FontSize = 12, FontFamily = mono, FontWeight = FontWeight.Bold, Foreground = call.Tx ? p.Tx : p.Rx });
                row.Children.Add(new TextBlock { Text = call.Chan, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = p.Text, MinWidth = 86 });
                row.Children.Add(new TextBlock { Text = call.Who, FontSize = 12, Foreground = p.Text });
                row.Children.Add(new TextBlock { Text = call.Detail, FontSize = 12, Foreground = p.Dim });
                recentRows.Children.Add(row);
            }
        }

        private void RefreshLog(DispatchConsole c, Palette p)
        {
            if (c.LogVersion == shownLog && shownLogPalette == p)
                return;
            shownLog = c.LogVersion;
            shownLogPalette = p;

            logRows.Children.Clear();
            foreach (string line in c.SnapshotLog(MaxLogLines))
            {
                IBrush colour = line.Contains("TX ") ? p.Tx
                    : line.Contains(": RX ") ? p.Rx
                    : line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
                      line.Contains("not transmitting") || line.Contains("no audio") ? p.Warn
                    : p.Text;
                logRows.Children.Add(new TextBlock { Text = line, FontSize = 12, FontFamily = mono, Foreground = colour, TextTrimming = TextTrimming.CharacterEllipsis });
            }
            Dispatcher.UIThread.Post(logScroll.ScrollToEnd, DispatcherPriority.Background);
        }
    } // public sealed class MainWindow

    /// <summary>
    /// A made-up system for --demo and screenshots: nothing here is real, and nothing is connected.
    /// </summary>
    public static class DemoCodeplug
    {
        private const string Yaml = @"
systems:
  - name: ""Demo System""
    identity: ""DEMO""
    address: ""127.0.0.1""
    port: 62031
    peerId: 9000001
    rid: ""9000001""
    password: ""DEMO""
    encrypted: false
zones:
  - name: ""Demo""
    channels:
      - name: ""Dispatch""
        system: ""Demo System""
        tgid: ""100""
        mode: ""p25""
        algo: ""aes""
        keyId: 0x101
      - name: ""Tac 1""
        system: ""Demo System""
        tgid: ""101""
        mode: ""p25""
      - name: ""Tac 2""
        system: ""Demo System""
        tgid: ""102""
        mode: ""p25""
      - name: ""Calling""
        system: ""Demo System""
        tgid: ""104""
        mode: ""p25""
      - name: ""Weather""
        system: ""Demo System""
        tgid: ""105""
        mode: ""p25""
        rx_only: true
      - name: ""Events""
        system: ""Demo System""
        tgid: ""106""
        mode: ""p25""
        algo: ""aes""
        keyId: 0x102
      - name: ""DMR Link""
        system: ""Demo System""
        tgid: ""200""
        mode: ""dmr""
";

        /// <summary>Writes the demo codeplug to a temporary file and returns its path.</summary>
        public static string Write()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dvmgui-demo-{Environment.ProcessId}.yml");
            File.WriteAllText(path, Yaml);
            return path;
        }

        /// <summary>Gives the demo console something to show: listening channels, a muted one, log lines and calls.</summary>
        public static void Populate(DispatchConsole c)
        {
            c.AddLog("demo: a made-up system; nothing is connected and nothing is transmitted");
            c.Select(c.Channels[0]);
            c.Select(c.Channels[1]);
            c.Select(c.Channels[3]);
            c.ToggleMute(c.Channels[3]);
            c.Louder(c.Channels[1]);
            c.SetFocus(1);
            c.Channels[1].LastCall = "Unit 12 at 14:02 (6 s)";
            c.Channels[2].Active = true;
            c.Channels[2].ActiveSrc = 3041;
            c.AddLog("Tac 1: RX Unit 12 (3012)");
            c.AddLog("Tac 1: end Unit 12 (3012), 6.2 s, clear");
            c.AddLog("Tac 1: TX start as 9000001 (clear)");
            c.AddLog("Tac 1: TX end, 3.4 s, 170 frames");
            c.AddLog("warning: this is what a warning looks like");
            c.AddRecent("Tac 1", "9000001", "3 s, clear", tx: true);
            c.AddRecent("Tac 1", "Unit 12", "6 s, clear", tx: false);
        }
    } // public static class DemoCodeplug
}
