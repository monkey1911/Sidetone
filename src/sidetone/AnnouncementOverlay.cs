// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The Announcements box: WAV files to play onto talkgroups, on the focused channel or on every
* listening channel. Files are added with a file dialog and remembered; any common WAV format works.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sidetone.Gui
{
    public sealed class AnnouncementOverlay
    {
        /// <summary>Covers the window below the header while the box is open.</summary>
        public readonly Grid Root = new Grid { IsVisible = false };

        private readonly Func<DispatchConsole> console;
        private readonly Action addFiles, changed, closed;

        private readonly Border scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
        private readonly Border card = new Border
        {
            Width = 700,
            Padding = new Thickness(22, 18, 22, 18),
            Margin = new Thickness(0, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly TextBlock title = new TextBlock { Text = "Announcements", FontSize = 19, FontWeight = FontWeight.Bold };
        private readonly TextBlock note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock message = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel rows = new StackPanel { Spacing = 6 };
        private readonly List<(string Path, Border Row, TextBlock Name, TextBlock Length, Tap Here, Tap All, Tap Remove)> lines = new();
        private readonly Tap add, done;

        public bool IsOpen => Root.IsVisible;

        /// <summary>The highlighted row, the files listed, and the last message (self-check hooks).</summary>
        internal int Row { get; private set; }
        internal int Count => lines.Count;
        internal string Message => message.Text ?? string.Empty;

        public AnnouncementOverlay(Func<DispatchConsole> console, Action addFiles, Action changed, Action closed)
        {
            this.console = console;
            this.addFiles = addFiles;
            this.changed = changed;
            this.closed = closed;

            note.Text = "WAV files to play onto talkgroups, like a pre-recorded message. Any common WAV works; it's converted to radio audio and its level evened out. " +
                        "It goes out like an alert tone: Esc stops it. Enter plays the highlighted one on the focused channel, Shift+Enter on every listening channel.";
            add = new Tap("Add…", () => addFiles(), fontSize: 13, minWidth: 90, padY: 8);
            done = new Tap("Done", Close, fontSize: 13, minWidth: 110, padY: 8);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(add.Root);
            buttons.Children.Add(done.Root);

            DockPanel dock = new DockPanel();
            StackPanel head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(title);
            head.Children.Add(note);
            DockPanel.SetDock(head, Dock.Top);
            StackPanel foot = new StackPanel { Spacing = 6 };
            foot.Children.Add(message);
            foot.Children.Add(buttons);
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

        /// <summary>Opens the box (or refills it after files were added), keeping a message to show.</summary>
        public void Show(string say = null)
        {
            DispatchConsole c = console();
            if (c == null)
                return;
            rows.Children.Clear();
            lines.Clear();
            string focused = c.Channels[c.FocusIndex].Cfg.Name;
            foreach (string file in c.Announcements)
            {
                string path = file;
                TimeSpan? length = c.AnnouncementLength(path);
                TextBlock name = new TextBlock { Text = DispatchConsole.AnnouncementName(path), FontSize = 14, FontWeight = FontWeight.SemiBold, Width = 190, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                TextBlock len = new TextBlock { Text = length == null ? "can't read it" : $"{length.Value.TotalSeconds:F0} s", FontSize = 12, Width = 80, VerticalAlignment = VerticalAlignment.Center };
                Tap here = new Tap($"Play on {focused}", () => Play(path, false), fontSize: 12, padX: 10, padY: 4);
                Tap all = new Tap("Play on all listening", () => Play(path, true), fontSize: 12, padX: 10, padY: 4);
                Tap remove = new Tap("Remove", () => { console()?.RemoveAnnouncement(path); Show(); changed(); }, fontSize: 12, padX: 10, padY: 4);
                here.Root.MaxWidth = 170;
                StackPanel inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                inner.Children.Add(name);
                inner.Children.Add(len);
                inner.Children.Add(here.Root);
                inner.Children.Add(all.Root);
                inner.Children.Add(remove.Root);
                Border row = new Border { Child = inner, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(6) };
                int index = lines.Count;
                row.PointerPressed += (s, e) => { Row = index; changed(); };
                lines.Add((path, row, name, len, here, all, remove));
                rows.Children.Add(row);
            }
            if (lines.Count == 0)
                rows.Children.Add(new TextBlock { Text = "No announcements yet: Add… picks WAV files.", FontSize = 13 });
            Row = Math.Clamp(Row, 0, Math.Max(0, lines.Count - 1));
            message.Text = say ?? string.Empty;
            Root.IsVisible = true;
        }

        /// <summary>Plays a file; the box closes when it has started, and says why when it hasn't.</summary>
        public void Play(string path, bool allListening)
        {
            DispatchConsole c = console();
            if (c == null)
                return;
            string why = c.PlayAnnouncement(path, allListening);
            if (why == null)
            {
                Close();
                changed();
                return;
            }
            message.Text = char.ToUpperInvariant(why[0]) + why.Substring(1) + ".";
            changed();
        }

        /// <summary>Plays the highlighted file (Enter, Shift+Enter).</summary>
        public void PlayRow(bool allListening)
        {
            if (Row >= 0 && Row < lines.Count)
                Play(lines[Row].Path, allListening);
        }

        public void MoveRow(int delta)
        {
            if (lines.Count == 0)
                return;
            Row = Math.Clamp(Row + delta, 0, lines.Count - 1);
            changed();
        }

        public void Close()
        {
            if (!Root.IsVisible)
                return;
            Root.IsVisible = false;
            closed();
        }

        public void Refresh(Palette p)
        {
            if (!Root.IsVisible)
                return;
            card.Background = p.Panel;
            card.BorderBrush = p.Edge;
            title.Foreground = p.Text;
            note.Foreground = p.Dim;
            message.Foreground = p.Tx;
            foreach (TextBlock t in rows.Children.OfType<TextBlock>())
                t.Foreground = p.Dim;
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                l.Row.Background = i == Row ? p.Hover : Brushes.Transparent;
                l.Name.Foreground = p.Text;
                l.Length.Foreground = p.Dim;
                l.Here.Paint(p.Accent, p.OnStrong);
                l.All.Paint(Brushes.Transparent, p.Text, p.Edge);
                l.Remove.Paint(Brushes.Transparent, p.Dim, p.Edge);
            }
            add.Paint(Brushes.Transparent, p.Text, p.Edge);
            done.Paint(p.Accent, p.OnStrong);
        }
    } // public sealed class AnnouncementOverlay
}
