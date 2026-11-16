// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The Patches box: one row per channel, with a button for "not patched" and one per patch.
* A click takes effect at once; there is nothing to save.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sidetone.Gui
{
    public sealed class PatchOverlay
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
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly TextBlock title = new TextBlock { Text = "Patches", FontSize = 19, FontWeight = FontWeight.Bold };
        private readonly TextBlock note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly StackPanel rows = new StackPanel { Spacing = 6 };
        private readonly List<(ConsoleChannel Chan, TextBlock Name, List<Tap> Taps)> lines = new List<(ConsoleChannel, TextBlock, List<Tap>)>();
        private readonly Tap clear, done;

        public bool IsOpen => Root.IsVisible;

        public PatchOverlay(Func<DispatchConsole> console, Action changed, Action closed)
        {
            this.console = console;
            this.changed = changed;
            this.closed = closed;

            note.Text = "Channels in the same patch hear each other: a call on one is sent out on the others, and so is your own PTT. " +
                        "A patched channel is always listening. Patches aren't remembered when Sidetone closes.";
            clear = new Tap("Clear all", () => { console()?.ClearPatches(); changed(); }, fontSize: 13, minWidth: 90, padY: 8);
            done = new Tap("Done", Close, fontSize: 13, minWidth: 110, padY: 8);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(clear.Root);
            buttons.Children.Add(done.Root);

            DockPanel dock = new DockPanel();
            StackPanel head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(title);
            head.Children.Add(note);
            DockPanel.SetDock(head, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(head);
            dock.Children.Add(buttons);
            dock.Children.Add(new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            card.Child = dock;

            scrim.PointerPressed += (s, e) => { e.Handled = true; Close(); };
            card.PointerPressed += (s, e) => e.Handled = true;
            Root.Children.Add(scrim);
            Root.Children.Add(card);
        }

        /// <summary>Opens the box on the console's channels and patches.</summary>
        public void Show()
        {
            DispatchConsole c = console();
            if (c == null)
                return;

            rows.Children.Clear();
            lines.Clear();
            foreach (ConsoleChannel chan in c.Channels)
            {
                if (!DispatchConsole.CanPatch(chan))
                    continue;
                ConsoleChannel channel = chan;
                TextBlock name = new TextBlock { Text = chan.Cfg.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, Width = 170, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                row.Children.Add(name);
                List<Tap> taps = new List<Tap>();
                for (int p = 0; p <= c.PatchNames.Count; p++)
                {
                    int patch = p;
                    Tap t = new Tap(p == 0 ? "Off" : c.PatchNames[p - 1], () => { console()?.SetPatch(channel, patch); changed(); }, fontSize: 12, minWidth: 64, padX: 10, padY: 4);
                    taps.Add(t);
                    row.Children.Add(t.Root);
                }
                lines.Add((chan, name, taps));
                rows.Children.Add(row);
            }
            Root.IsVisible = true;
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
            foreach ((ConsoleChannel chan, TextBlock name, List<Tap> taps) in lines)
            {
                name.Foreground = p.Text;
                for (int i = 0; i < taps.Count; i++)
                {
                    if (chan.Patch == i && i != 0)
                        taps[i].Paint(p.Accent, p.OnStrong);
                    else if (chan.Patch == i)
                        taps[i].Paint(p.Track, p.Text, p.Edge);
                    else
                        taps[i].Paint(Brushes.Transparent, p.Dim, p.Edge);
                }
            }
            clear.Paint(Brushes.Transparent, p.Text, p.Edge);
            done.Paint(p.Accent, p.OnStrong);
        }
    } // public sealed class PatchOverlay
}
