// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The Instant recall box: a channel's last calls, newest first, each with a Play button, plus the
* channel's Record switch and a way to the recordings: the folder on a desktop, the Recordings box (to share
* them) on a device.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sidetone.Gui
{
    public sealed class RecallOverlay
    {
        /// <summary>Covers the window below the header while the box is open.</summary>
        public readonly Grid Root = new Grid { IsVisible = false };

        private readonly Func<DispatchConsole> console;
        private readonly Action changed, closed, openFolder;

        private readonly Border scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
        private readonly Border card = new Border
        {
            Width = 620,
            Padding = new Thickness(22, 18, 22, 18),
            Margin = new Thickness(0, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly TextBlock title = new TextBlock { Text = "Instant recall", FontSize = 19, FontWeight = FontWeight.Bold };
        private readonly WrapPanel channelRow = new WrapPanel { Orientation = Orientation.Horizontal };
        private readonly StackPanel rows = new StackPanel { Spacing = 4 };
        private readonly TextBlock empty = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly List<(ConsoleChannel Chan, Tap Tap)> channelTaps = new List<(ConsoleChannel, Tap)>();
        private readonly List<(RecalledCall Call, TextBlock Text, Tap Play)> lines = new List<(RecalledCall, TextBlock, Tap)>();
        private readonly Tap record, folder, done;
        private ConsoleChannel shown;
        private int shownVersion = -1;

        public bool IsOpen => Root.IsVisible;
        /// <summary>The channel whose calls are listed.</summary>
        public ConsoleChannel Channel => shown;
        /// <summary>How many calls are listed.</summary>
        public int Count => lines.Count;

        /// <summary>The recordings button: its words, and pressing it (self-check hooks).</summary>
        internal string FolderText => folder.Text;
        internal void PressFolder() => openFolder();

        public RecallOverlay(Func<DispatchConsole> console, Action changed, Action closed, Action openFolder)
        {
            this.console = console;
            this.changed = changed;
            this.closed = closed;
            this.openFolder = openFolder;

            record = new Tap("Record", () => { if (shown != null) console()?.ToggleRecord(shown); changed(); }, fontSize: 12, padX: 10, padY: 5);
            folder = new Tap("Open recordings folder", openFolder, fontSize: 12, padX: 10, padY: 5);
            done = new Tap("Done", Close, fontSize: 13, minWidth: 110, padY: 8);

            note.Text = $"The last {DispatchConsole.RecallPerChannel} calls on each listening channel are kept, including your own, until Sidetone closes. " +
                        "Record also saves every call on the channel to a file.";

            Grid foot = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
            folder.Root.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(record.Root, 0);
            Grid.SetColumn(folder.Root, 1);
            Grid.SetColumn(done.Root, 3);
            foot.Children.Add(record.Root);
            foot.Children.Add(folder.Root);
            foot.Children.Add(done.Root);

            StackPanel head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(title);
            head.Children.Add(channelRow);
            head.Children.Add(note);

            StackPanel list = new StackPanel { Spacing = 4 };
            list.Children.Add(empty);
            list.Children.Add(rows);

            DockPanel dock = new DockPanel();
            DockPanel.SetDock(head, Dock.Top);
            DockPanel.SetDock(foot, Dock.Bottom);
            dock.Children.Add(head);
            dock.Children.Add(foot);
            dock.Children.Add(new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            card.Child = dock;

            scrim.PointerPressed += (s, e) => { e.Handled = true; Close(); };
            card.PointerPressed += (s, e) => e.Handled = true;
            Root.Children.Add(scrim);
            Root.Children.Add(card);
        }

        /// <summary>Opens the box on a channel's calls.</summary>
        public void Show(ConsoleChannel chan)
        {
            DispatchConsole c = console();
            if (c == null)
                return;

            channelRow.Children.Clear();
            channelTaps.Clear();
            foreach (ConsoleChannel ch in c.Channels.Where(ch => ch.IsP25))
            {
                ConsoleChannel target = ch;
                Tap t = new Tap(ch.Cfg.Name, () => { shown = target; shownVersion = -1; changed(); }, fontSize: 12, padX: 10, padY: 4);
                t.Root.Margin = new Thickness(0, 0, 4, 4);
                channelTaps.Add((ch, t));
                channelRow.Children.Add(t.Root);
            }
            shown = chan ?? c.Channels.FirstOrDefault(ch => ch.IsP25);
            shownVersion = -1;
            Root.IsVisible = true;
        }

        public void Close()
        {
            if (!Root.IsVisible)
                return;
            Root.IsVisible = false;
            closed();
        }

        /// <summary>Plays (or stops) the call on a row, 0 being the newest (self-check hook).</summary>
        public void PlayRow(int row)
        {
            if (row >= 0 && row < lines.Count)
                Play(lines[row].Call);
        }

        private void Play(RecalledCall call)
        {
            DispatchConsole c = console();
            if (c == null || shown == null)
                return;
            if (c.Replaying == call)
                c.StopReplay();
            else
                c.Replay(shown, call);
            changed();
        }

        private void Rebuild(DispatchConsole c)
        {
            rows.Children.Clear();
            lines.Clear();
            if (shown == null)
                return;
            foreach (RecalledCall call in c.RecalledCalls(shown))
            {
                RecalledCall target = call;
                TextBlock text = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Tap play = new Tap("Play", () => Play(target), fontSize: 12, minWidth: 70, padX: 10, padY: 3);
                Grid row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                Grid.SetColumn(text, 0);
                Grid.SetColumn(play.Root, 1);
                row.Children.Add(text);
                row.Children.Add(play.Root);
                lines.Add((call, text, play));
                rows.Children.Add(row);
            }
            shownVersion = c.RecallVersion;
        }

        public void Refresh(Palette p)
        {
            if (!Root.IsVisible)
                return;
            DispatchConsole c = console();
            if (c == null)
                return;
            if (shownVersion != c.RecallVersion)
                Rebuild(c);

            card.Background = p.Panel;
            card.BorderBrush = p.Edge;
            title.Foreground = p.Text;
            note.Foreground = p.Dim;
            foreach ((ConsoleChannel ch, Tap t) in channelTaps)
            {
                if (ch == shown)
                    t.Paint(p.Accent, p.OnStrong);
                else
                    t.Paint(Brushes.Transparent, p.Dim, p.Edge);
            }

            empty.IsVisible = lines.Count == 0;
            empty.Foreground = p.Dim;
            empty.Text = shown == null ? string.Empty
                       : shown.Selected ? $"No calls on {shown.Cfg.Name} yet."
                       : $"{shown.Cfg.Name} isn't being listened to. Calls are kept only on listening channels.";

            foreach ((RecalledCall call, TextBlock text, Tap play) in lines)
            {
                bool playing = c.Replaying == call;
                text.Text = $"{call.Started:HH:mm:ss}   {(call.Tx ? "TX" : "RX")}   {call.Label}   {call.Length.TotalSeconds:F0} s, {call.Crypt}" +
                            (playing ? $"   ▶ {c.ReplayPosition.TotalSeconds:F0} s" : string.Empty);
                text.Foreground = playing ? p.Accent : call.Tx ? p.Tx : p.Text;
                play.Text = playing ? "Stop" : "Play";
                if (playing)
                    play.Paint(p.Accent, p.OnStrong);
                else
                    play.Paint(Brushes.Transparent, p.Text, p.Edge);
            }

            bool rec = shown?.Record == true;
            record.Text = rec ? "● Recording" : "Record";
            if (rec)
                record.Paint(p.Tx, p.OnStrong);
            else
                record.Paint(Brushes.Transparent, p.Text, p.Edge);
            folder.Text = GuiPlatform.Current != null ? "Recordings…" : "Open recordings folder";     // a device shares them instead
            folder.Paint(Brushes.Transparent, p.Text, p.Edge);
            done.Paint(p.Accent, p.OnStrong);
        }
    } // public sealed class RecallOverlay
}
