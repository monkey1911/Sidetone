// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The Audio outputs box: which output each tab and each channel plays to. A channel follows its
* tab unless it has its own output; a tab follows the main output (Settings' Output) unless it has
* its own. A choice takes effect at once, and is remembered for the system.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sidetone.Gui
{
    public sealed class RoutingOverlay
    {
        /// <summary>Covers the window below the header while the box is open.</summary>
        public readonly Grid Root = new Grid { IsVisible = false };

        private readonly Func<DispatchConsole> console;
        private readonly Func<AudioDeviceList> devices;
        private readonly Action changed, closed;

        private readonly Border scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
        private readonly Border card = new Border
        {
            Width = 640,
            Padding = new Thickness(22, 18, 22, 18),
            Margin = new Thickness(0, 10),
            CornerRadius = new CornerRadius(Ui.CornerRadius),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly TextBlock title = new TextBlock { Text = "Audio outputs", FontSize = 19, FontWeight = FontWeight.Bold };
        private readonly TextBlock note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock main = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel rows = new StackPanel { Spacing = 5 };
        private readonly List<TextBlock> names = new List<TextBlock>();
        private readonly List<(ConsoleZone Zone, ConsoleChannel Chan, ComboBox Box)> boxes = new List<(ConsoleZone, ConsoleChannel, ComboBox)>();
        private readonly Tap done;
        private bool filling;

        public bool IsOpen => Root.IsVisible;

        /// <summary>A drop-down list is showing (Esc closes that, not the box).</summary>
        public bool DropDownOpen => boxes.Any(b => b.Box.IsDropDownOpen);

        /// <summary>The list for a tab, or for a channel (self-check hooks).</summary>
        internal ComboBox ZoneBox(ConsoleZone z) => boxes.FirstOrDefault(b => b.Zone == z && b.Chan == null).Box;
        internal ComboBox ChannelBox(ConsoleChannel c) => boxes.FirstOrDefault(b => b.Chan == c).Box;

        public RoutingOverlay(Func<DispatchConsole> console, Func<AudioDeviceList> devices, Action changed, Action closed)
        {
            this.console = console;
            this.devices = devices;
            this.changed = changed;
            this.closed = closed;

            note.Text = "Each channel plays to its own output if it has one, otherwise to its tab's, otherwise to the main output. " +
                        "Changes take effect at once and are remembered for this system. Replays play where the channel does.";
            done = new Tap("Done", Close, fontSize: 13, minWidth: 110, padY: 8);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(done.Root);

            DockPanel dock = new DockPanel();
            StackPanel head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(title);
            head.Children.Add(note);
            head.Children.Add(main);
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

        /// <summary>Opens the box on the console's tabs and channels and the sound system's outputs.</summary>
        public void Show()
        {
            DispatchConsole c = console();
            if (c == null)
                return;
            AudioDeviceList list = null;
            try { list = devices(); }
            catch (Exception) { /* only what's saved can be shown */ }
            List<AudioDevice> outputs = list?.Outputs ?? new List<AudioDevice>();

            string mainName = c.Audio?.OutputTarget ?? list?.DefaultOutputName;
            string mainLabel = outputs.FirstOrDefault(d => d.Name == mainName)?.Label ?? mainName ?? "the system default";
            main.Text = $"Main output: {mainLabel} (change it under Output in Settings)";

            rows.Children.Clear();
            names.Clear();
            boxes.Clear();
            filling = true;
            foreach (ConsoleZone zone in c.VisibleZones)
            {
                ConsoleZone z = zone;
                if (c.Zones.Count > 1)
                {
                    ComboBox zoneBox = Combo(z.Output, "Main output", outputs);
                    zoneBox.SelectionChanged += (s, e) => { if (!filling) { console()?.SetZoneOutput(z, Value(zoneBox)); changed(); } };
                    rows.Children.Add(Row($"Tab {z.Name}", zoneBox, bold: true, indent: 0));
                    boxes.Add((z, null, zoneBox));
                }
                foreach (ConsoleChannel channel in z.Channels)
                {
                    ConsoleChannel ch = channel;
                    ComboBox box = Combo(ch.Output, c.Zones.Count > 1 ? "Same as tab" : "Main output", outputs);
                    box.SelectionChanged += (s, e) => { if (!filling) { console()?.SetChannelOutput(ch, Value(box)); changed(); } };
                    rows.Children.Add(Row(ch.Cfg.Name, box, bold: false, indent: c.Zones.Count > 1 ? 18 : 0));
                    boxes.Add((z, ch, box));
                }
            }
            filling = false;
            Root.IsVisible = true;
        }

        private static ComboBox Combo(string selected, string inherit, List<AudioDevice> outputs)
        {
            List<SettingsOverlay.Choice> items = new List<SettingsOverlay.Choice> { new SettingsOverlay.Choice(null, inherit) };
            items.AddRange(outputs.Select(d => new SettingsOverlay.Choice(d.Name, d.Label)));
            int index = 0;
            if (selected != null)
            {
                index = items.FindIndex(i => i.Value == selected);
                if (index < 0)
                {
                    items.Add(new SettingsOverlay.Choice(selected, $"{selected}  (not connected)"));
                    index = items.Count - 1;
                }
            }
            return new ComboBox { Width = 300, FontSize = 13, ItemsSource = items, SelectedIndex = index, HorizontalAlignment = HorizontalAlignment.Right };
        }

        private static string Value(ComboBox box) => (box.SelectedItem as SettingsOverlay.Choice)?.Value;

        private Grid Row(string label, ComboBox box, bool bold, double indent)
        {
            TextBlock name = new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(indent, bold ? 6 : 0, 8, 0)
            };
            names.Add(name);
            Grid g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(box, 1);
            g.Children.Add(name);
            g.Children.Add(box);
            return g;
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
            main.Foreground = p.Text;
            foreach (TextBlock n in names)
                n.Foreground = p.Text;
            done.Paint(p.Accent, p.OnStrong);
        }
    } // public sealed class RoutingOverlay
}
