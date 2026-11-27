// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The phone layout. Below NarrowWidth the console view changes shape instead of being squeezed:
* the cards go into one column, the header keeps only the system and Settings, and a bar at the
* bottom holds a big hold-to-talk PTT, Tones and Mute all, above buttons for the four pages:
* Channels, Calls (recent calls), Log, and More (everything the desktop's tool bar and header
* hold). Boxes wider than the screen are scaled down to fit. A tablet, or a phone on its side,
* is wide enough for the usual layout.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sidetone.Gui
{
    public sealed partial class ConsoleView
    {
        /// <summary>Narrower than this (in device-independent pixels), the phone layout is used.</summary>
        public const double NarrowWidth = 600;      // Android's own phone/tablet line (smallest width 600 dp): an unfolded Galaxy Fold gets the tablet layout

        /// <summary>Narrower than this, the usual layout puts the header's buttons on a second row (an upright tablet).</summary>
        public const double TwoRowHeaderWidth = 1150;

        private double lastWidth;

        /// <summary>The pages of the phone layout.</summary>
        internal enum NarrowPage { Channels, Calls, Log, More }

        private bool narrow;
        private NarrowPage page = NarrowPage.Channels;

        private readonly Border narrowBar = new Border { Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(0, 1, 0, 0), IsVisible = false };
        private readonly Grid navBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), IsVisible = false };
        private readonly StackPanel morePanel = new StackPanel { Spacing = 10, Margin = new Thickness(14, 12) };
        private ScrollViewer moreScroll;
        private Tap bigPtt, muteAll, tonesTap, navChannels, navCalls, navLog, navMore;
        private Tap morePage, moreTones, moreRadio, moreMarker, morePatch, moreRecall, moreAnnounce, moreTabs, moreMicCheck, moreMicMute, moreSettings;
        private Tap moreThemeSystem, moreThemeLight, moreThemeDark;
        private readonly List<Tap> moreAlerts = new List<Tap>();
        private readonly List<TextBlock> moreHeadings = new List<TextBlock>();
        /// <summary>Every box (its root grid: the scrim, then the card), made a full-screen sheet on a phone (PhoneSheet).</summary>
        private readonly List<Grid> boxes = new List<Grid>();

        /// <summary>The phone layout is on show (self-check hook).</summary>
        internal bool Narrow => narrow;

        /// <summary>The channel cards are on show, not another page (self-check hook).</summary>
        internal bool CardsOnShow => cardScroll.IsEffectivelyVisible;

        /// <summary>The phone layout's page on show (self-check hook).</summary>
        internal NarrowPage PageShown => page;

        /// <summary>The phone layout's big PTT, Mute all, and page buttons (self-check hooks).</summary>
        internal Tap BigPtt => bigPtt;
        internal Tap MuteAllTap => muteAll;
        internal Tap NavTap(NarrowPage which) => which switch
        {
            NarrowPage.Calls => navCalls,
            NarrowPage.Log => navLog,
            NarrowPage.More => navMore,
            _ => navChannels
        };

        /// <summary>A box's card (self-check hook).</summary>
        internal static Control BoxCard(Grid box) => box.Children.Count > 1 ? box.Children[1] : null;

        private void BuildNarrow()
        {
            // the bottom bar: Tones, a big PTT, Mute all
            tonesTap = new Tap("Tones", () => ShowPage(NarrowPage.More), fontSize: 14, minWidth: 76, padX: 8, padY: 20);
            bigPtt = new Tap("PTT", () => PttPressed(null), fontSize: 24, padY: 20) { Released = PttReleased };
            bigPtt.Root.HorizontalAlignment = HorizontalAlignment.Stretch;
            bigPtt.Root.CornerRadius = new CornerRadius(Ui.CornerRadius);
            muteAll = new Tap("Mute all", () => Do(c => c.ToggleGlobalMute()), fontSize: 14, minWidth: 76, padX: 8, padY: 20);
            Grid bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
            Grid.SetColumn(tonesTap.Root, 0);
            Grid.SetColumn(bigPtt.Root, 1);
            Grid.SetColumn(muteAll.Root, 2);
            bar.Children.Add(tonesTap.Root);
            bar.Children.Add(bigPtt.Root);
            bar.Children.Add(muteAll.Root);
            narrowBar.Child = bar;

            // the page buttons
            navChannels = NavButton("Channels", NarrowPage.Channels, 0);
            navCalls = NavButton("Calls", NarrowPage.Calls, 1);
            navLog = NavButton("Log", NarrowPage.Log, 2);
            navMore = NavButton("More", NarrowPage.More, 3);

            // More: what the desktop's tool bar and header hold, as big buttons
            morePanel.Children.Add(Heading("Alert tones"));
            WrapPanel alerts = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
            for (int n = 1; n <= 6; n++)
            {
                int number = n;
                Tap t = new Tap(n.ToString(), () => Do(c => c.SendAlert(number)), fontSize: 17, minWidth: 52, padY: 12);
                moreAlerts.Add(t);
                alerts.Children.Add(t.Root);
            }
            morePanel.Children.Add(alerts);
            moreMarker = Wide("Channel marker", () => Do(c => c.ToggleMarker()));
            morePanel.Children.Add(moreMarker.Root);

            morePanel.Children.Add(Heading("Send and listen"));
            morePage = Wide("Page…", () => overlay.ShowPage(false));
            moreTones = Wide("Tones…", ShowTones);
            moreRadio = Wide("Radio command…", () => overlay.ShowRadio());
            morePatch = Wide("Patch…", () => { overlay.Close(); patchBox.Show(); RefreshNow(); });
            moreRecall = Wide("Recall…", () => { overlay.Close(); recallBox.Show(FocusedChannel()); RefreshNow(); });
            moreAnnounce = Wide("Announce…", () => ShowAnnouncements());
            moreTabs = Wide("Tabs…", ShowZones);
            foreach (Tap t in new[] { morePage, moreTones, moreRadio, morePatch, moreRecall, moreAnnounce, moreTabs })
                morePanel.Children.Add(t.Root);

            morePanel.Children.Add(Heading("Microphone"));
            moreMicCheck = Wide("Mic check", () => Do(c => c.ToggleMicMonitor()));
            moreMicMute = Wide("Mute mic", () => Do(c => c.ToggleMicMute()));
            morePanel.Children.Add(moreMicCheck.Root);
            morePanel.Children.Add(moreMicMute.Root);

            morePanel.Children.Add(Heading("Theme"));
            moreThemeSystem = new Tap("Auto", () => SetTheme(null), fontSize: 15, minWidth: 80, padY: 10);
            moreThemeLight = new Tap("Light", () => SetTheme("light"), fontSize: 15, minWidth: 80, padY: 10);
            moreThemeDark = new Tap("Dark", () => SetTheme("dark"), fontSize: 15, minWidth: 80, padY: 10);
            StackPanel themes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            themes.Children.Add(moreThemeSystem.Root);
            themes.Children.Add(moreThemeLight.Root);
            themes.Children.Add(moreThemeDark.Root);
            morePanel.Children.Add(themes);

            moreSettings = Wide("Settings…", ShowSettings);
            moreSettings.Root.Margin = new Thickness(0, 8, 0, 0);
            morePanel.Children.Add(moreSettings.Root);

            moreScroll = new ScrollViewer { Content = morePanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsVisible = false };

            Grid.SetRow(moreScroll, 2);
            Grid.SetRow(narrowBar, 5);
            Grid.SetRow(navBar, 6);
            consoleView.Children.Add(moreScroll);
            consoleView.Children.Add(narrowBar);
            consoleView.Children.Add(navBar);
        }

        private Tap NavButton(string text, NarrowPage which, int column)
        {
            Tap t = new Tap(text, () => ShowPage(which), fontSize: 13, padX: 2, padY: 14);
            t.Root.HorizontalAlignment = HorizontalAlignment.Stretch;
            t.Root.CornerRadius = new CornerRadius(0);
            t.Root.BorderThickness = new Thickness(0, 3, 0, 0);
            Grid.SetColumn(t.Root, column);
            navBar.Children.Add(t.Root);
            return t;
        }

        private TextBlock Heading(string text)
        {
            TextBlock h = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0) };
            moreHeadings.Add(h);
            return h;
        }

        private Tap Wide(string text, Action click)
        {
            Tap t = new Tap(text, () => { click(); RefreshNow(); }, fontSize: 15, padY: 12);
            t.Root.HorizontalAlignment = HorizontalAlignment.Stretch;
            t.Label.HorizontalAlignment = HorizontalAlignment.Left;
            return t;
        }

        /// <summary>Shows one of the phone layout's pages.</summary>
        internal void ShowPage(NarrowPage which)
        {
            page = which;
            UpdateLayoutMode();
            RefreshNow();
        }

        /// <summary>
        /// Goes back one step on a phone (Android's back gesture): closes the box that's open, else returns to
        /// the channels. Returns false when there was nothing to go back from.
        /// </summary>
        public bool GoBack()
        {
            if (keyBox.IsOpen) { keyBox.Close(); root.Focus(); return true; }
            if (announceBox.IsOpen) { announceBox.Close(); root.Focus(); return true; }
            if (routingBox.IsOpen) { routingBox.Close(); root.Focus(); return true; }
            if (zoneBox.IsOpen) { zoneBox.Close(); root.Focus(); return true; }
            if (recordingsBox.IsOpen) { recordingsBox.Close(); root.Focus(); return true; }
            if (tonesBox.IsOpen) { tonesBox.Escape(); root.Focus(); return true; }
            if (recallBox.IsOpen) { recallBox.Close(); root.Focus(); return true; }
            if (patchBox.IsOpen) { patchBox.Close(); root.Focus(); return true; }
            if (settingsBox.IsOpen) { settingsBox.Close(); root.Focus(); return true; }
            if (overlay.Root.IsVisible) { overlay.Close(); root.Focus(); return true; }
            if (narrow && page != NarrowPage.Channels)
            {
                ShowPage(NarrowPage.Channels);
                return true;
            }
            return false;
        }

        private void WidthChanged(double width)
        {
            lastWidth = width;
            bool n = width > 0 && width < NarrowWidth;
            if (n != narrow)
            {
                narrow = n;
                if (!narrow)
                    page = NarrowPage.Channels;
            }
            UpdateLayoutMode();
            FitBoxes(all: true);
            RefreshNow();
        }

        /// <summary>Shows and hides what each layout has (also when a console opens or closes).</summary>
        private void UpdateLayoutMode()
        {
            bool open = console != null && consoleView.IsVisible;
            bool lists = page == NarrowPage.Calls || page == NarrowPage.Log;

            consoleButtons.IsVisible = open && !narrow;
            offlineTap.Root.IsVisible = open;
            themeRow.IsVisible = !narrow;
            quit.Root.IsVisible = !narrow && !OperatingSystem.IsAndroid();
            details.IsVisible = !narrow;
            header.Padding = narrow ? new Thickness(12, 8) : new Thickness(16, 10);
            bool twoRows = !narrow && lastWidth > 0 && lastWidth < TwoRowHeaderWidth;
            Grid.SetRow(headerRight, twoRows ? 1 : 0);
            Grid.SetColumn(headerRight, twoRows ? 0 : 1);
            Grid.SetColumnSpan(headerRight, twoRows ? 2 : 1);
            headerRight.HorizontalAlignment = twoRows ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            headerRight.Margin = twoRows ? new Thickness(0, 8, 0, 0) : new Thickness(0);
            systemName.FontSize = narrow ? 18 : 20;
            titleRow.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;     // the connection under the name, clear of Settings
            titleRow.Spacing = narrow ? 1 : 14;
            connection.FontSize = narrow ? 12 : 13;
            toolBar.IsVisible = !narrow;
            keyHints.IsVisible = !narrow && !OperatingSystem.IsAndroid();
            txText.TextTrimming = TextTrimming.CharacterEllipsis;
            txBar.Padding = narrow ? new Thickness(12, 6) : new Thickness(16, 7);

            narrowBar.IsVisible = narrow && open;
            navBar.IsVisible = narrow && open;
            cardScroll.IsVisible = !narrow || page == NarrowPage.Channels;
            moreScroll.IsVisible = narrow && page == NarrowPage.More;
            bottom.IsVisible = !narrow || lists;
            recentPanel.IsVisible = !narrow || page == NarrowPage.Calls;
            logPanel.IsVisible = !narrow || page == NarrowPage.Log;
            consoleView.RowDefinitions[2].Height = narrow && lists ? new GridLength(0) : GridLength.Star;
            bool folded = !narrow && BottomCollapsed;
            consoleView.RowDefinitions[4].Height = folded ? GridLength.Auto : !narrow ? new GridLength(230) : lists ? GridLength.Star : new GridLength(0);
            recentScroll.IsVisible = logScroll.IsVisible = !folded;
            recentPanel.Padding = logPanel.Padding = folded ? new Thickness(12, 5) : new Thickness(12, 8);
            SetBottomTitles();
            bottom.ColumnDefinitions[0].Width = !narrow ? new GridLength(2, GridUnitType.Star) : page == NarrowPage.Calls ? GridLength.Star : new GridLength(0);
            bottom.ColumnDefinitions[1].Width = !narrow ? new GridLength(3, GridUnitType.Star) : page == NarrowPage.Log ? GridLength.Star : new GridLength(0);
            recentPanel.Margin = narrow ? new Thickness(8) : new Thickness(12, 8, 6, 12);
            logPanel.Margin = narrow ? new Thickness(8) : new Thickness(6, 8, 12, 12);
            cardPanel.Margin = narrow ? new Thickness(6, 6) : new Thickness(10, 8);
        }

        /// <summary>
        /// Phones get the boxes as full-screen sheets (PhoneSheet), tablets and desktops the boxes as they are: every box when the
        /// layout switches, and the open ones on each refresh (a box builds some rows when it opens).
        /// </summary>
        private void FitBoxes(bool all = false)
        {
            foreach (Grid box in boxes)
                if ((all || box.IsVisible) && BoxCard(box) is Control card)
                    PhoneSheet.Apply(card, narrow);
        }

        private void RefreshNarrow(DispatchConsole c, Palette p, bool tx, ToneJob tone)
        {
            // the cards fill the width on a phone
            double cardWidth = Math.Max(240, cardScroll.Bounds.Width - cardPanel.Margin.Left - cardPanel.Margin.Right - 8 - 2);
            foreach (ChannelCard card in cards)
                card.NarrowWidth = narrow ? cardWidth : null;
            foreach (StreamCard sc in streamCards)
                sc.NarrowWidth = narrow ? cardWidth : null;
            if (!narrow)
                return;

            narrowBar.Background = p.Panel;
            narrowBar.BorderBrush = p.Edge;
            navBar.Background = p.Panel;
            moreScroll.Background = p.Window;

            bigPtt.Text = tone != null ? "STOP TONE" : tx ? "● TX" : "PTT";
            if (tx || tone != null)
                bigPtt.Paint(p.Tx, p.OnStrong);
            else
                bigPtt.Paint(p.Accent, p.OnStrong);
            muteAll.Text = c.GlobalMute ? "MUTED" : "Mute all";
            if (c.GlobalMute)
                muteAll.Paint(p.Warn, p.OnStrong);
            else
                muteAll.Paint(p.Track, p.Text, p.Edge);
            tonesTap.Paint(p.Track, p.Text, p.Edge);

            foreach ((Tap t, NarrowPage which) in new[] { (navChannels, NarrowPage.Channels), (navCalls, NarrowPage.Calls), (navLog, NarrowPage.Log), (navMore, NarrowPage.More) })
            {
                bool on = page == which;
                t.Paint(Brushes.Transparent, on ? p.Accent : p.Dim, on ? p.Accent : Brushes.Transparent);
            }

            if (page != NarrowPage.More)
                return;
            foreach (TextBlock h in moreHeadings)
                h.Foreground = p.Dim;
            for (int i = 0; i < moreAlerts.Count; i++)
            {
                if (tone != null && tone.Plan.Name == $"alert {i + 1}")
                    moreAlerts[i].Paint(p.Tx, p.OnStrong);
                else
                    moreAlerts[i].Paint(p.Card, p.Text, p.Edge);
            }
            foreach (Tap t in new[] { morePage, moreTones, moreRadio, moreTabs, moreSettings })
                t.Paint(p.Card, p.Text, p.Edge);
            PaintState(moreMarker, c.MarkerOn, c.MarkerOn ? "Channel marker on" : "Channel marker", p.Warn, p);
            PaintState(morePatch, c.Channels.Any(ch => ch.Patch != 0), "Patch…", p.Accent, p);
            PaintState(moreRecall, c.Replaying != null, "Recall…", p.Accent, p);
            PaintState(moreAnnounce, tone != null && tone.Plan.Name.StartsWith("announcement ", StringComparison.Ordinal), "Announce…", p.Tx, p);
            PaintState(moreMicCheck, c.MicMonitor, c.MicMonitor ? "Mic check on" : "Mic check", p.Accent, p);
            PaintState(moreMicMute, c.MicMuted, c.MicMuted ? "MIC MUTED" : "Mute mic", p.Tx, p);
            Tap active = themeSetting == "light" ? moreThemeLight : themeSetting == "dark" ? moreThemeDark : moreThemeSystem;
            foreach (Tap t in new[] { moreThemeSystem, moreThemeLight, moreThemeDark })
            {
                if (t == active)
                    t.Paint(p.Accent, p.OnStrong);
                else
                    t.Paint(p.Card, p.Text, p.Edge);
            }
        }

        private static void PaintState(Tap t, bool on, string text, IBrush onColour, Palette p)
        {
            t.Text = text;
            if (on)
                t.Paint(onColour, p.OnStrong);
            else
                t.Paint(p.Card, p.Text, p.Edge);
        }
    } // public sealed partial class ConsoleView
}
