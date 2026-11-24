// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* Zone tabs: the strip of tabs above the cards (one per codeplug zone, in the codeplug's tab
* colours), and the Tabs box, where a zone can be hidden (its channels switched off, as the
* Windows console's Tab Manager does) or shown again. A click takes effect at once.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sidetone.Gui
{
    /// <summary>
    /// The tab strip. Rebuilt for each console; repainted with the window.
    /// </summary>
    public sealed class ZoneTabs
    {
        public readonly Border Root = new Border { Padding = new Thickness(12, 6, 12, 0), BorderThickness = new Thickness(0, 0, 0, 1) };
        private readonly WrapPanel strip = new WrapPanel();
        private readonly List<(ConsoleZone Zone, Tap Tap, TextBlock Mark)> tabs = new List<(ConsoleZone, Tap, TextBlock)>();
        private Tap manage;

        public ZoneTabs() => Root.Child = strip;

        /// <summary>The tab buttons, in order (self-check hook).</summary>
        internal IEnumerable<(ConsoleZone Zone, Tap Tap)> Tabs => tabs.Select(t => (t.Zone, t.Tap));

        /// <summary>Makes one tab per zone, plus the button that opens the Tabs box.</summary>
        public void Build(DispatchConsole c, Action<ConsoleZone> show, Action openBox)
        {
            strip.Children.Clear();
            tabs.Clear();
            if (c == null)
                return;
            foreach (ConsoleZone zone in c.Zones)
            {
                ConsoleZone z = zone;
                Tap t = new Tap(z.Name, () => show(z), fontSize: 13, minWidth: 90, padX: 14, padY: 6);
                t.Root.Margin = new Thickness(0, 0, 4, 6);
                t.Root.CornerRadius = new CornerRadius(Ui.CornerRadius, Ui.CornerRadius, 0, 0);

                // the name, and a dot when there's traffic on the tab
                TextBlock mark = new TextBlock { Text = " ●", FontSize = 13, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
                t.Root.Child = null;
                StackPanel inner = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                inner.Children.Add(t.Label);
                inner.Children.Add(mark);
                t.Root.Child = inner;

                tabs.Add((z, t, mark));
                strip.Children.Add(t.Root);
            }
            manage = new Tap("Tabs…", openBox, fontSize: 12, padX: 10, padY: 6);
            manage.Root.Margin = new Thickness(6, 0, 0, 6);
            strip.Children.Add(manage.Root);
        }

        public void Refresh(DispatchConsole c, Palette p)
        {
            Root.IsVisible = c != null && c.Zones.Count > 1;
            if (!Root.IsVisible)
                return;
            Root.Background = p.Panel;
            Root.BorderBrush = p.Edge;

            ConsoleZone current = c.CurrentZone;
            foreach ((ConsoleZone z, Tap t, TextBlock mark) in tabs)
            {
                t.Root.IsVisible = !z.Hidden;
                if (z.Hidden)
                    continue;
                IBrush colour = Parse(z.TabColor), text = Parse(z.TabTextColor);
                if (z == current)
                {
                    IBrush bg = colour ?? p.Accent;
                    t.Paint(bg, text ?? (colour != null ? Contrast(z.TabColor) : p.OnStrong));
                    t.Label.FontWeight = FontWeight.Bold;
                }
                else
                {
                    t.Paint(Brushes.Transparent, p.Text, colour ?? p.Edge);
                    t.Label.FontWeight = FontWeight.SemiBold;
                }
                ChannelActivity act = c.ZoneActivity(z);
                mark.IsVisible = act != ChannelActivity.Idle;
                mark.Foreground = act == ChannelActivity.Transmitting ? p.Tx : z == current ? t.Label.Foreground : p.Rx;
            }

            int hidden = c.Zones.Count(z => z.Hidden);
            manage.Text = hidden > 0 ? $"{hidden} hidden…" : "Tabs…";
            if (hidden > 0)
                manage.Paint(Brushes.Transparent, p.Warn, p.Warn);
            else
                manage.Paint(Brushes.Transparent, p.Dim, p.Edge);
        }

        /// <summary>A codeplug colour ("#RRGGBB" or "#AARRGGBB"), or null.</summary>
        internal static IBrush Parse(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return null;
            string h = hex.Trim();
            if (!h.StartsWith('#'))
                h = "#" + h;
            return Color.TryParse(h, out Color colour) ? new SolidColorBrush(colour) : null;
        }

        /// <summary>Black or white text, whichever reads better on a colour.</summary>
        internal static IBrush Contrast(string hex)
        {
            if (Parse(hex) is not SolidColorBrush b)
                return Brushes.White;
            Color c = b.Color;
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
            return lum > 0.6 ? Brushes.Black : Brushes.White;
        }
    } // public sealed class ZoneTabs

    /// <summary>
    /// The Tabs box: every zone, with a button to hide it or show it again.
    /// </summary>
    public sealed class ZoneOverlay
    {
        /// <summary>Covers the window below the header while the box is open.</summary>
        public readonly Grid Root = new Grid { IsVisible = false };

        private readonly Func<DispatchConsole> console;
        private readonly Action changed, closed;

        private readonly Border scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
        private readonly Border card = new Border
        {
            Padding = new Thickness(22, 18, 22, 18),
            Margin = new Thickness(0, 10),
            CornerRadius = new CornerRadius(Ui.CornerRadius),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly TextBlock title = new TextBlock { Text = "Tabs", FontSize = 19, FontWeight = FontWeight.Bold };
        private readonly TextBlock note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock message = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        private readonly StackPanel rows = new StackPanel { Spacing = 6 };
        private readonly List<(ConsoleZone Zone, Border Row, Border Swatch, TextBlock Name, TextBlock State, Tap Toggle)> lines = new();
        private readonly Tap done;

        public bool IsOpen => Root.IsVisible;

        /// <summary>The highlighted row (keyboard), and the last refusal shown (self-check hooks).</summary>
        internal int Row { get; private set; }
        internal string Message => message.Text ?? string.Empty;

        public ZoneOverlay(Func<DispatchConsole> console, Action changed, Action closed)
        {
            this.console = console;
            this.changed = changed;
            this.closed = closed;

            note.Text = "Each zone in the codeplug is a tab. Channels on other tabs keep working: they go on listening, patching and recording. " +
                        "A hidden zone's channels are switched off: they stop listening and their traffic isn't shown. Remembered for this system.";
            done = new Tap("Done", Close, fontSize: 13, minWidth: 110, padY: 8);
            WrapPanel buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
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

        /// <summary>Opens the box on the console's zones, with the tab on show highlighted.</summary>
        public void Show()
        {
            DispatchConsole c = console();
            if (c == null)
                return;

            rows.Children.Clear();
            lines.Clear();
            message.Text = string.Empty;
            for (int i = 0; i < c.Zones.Count; i++)
            {
                ConsoleZone zone = c.Zones[i];
                int index = i;
                Border swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(Ui.CornerRadius), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
                TextBlock name = new TextBlock { Text = zone.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, Width = 200, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                TextBlock state = new TextBlock { FontSize = 12, Width = 150, VerticalAlignment = VerticalAlignment.Center };
                Tap toggle = new Tap("Hide", () => Toggle(index), fontSize: 12, minWidth: 70, padX: 10, padY: 4);
                WrapPanel inner = new WrapPanel { ItemSpacing = 10, LineSpacing = 10 };
                inner.Children.Add(swatch);
                inner.Children.Add(name);
                inner.Children.Add(state);
                inner.Children.Add(toggle.Root);
                Border row = new Border { Child = inner, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(Ui.CornerRadius) };
                row.PointerPressed += (s, e) => { Row = index; changed(); };
                lines.Add((zone, row, swatch, name, state, toggle));
                rows.Children.Add(row);
            }
            Row = Math.Max(0, c.CurrentZone.Index);
            Root.IsVisible = true;
        }

        /// <summary>Hides or shows the zone on a row.</summary>
        public void Toggle(int index)
        {
            DispatchConsole c = console();
            if (c == null || index < 0 || index >= lines.Count)
                return;
            Row = index;
            ConsoleZone z = lines[index].Zone;
            string why = c.SetZoneHidden(z, !z.Hidden);
            message.Text = why == null ? string.Empty : char.ToUpperInvariant(why[0]) + why.Substring(1) + ".";
            changed();
        }

        /// <summary>Up and down move the highlight; Space hides or shows the highlighted zone.</summary>
        public void MoveRow(int delta)
        {
            if (lines.Count == 0)
                return;
            Row = Math.Clamp(Row + delta, 0, lines.Count - 1);
            message.Text = string.Empty;
            changed();
        }

        public void ToggleRow() => Toggle(Row);

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
            DispatchConsole c = console();
            card.Background = p.Panel;
            card.BorderBrush = p.Edge;
            title.Foreground = p.Text;
            note.Foreground = p.Dim;
            message.Foreground = p.Tx;
            for (int i = 0; i < lines.Count; i++)
            {
                (ConsoleZone z, Border row, Border swatch, TextBlock name, TextBlock state, Tap toggle) = lines[i];
                row.Background = i == Row ? p.Hover : Brushes.Transparent;
                swatch.Background = ZoneTabs.Parse(z.TabColor) ?? p.Track;
                swatch.BorderBrush = p.Edge;
                name.Foreground = z.Hidden ? p.Dim : p.Text;
                int listening = z.Channels.Count(ch => ch.Selected);
                string count = $"{z.Channels.Count} channel{(z.Channels.Count == 1 ? string.Empty : "s")}";
                state.Text = z.Hidden ? $"hidden, {count} off" : listening > 0 ? $"{count}, {listening} listening" : count;
                state.Foreground = z.Hidden ? p.Warn : p.Dim;
                toggle.Text = z.Hidden ? "Show" : "Hide";
                if (z.Hidden)
                    toggle.Paint(p.Accent, p.OnStrong);
                else
                    toggle.Paint(Brushes.Transparent, p.Text, p.Edge);
            }
            done.Paint(p.Accent, p.OnStrong);
        }
    } // public sealed class ZoneOverlay
}
