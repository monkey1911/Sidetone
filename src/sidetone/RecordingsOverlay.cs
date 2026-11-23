// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The Recordings box (on a device, from Instant recall's Recordings…): the recordings in the recordings
* folder, a heading per day, newest first, each call with Share and each day with Share day, through the
* platform's share sheet (IGuiPlatform.Share). A desktop opens the folder instead and has no need of it.
*/

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sidetone.Gui
{
    public sealed class RecordingsOverlay
    {
        /// <summary>Covers the window below the header while the box is open.</summary>
        public readonly Grid Root = new Grid { IsVisible = false };

        private readonly Func<string> folder;
        private readonly Action closed;
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
        private readonly TextBlock title = new TextBlock { Text = "Recordings", FontSize = 19, FontWeight = FontWeight.Bold };
        private readonly TextBlock note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock message = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel rows = new StackPanel { Spacing = 4 };
        private readonly List<TextBlock> headings = new List<TextBlock>(), lines = new List<TextBlock>();
        private readonly List<Tap> shares = new List<Tap>();
        private readonly Tap done;
        private List<RecordingFile> files = new List<RecordingFile>();

        public bool IsOpen => Root.IsVisible;
        /// <summary>The recordings listed, newest first (self-check hook).</summary>
        internal IReadOnlyList<RecordingFile> Files => files;
        /// <summary>The days listed, newest first (self-check hook).</summary>
        internal IReadOnlyList<DateTime> Days => files.Select(f => f.Started.Date).Distinct().ToList();
        /// <summary>What the last share said (self-check hook).</summary>
        internal string Message => message.Text ?? string.Empty;

        /// <param name="folder">The recordings folder now.</param>
        public RecordingsOverlay(Func<string> folder, Action closed)
        {
            this.folder = folder;
            this.closed = closed;
            done = new Tap("Done", Close, fontSize: 13, minWidth: 110, padY: 8);
            note.Text = "Share one call, or a whole day's, to send them on: by email, to a drive, to another app.";

            StackPanel head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(title);
            head.Children.Add(note);
            head.Children.Add(message);
            Grid foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
            Grid.SetColumn(done.Root, 1);
            foot.Children.Add(done.Root);

            DockPanel dock = new DockPanel();
            DockPanel.SetDock(head, Dock.Top);
            DockPanel.SetDock(foot, Dock.Bottom);
            dock.Children.Add(head);
            dock.Children.Add(foot);
            dock.Children.Add(new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            card.Child = dock;

            scrim.PointerPressed += (s, e) => { e.Handled = true; Close(); };
            card.PointerPressed += (s, e) => e.Handled = true;
            Root.Children.Add(scrim);
            Root.Children.Add(card);
        }

        /// <summary>Opens the box on the recordings there are now.</summary>
        public void Show()
        {
            message.Text = string.Empty;
            Rebuild();
            Root.IsVisible = true;
        }

        public void Close()
        {
            if (!Root.IsVisible)
                return;
            Root.IsVisible = false;
            closed();
        }

        /// <summary>Shares one recording (by its place in Files) or a whole day's (self-check hooks).</summary>
        internal void ShareFile(int index) => Share(new[] { files[index] }, files[index].Started.ToString("d MMM HH:mm", CultureInfo.InvariantCulture));
        internal void ShareDay(DateTime day) => Share(files.Where(f => f.Started.Date == day.Date).ToList(), DayName(day.Date));

        private void Share(IReadOnlyList<RecordingFile> which, string what)
        {
            IGuiPlatform platform = GuiPlatform.Current;
            if (platform == null || which.Count == 0)
                return;
            bool sent = platform.Share(which.Select(f => f.Path).ToList(), $"Sidetone recordings, {what}");
            message.Text = sent ? string.Empty : "Nothing on this device can take the recordings.";
        }

        /// <summary>"Today, 22 Nov", "Yesterday, 21 Nov", else "Fri 20 Nov".</summary>
        internal static string DayName(DateTime day)
        {
            DateTime today = DateTime.Today;
            string date = day.ToString("d MMM", CultureInfo.InvariantCulture);
            return day == today ? $"Today, {date}" : day == today.AddDays(-1) ? $"Yesterday, {date}" : day.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        }

        private void Rebuild()
        {
            files = Recordings.List(folder());
            rows.Children.Clear();
            headings.Clear();
            lines.Clear();
            shares.Clear();
            if (files.Count == 0)
            {
                TextBlock empty = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Text = "No recordings yet. Turn on Record on a channel's card or in Instant recall." };
                lines.Add(empty);
                rows.Children.Add(empty);
                return;
            }
            foreach (IGrouping<DateTime, RecordingFile> day in files.GroupBy(f => f.Started.Date))
            {
                DateTime date = day.Key;
                TextBlock heading = new TextBlock { Text = DayName(date), FontSize = 14, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
                Tap shareDay = new Tap("Share day", () => ShareDay(date), fontSize: 12, padX: 12, padY: 6);
                headings.Add(heading);
                shares.Add(shareDay);
                rows.Children.Add(Row(heading, shareDay.Root, new Thickness(0, rows.Children.Count == 0 ? 0 : 12, 0, 2)));
                foreach (RecordingFile f in day)
                {
                    int index = files.IndexOf(f);
                    TextBlock line = new TextBlock
                    {
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        Text = $"{f.Started:HH:mm}   {f.Label}"
                    };
                    Tap share = new Tap("Share", () => ShareFile(index), fontSize: 12, padX: 12, padY: 6);
                    lines.Add(line);
                    shares.Add(share);
                    rows.Children.Add(Row(line, share.Root, new Thickness(8, 0, 0, 0)));
                }
            }
        }

        private static Grid Row(Control left, Control right, Thickness margin)
        {
            Grid g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = margin };
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 1);
            right.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(left);
            g.Children.Add(right);
            return g;
        }

        public void Refresh(Palette p)
        {
            if (!Root.IsVisible)
                return;
            card.Background = p.Panel;
            card.BorderBrush = p.Edge;
            title.Foreground = p.Text;
            note.Foreground = p.Dim;
            message.Foreground = p.Warn;
            foreach (TextBlock h in headings)
                h.Foreground = p.Text;
            foreach (TextBlock l in lines)
                l.Foreground = files.Count == 0 ? p.Dim : p.Text;
            foreach (Tap t in shares)
                t.Paint(Brushes.Transparent, p.Text, p.Edge);
            done.Paint(p.Accent, p.OnStrong);
        }
    } // public sealed class RecordingsOverlay
}
