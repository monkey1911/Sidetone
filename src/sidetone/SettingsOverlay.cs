// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The Settings box: audio output and microphone, the PTT device and its buttons, the transmit
* time limit, what Space does, the codeplug, and the app launcher entry. These were command
* line options only; a program started from a launcher has no command line, so they are chosen
* here and remembered. A command line option still wins for the run it was given on.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sidetone.Gui
{
    public sealed class SettingsOverlay
    {
        /// <summary>What the box edits. Null strings mean "default" / "none" / "automatic".</summary>
        public sealed class Values
        {
            public string Output, Input, PttDevice, PttButton, MuteButton;
            public int TxLimit = 120;
            public bool SpaceToggle;
            /// <summary>Display scale (1.0 = 100%), or null for automatic.</summary>
            public double? Scale;
        }

        /// <summary>One line in a drop-down list.</summary>
        public sealed class Choice
        {
            public readonly string Value, Label;
            public Choice(string value, string label) { Value = value; Label = label; }
            public override string ToString() => Label;
        }

        public const int MinTxLimit = 5, MaxTxLimit = 600;
        private const int MaxButtonsListed = 40;    // more than this is a keyboard, not a PTT device

        /// <summary>Covers the window below the header while the box is open.</summary>
        public readonly Grid Root = new Grid { IsVisible = false };
        public readonly ComboBox Output = Combo(), Input = Combo(), PttDevice = Combo(), PttButton = Combo(), MuteButton = Combo();
        public readonly ComboBox Scale = new ComboBox { Width = 150, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock scaleNote = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        private static readonly double[] Scales = { 1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 3.0 };
        public readonly TextBox TxLimit = new TextBox { Width = 90, MaxLength = 3, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Left };

        private readonly Func<Values, string> save;
        private readonly Func<bool, string> launcher;
        private readonly Action closed;

        private readonly Border scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
        private readonly Border card = new Border
        {
            Width = 580,
            Padding = new Thickness(24, 16, 10, 16),
            Margin = new Thickness(0, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly TextBlock title = new TextBlock { Text = "Settings", FontSize = 19, FontWeight = FontWeight.Bold };
        private readonly TextBlock codeplugPath = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock launcherState = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock seconds = new TextBlock { Text = $"seconds ({MinTxLimit} to {MaxTxLimit})", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock overrides = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock message = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly List<TextBlock> labels = new List<TextBlock>();
        private readonly List<TextBlock> headings = new List<TextBlock>();
        private readonly TextBlock keyFileText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly Tap spaceHold, spaceToggle, openCodeplug, launcherTap, cancel, saveTap, keyChoose, keyNone, keyUnlock;

        private List<InputDeviceInfo> inputDevices = new List<InputDeviceInfo>();
        private bool filling;
        private bool launcherInstalled;

        public bool IsOpen => Root.IsVisible;
        /// <summary>A drop-down list is showing (Esc closes that, not the box).</summary>
        public bool DropDownOpen => Scale.IsDropDownOpen || Output.IsDropDownOpen || Input.IsDropDownOpen || PttDevice.IsDropDownOpen || PttButton.IsDropDownOpen || MuteButton.IsDropDownOpen;
        /// <summary>Space starts and stops transmit (as chosen in the box).</summary>
        public bool SpaceToggle { get; private set; }
        /// <summary>The error being shown.</summary>
        public string Message => message.Text ?? string.Empty;
        /// <summary>What the launcher line says.</summary>
        public string LauncherState => launcherState.Text ?? string.Empty;

        /// <param name="save">Applies the values; returns null, or why it couldn't.</param>
        /// <param name="pickCodeplug">Opens the file dialog for another codeplug.</param>
        /// <param name="launcher">Adds (true) or removes (false) the app launcher entry; returns what happened, as a sentence.</param>
        /// <param name="closed">Called after the box closes.</param>
        /// <param name="pickKeyFile">Opens the file dialog for a key file.</param>
        /// <param name="clearKeyFile">Stops using the key file chosen here.</param>
        /// <param name="unlockKeyFile">Opens the password box for the key file.</param>
        public SettingsOverlay(Func<Values, string> save, Action pickCodeplug, Func<bool, string> launcher, Action closed,
                               Action pickKeyFile, Action clearKeyFile, Action unlockKeyFile)
        {
            keyChoose = new Tap("Choose…", () => { Close(); pickKeyFile(); }, fontSize: 12, padX: 10, padY: 5);
            keyNone = new Tap("Stop using it", () => { Close(); clearKeyFile(); }, fontSize: 12, padX: 10, padY: 5);
            keyUnlock = new Tap("Unlock…", () => { Close(); unlockKeyFile(); }, fontSize: 12, padX: 10, padY: 5);
            StackPanel keyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            keyRow.Children.Add(keyChoose.Root);
            keyRow.Children.Add(keyUnlock.Root);
            keyRow.Children.Add(keyNone.Root);

            this.save = save;
            this.launcher = launcher;
            this.closed = closed;

            spaceHold = new Tap("Hold to talk", () => SetSpace(false), fontSize: 12, padX: 10, padY: 5);
            spaceToggle = new Tap("Press to start, press again to stop", () => SetSpace(true), fontSize: 12, padX: 10, padY: 5);
            StackPanel spaceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            spaceRow.Children.Add(spaceHold.Root);
            spaceRow.Children.Add(spaceToggle.Root);

            StackPanel limitRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            limitRow.Children.Add(TxLimit);
            limitRow.Children.Add(seconds);

            openCodeplug = new Tap("Open another…", () => { Close(); pickCodeplug(); }, fontSize: 12, padX: 10, padY: 5);
            Grid codeplugRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(codeplugPath, 0);
            Grid.SetColumn(openCodeplug.Root, 1);
            codeplugRow.Children.Add(codeplugPath);
            codeplugRow.Children.Add(openCodeplug.Root);

            launcherTap = new Tap("Add to app launcher", ToggleLauncher, fontSize: 12, padX: 10, padY: 5);
            launcherTap.Root.HorizontalAlignment = HorizontalAlignment.Left;
            Grid launcherRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            launcherState.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(launcherTap.Root, 0);
            Grid.SetColumn(launcherState, 1);
            launcherRow.Children.Add(launcherTap.Root);
            launcherRow.Children.Add(launcherState);

            PttDevice.SelectionChanged += (s, e) =>
            {
                if (!filling)
                    FillButtons(null, null);
            };
            TxLimit.TextChanged += (s, e) => message.Text = string.Empty;

            cancel = new Tap("Cancel", Close, fontSize: 13, minWidth: 90, padY: 8);
            saveTap = new Tap("Save", Save, fontSize: 13, minWidth: 110, padY: 8);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
            buttons.Children.Add(cancel.Root);
            buttons.Children.Add(saveTap.Root);

            note.Text = "Saving a change to audio, the PTT device or the time limit reconnects the console.";

            // the title and the buttons stay put; what's between them scrolls when the window is short
            StackPanel stack = new StackPanel { Spacing = 7, Margin = new Thickness(0, 0, 16, 0) };
            Grid scaleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            scaleNote.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(Scale, 0);
            Grid.SetColumn(scaleNote, 1);
            scaleRow.Children.Add(Scale);
            scaleRow.Children.Add(scaleNote);
            stack.Children.Add(Heading("Display"));
            stack.Children.Add(Field("Scale", scaleRow));
            stack.Children.Add(Heading("Audio"));
            stack.Children.Add(Field("Output", Output));
            stack.Children.Add(Field("Microphone", Input));
            stack.Children.Add(Heading("Push to talk"));
            stack.Children.Add(Field("PTT device", PttDevice));
            stack.Children.Add(Field("PTT button", PttButton));
            stack.Children.Add(Field("Mute button", MuteButton));
            stack.Children.Add(Field("Space and PTT", spaceRow));
            stack.Children.Add(Field("Time limit", limitRow));
            stack.Children.Add(Heading("Codeplug"));
            stack.Children.Add(codeplugRow);
            stack.Children.Add(Heading("Key file"));
            stack.Children.Add(keyFileText);
            stack.Children.Add(keyRow);
            stack.Children.Add(Heading("App launcher"));
            stack.Children.Add(launcherRow);
            stack.Children.Add(overrides);
            stack.Children.Add(note);

            StackPanel foot = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 14, 0) };
            foot.Children.Add(message);
            foot.Children.Add(buttons);
            title.Margin = new Thickness(0, 0, 0, 4);
            DockPanel dock = new DockPanel();
            DockPanel.SetDock(title, Dock.Top);
            DockPanel.SetDock(foot, Dock.Bottom);
            dock.Children.Add(title);
            dock.Children.Add(foot);
            dock.Children.Add(new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            card.Child = dock;

            scrim.PointerPressed += (s, e) => { e.Handled = true; Close(); };
            card.PointerPressed += (s, e) => e.Handled = true;
            Root.Children.Add(scrim);
            Root.Children.Add(card);
        }

        private static ComboBox Combo() => new ComboBox { Width = 380, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left };

        private TextBlock Heading(string text)
        {
            TextBlock h = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 4, 0, 0) };
            headings.Add(h);
            return h;
        }

        private Grid Field(string label, Control control)
        {
            TextBlock text = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            labels.Add(text);
            Grid g = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*") };
            Grid.SetColumn(text, 0);
            Grid.SetColumn(control, 1);
            g.Children.Add(text);
            g.Children.Add(control);
            return g;
        }

        /*
        ** Opening and closing
        */

        /// <summary>
        /// Opens the box on the current values.
        /// </summary>
        /// <param name="audio">The audio devices, or null when they can't be listed.</param>
        /// <param name="inputs">Input devices that could be a PTT device.</param>
        /// <param name="codeplug">The open codeplug's path, or null.</param>
        /// <param name="commandLine">Options given on the command line that win over these settings, or null.</param>
        /// <param name="keyFile">What to say about the local key file.</param>
        /// <param name="keyFileLocked">There is one and it's waiting for its password.</param>
        /// <param name="keyFileChosen">One was chosen here (so it can be un-chosen).</param>
        public void Show(Values current, AudioDeviceList audio, List<InputDeviceInfo> inputs, string codeplug, string commandLine, bool launcherIsInstalled,
                         string keyFile, bool keyFileLocked, bool keyFileChosen)
        {
            keyFileText.Text = keyFile;
            filling = true;
            Fill(Scale, current.Scale?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "Automatic",
                 Scales.Select(v => new Choice(v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), DisplayScale.Percent(v))), "custom");
            filling = false;
            scaleNote.Text = $"Now: {DisplayScale.InEffect}. A change takes effect the next time Sidetone starts.";
            keyUnlock.Root.IsVisible = keyFileLocked;
            keyNone.Root.IsVisible = keyFileChosen;
            filling = true;
            try
            {
                Fill(Output, current.Output, "System default",
                     audio?.Outputs.Select(d => new Choice(d.Name, d.Label)) ?? Enumerable.Empty<Choice>(), "not connected");
                Fill(Input, current.Input, "System default",
                     audio?.Inputs.Select(d => new Choice(d.Name, d.Label)) ?? Enumerable.Empty<Choice>(), "not connected");

                // one line per device name; a device can show up as several input nodes
                inputDevices = (inputs ?? new List<InputDeviceInfo>())
                    .Where(d => d.EventPath != null && d.Keys.Count > 0 && d.Keys.Count <= MaxButtonsListed)
                    .GroupBy(d => d.Name).Select(g => g.OrderByDescending(d => d.Keys.Count).First()).ToList();
                Fill(PttDevice, current.PttDevice, "None",
                     inputDevices.Select(d => new Choice(d.Name, $"{d.Name}  ({InputDevices.DescribeKeys(d)})")), "not connected");
                FillButtons(current.PttButton, current.MuteButton);
            }
            finally
            {
                filling = false;
            }

            TxLimit.Text = current.TxLimit.ToString();
            SpaceToggle = current.SpaceToggle;
            codeplugPath.Text = codeplug ?? "No codeplug open.";
            overrides.Text = commandLine == null ? string.Empty
                : $"Started with {commandLine}: for this run, the command line wins over what is saved here.";
            overrides.IsVisible = commandLine != null;
            launcherInstalled = launcherIsInstalled;
            launcherState.Text = launcherIsInstalled ? "Sidetone is in your app launcher." : "Start Sidetone from your desktop's app launcher instead of a terminal.";
            message.Text = string.Empty;
            Root.IsVisible = true;
        }

        public void Close()
        {
            if (!Root.IsVisible)
                return;
            Root.IsVisible = false;
            closed();
        }

        /// <summary>Fills a list: a first "nothing chosen" line, the choices, and the saved value if it isn't among them.</summary>
        private static void Fill(ComboBox box, string selected, string nothing, IEnumerable<Choice> choices, string missing)
        {
            List<Choice> items = new List<Choice> { new Choice(null, nothing) };
            items.AddRange(choices);
            int index = 0;
            if (!string.IsNullOrWhiteSpace(selected))
            {
                index = items.FindIndex(c => c.Value != null && c.Value.Equals(selected, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    index = items.FindIndex(c => c.Value != null && c.Label.Contains(selected, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    items.Add(new Choice(selected, $"{selected}  ({missing})"));
                    index = items.Count - 1;
                }
            }
            box.ItemsSource = items;
            box.SelectedIndex = index;
        }

        /// <summary>The button lists follow the chosen PTT device.</summary>
        private void FillButtons(string ptt, string mute)
        {
            bool was = filling;
            filling = true;
            try
            {
                string name = Selected(PttDevice);
                InputDeviceInfo dev = name == null ? null : inputDevices.FirstOrDefault(d => d.Name == name);
                List<Choice> keys = dev == null ? new List<Choice>()
                    : dev.Keys.Select(k => new Choice(InputDevices.ButtonName(k), InputDevices.ButtonName(k))).ToList();
                Fill(PttButton, name == null ? null : ptt, "Automatic", keys, "not on this device");
                Fill(MuteButton, name == null ? null : mute, "Automatic",
                     new[] { new Choice("none", "No mute button") }.Concat(keys), "not on this device");
                PttButton.IsEnabled = MuteButton.IsEnabled = name != null;
            }
            finally
            {
                filling = was;
            }
        }

        private static string Selected(ComboBox box) => (box.SelectedItem as Choice)?.Value;

        private void SetSpace(bool toggle)
        {
            SpaceToggle = toggle;
            message.Text = string.Empty;
        }

        private void ToggleLauncher()
        {
            string result = launcher(!launcherInstalled);
            launcherInstalled = result != null && result.StartsWith("Added", StringComparison.Ordinal) ? true
                              : result != null && result.StartsWith("Removed", StringComparison.Ordinal) ? false
                              : launcherInstalled;
            launcherState.Text = result ?? string.Empty;
        }

        /*
        ** Saving
        */

        /// <summary>Checks and applies what's in the box; it stays open, saying why, when something is wrong.</summary>
        public void Save()
        {
            if (!int.TryParse((TxLimit.Text ?? string.Empty).Trim(), out int limit) || limit < MinTxLimit || limit > MaxTxLimit)
            {
                message.Text = $"The time limit must be {MinTxLimit} to {MaxTxLimit} seconds.";
                return;
            }

            string device = Selected(PttDevice);
            string scaleText = Selected(Scale);
            Values v = new Values
            {
                Scale = scaleText != null && double.TryParse(scaleText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double chosenScale)
                    ? chosenScale : null,
                Output = Selected(Output),
                Input = Selected(Input),
                PttDevice = device,
                PttButton = device == null ? null : Selected(PttButton),
                MuteButton = device == null ? null : Selected(MuteButton),
                TxLimit = limit,
                SpaceToggle = SpaceToggle
            };
            string error = save(v);
            if (error != null)
            {
                message.Text = error;
                return;
            }
            Close();
        }

        /*
        ** Painting
        */

        public void Refresh(Palette p)
        {
            if (!Root.IsVisible)
                return;

            card.Background = p.Panel;
            card.BorderBrush = p.Edge;
            title.Foreground = p.Text;
            foreach (TextBlock h in headings)
                h.Foreground = p.Text;
            foreach (TextBlock label in labels)
                label.Foreground = p.Dim;
            codeplugPath.Foreground = p.Dim;
            launcherState.Foreground = p.Dim;
            seconds.Foreground = p.Dim;
            scaleNote.Foreground = p.Dim;
            note.Foreground = p.Dim;
            overrides.Foreground = p.Warn;
            message.Foreground = p.Tx;

            Paint(spaceHold, !SpaceToggle, p);
            Paint(spaceToggle, SpaceToggle, p);
            openCodeplug.Paint(Brushes.Transparent, p.Text, p.Edge);
            keyFileText.Foreground = p.Dim;
            keyChoose.Paint(Brushes.Transparent, p.Text, p.Edge);
            keyNone.Paint(Brushes.Transparent, p.Text, p.Edge);
            keyUnlock.Paint(p.Accent, p.OnStrong);
            launcherTap.Text = launcherInstalled ? "Remove from app launcher" : "Add to app launcher";
            launcherTap.Paint(Brushes.Transparent, p.Text, p.Edge);
            cancel.Paint(Brushes.Transparent, p.Text, p.Edge);
            saveTap.Paint(p.Accent, p.OnStrong);
        }

        private static void Paint(Tap tap, bool selected, Palette p)
        {
            if (selected)
                tap.Paint(p.Accent, p.OnStrong);
            else
                tap.Paint(Brushes.Transparent, p.Dim, p.Edge);
        }
    } // public sealed class SettingsOverlay
}
