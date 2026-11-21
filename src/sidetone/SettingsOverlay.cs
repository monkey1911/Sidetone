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
    public sealed partial class SettingsOverlay
    {
        /// <summary>What the box edits. Null strings mean "default" / "none" / "automatic".</summary>
        public sealed class Values
        {
            public string Output, Input, PttDevice, PttButton, MuteButton;
            public int TxLimit = 120;
            public bool SpaceToggle;
            /// <summary>Display scale (1.0 = 100%), or null for automatic.</summary>
            public double? Scale;
            /// <summary>Desktop notifications when the console is paged.</summary>
            public bool Notifications = true;
            /// <summary>Microphone processing stages, and receive leveling.</summary>
            public bool LowCut = true, AutoLevel = true, NoiseGate, Limiter = true, RxLevel;
            /// <summary>Recording format ("opus" or "wav"), and the days after which recordings are deleted (null: kept).</summary>
            public string RecordingFormat = DispatchConsole.FormatOpus;
            public int? KeepDays;
            /// <summary>Keep the call history between runs, and secure calls' audio in it.</summary>
            public bool KeepHistory = true, HistorySecure;
            /// <summary>Android: the hardware PTT key (its code and name), or null; keep the screen on while connected.</summary>
            public int? HardwarePttKey;
            public string HardwarePttKeyName;
            public bool KeepScreenOn;
        }

        /// <summary>One line in a drop-down list.</summary>
        public sealed class Choice
        {
            public readonly string Value, Label;
            public Choice(string value, string label) { Value = value; Label = label; }
            public override string ToString() => Label;
        }

        public const int MinTxLimit = StartupChoices.MinTxLimit, MaxTxLimit = StartupChoices.MaxTxLimit;
        private const int MaxButtonsListed = 40;    // more than this is a keyboard, not a PTT device

        /// <summary>Covers the window below the header while the box is open.</summary>
        public readonly Grid Root = new Grid { IsVisible = false };
        public readonly ComboBox Output = Combo(), Input = Combo(), PttDevice = Combo(), PttButton = Combo(), MuteButton = Combo();
        public readonly ComboBox Scale = new ComboBox { Width = 150, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left };
        public readonly ComboBox KeepDays = new ComboBox { Width = 150, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left };
        private Tap formatOpus, formatWav, historyOn, historyOff, secureOut, secureIn;
        private readonly TextBlock historyText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        /// <summary>The call history choices as they are in the box.</summary>
        public bool KeepHistory { get; private set; } = true;
        public bool HistorySecure { get; private set; }
        /// <summary>Changes the call history choices (self-check hook).</summary>
        public void SetHistory(bool keep, bool secure)
        {
            KeepHistory = keep;
            HistorySecure = secure;
            message.Text = string.Empty;
        }
        /// <summary>The recording format as chosen in the box.</summary>
        public string RecordingFormat { get; private set; } = DispatchConsole.FormatOpus;
        /// <summary>Picks a recording format (self-check hook).</summary>
        public void SetFormat(string format)
        {
            RecordingFormat = format == DispatchConsole.FormatWav ? DispatchConsole.FormatWav : DispatchConsole.FormatOpus;
            message.Text = string.Empty;
        }
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
        private readonly TextBlock recordingsText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private Tap recChoose, recOpen, recDefault, notifyOn, notifyOff, logCopy, logOpen;
        private readonly List<Tap> routingTaps = new List<Tap>();
        private readonly TextBlock logText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock logResult = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

        /// <summary>Copies the log to the clipboard; returns what happened, as a sentence.</summary>
        public Func<string> CopyLog;
        /// <summary>Opens the folder with the log files.</summary>
        public Action OpenLogFolder;
        /// <summary>Opens the Audio outputs box (outputs per tab and channel).</summary>
        public Action OpenRouting;

        /// <summary>What the Log section says about the log files.</summary>
        public void SetLog(string text)
        {
            logText.Text = text;
            logResult.Text = string.Empty;
        }

        /// <summary>The result of the last Copy log (self-check hook).</summary>
        public string LogResult => logResult.Text ?? string.Empty;

        /// <summary>Presses Copy log (self-check hook).</summary>
        public void PressCopyLog() => logResult.Text = CopyLog?.Invoke() ?? string.Empty;
        private readonly Tap lowCutTap, autoLevelTap, gateTap, limiterTap, rxLevelTap;
        private bool lowCut, autoLevel, gate, limiter, rxLevel;
        /// <summary>The processing switches as they are in the box: low cut, auto level, gate, limiter, receive leveling (self-check hook).</summary>
        public (bool LowCut, bool AutoLevel, bool Gate, bool Limiter, bool RxLevel) Processing => (lowCut, autoLevel, gate, limiter, rxLevel);
        /// <summary>Flips one processing switch: 0 low cut, 1 auto level, 2 gate, 3 limiter, 4 receive leveling (self-check hook).</summary>
        public void FlipProcessing(int which)
        {
            switch (which)
            {
                case 0: lowCut = !lowCut; break;
                case 1: autoLevel = !autoLevel; break;
                case 2: gate = !gate; break;
                case 3: limiter = !limiter; break;
                case 4: rxLevel = !rxLevel; break;
            }
            message.Text = string.Empty;
        }
        /// <summary>Desktop notifications, as chosen in the box.</summary>
        public bool Notify { get; private set; } = true;
        private readonly Tap spaceHold, spaceToggle, openCodeplug, launcherTap, cancel, saveTap, keyChoose, keyNone, keyUnlock;

        private List<InputDeviceInfo> inputDevices = new List<InputDeviceInfo>();
        private bool filling;
        private bool launcherInstalled;

        public bool IsOpen => Root.IsVisible;
        /// <summary>A drop-down list is showing (Esc closes that, not the box).</summary>
        public bool DropDownOpen => Scale.IsDropDownOpen || KeepDays.IsDropDownOpen || Output.IsDropDownOpen || Input.IsDropDownOpen || PttDevice.IsDropDownOpen || PttButton.IsDropDownOpen || MuteButton.IsDropDownOpen;
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
                               Action pickKeyFile, Action clearKeyFile, Action unlockKeyFile,
                               Action pickRecordings, Action defaultRecordings, Action openRecordings)
        {
            lowCutTap = new Tap("Low cut", () => FlipProcessing(0), fontSize: 12, padX: 10, padY: 5);
            autoLevelTap = new Tap("Auto level", () => FlipProcessing(1), fontSize: 12, padX: 10, padY: 5);
            gateTap = new Tap("Noise gate", () => FlipProcessing(2), fontSize: 12, padX: 10, padY: 5);
            limiterTap = new Tap("Limiter", () => FlipProcessing(3), fontSize: 12, padX: 10, padY: 5);
            rxLevelTap = new Tap("Even out talkers' levels", () => FlipProcessing(4), fontSize: 12, padX: 10, padY: 5);
            WrapPanel micRow = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (Tap t in new[] { lowCutTap, autoLevelTap, gateTap, limiterTap })
            {
                t.Root.Margin = new Thickness(0, 0, 4, 0);
                micRow.Children.Add(t.Root);
            }
            rxLevelTap.Root.HorizontalAlignment = HorizontalAlignment.Left;
            recChoose = new Tap("Choose…", () => { Close(); pickRecordings(); }, fontSize: 12, padX: 10, padY: 5);
            recOpen = new Tap("Open", openRecordings, fontSize: 12, padX: 10, padY: 5);
            recDefault = new Tap("Use the default", () => { defaultRecordings(); Close(); }, fontSize: 12, padX: 10, padY: 5);
            StackPanel recRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            recRow.Children.Add(recChoose.Root);
            recRow.Children.Add(recOpen.Root);
            recRow.Children.Add(recDefault.Root);
            notifyOn = new Tap("On", () => { Notify = true; message.Text = string.Empty; }, fontSize: 12, padX: 12, padY: 5);
            notifyOff = new Tap("Off", () => { Notify = false; message.Text = string.Empty; }, fontSize: 12, padX: 12, padY: 5);
            formatOpus = new Tap("Opus (small files)", () => SetFormat(DispatchConsole.FormatOpus), fontSize: 12, padX: 10, padY: 5);
            formatWav = new Tap("WAV", () => SetFormat(DispatchConsole.FormatWav), fontSize: 12, padX: 12, padY: 5);
            StackPanel formatRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            formatRow.Children.Add(formatOpus.Root);
            formatRow.Children.Add(formatWav.Root);
            historyOn = new Tap("Keep", () => SetHistory(true, HistorySecure), fontSize: 12, padX: 12, padY: 5);
            historyOff = new Tap("Don't keep", () => SetHistory(false, HistorySecure), fontSize: 12, padX: 12, padY: 5);
            secureOut = new Tap("Leave their audio out", () => SetHistory(KeepHistory, false), fontSize: 12, padX: 10, padY: 5);
            secureIn = new Tap("Keep their audio too", () => SetHistory(KeepHistory, true), fontSize: 12, padX: 10, padY: 5);
            StackPanel historyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            historyRow.Children.Add(historyOn.Root);
            historyRow.Children.Add(historyOff.Root);
            StackPanel secureRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            secureRow.Children.Add(secureOut.Root);
            secureRow.Children.Add(secureIn.Root);
            historyText.Text = "Recent calls and instant recall's last ten calls per channel are kept when Sidetone closes, so they can be replayed after a restart. " +
                               "A secure call's audio is kept decrypted, so it's left out unless you choose otherwise; its Recent calls entry is always kept. " +
                               "Don't keep deletes the saved history.";
            StackPanel notifyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            notifyRow.Children.Add(notifyOn.Root);
            notifyRow.Children.Add(notifyOff.Root);

            logCopy = new Tap("Copy log", PressCopyLog, fontSize: 12, padX: 10, padY: 5);
            logOpen = new Tap("Open folder", () => OpenLogFolder?.Invoke(), fontSize: 12, padX: 10, padY: 5);
            StackPanel logRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            logRow.Children.Add(logCopy.Root);
            logRow.Children.Add(logOpen.Root);
            logRow.Children.Add(logResult);

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
            Control displayHeading = Heading("Display"), scaleField = Field("Scale", scaleRow);
            stack.Children.Add(displayHeading);
            stack.Children.Add(scaleField);
            stack.Children.Add(Heading("Audio"));
            stack.Children.Add(Field("Output", Output));
            Tap routingTap = new Tap("Per tab and channel…", () => { Close(); OpenRouting?.Invoke(); }, fontSize: 12, padX: 10, padY: 5);
            routingTap.Root.HorizontalAlignment = HorizontalAlignment.Left;
            routingTaps.Add(routingTap);
            stack.Children.Add(Field("Other outputs", routingTap.Root));
            stack.Children.Add(Field("Microphone", Input));
            stack.Children.Add(Field("Mic processing", micRow));
            stack.Children.Add(Field("Receive", rxLevelTap.Root));
            stack.Children.Add(Heading("Push to talk"));
            Control[] pttDeviceFields = { Field("PTT device", PttDevice), Field("PTT button", PttButton), Field("Mute button", MuteButton) };
            foreach (Control field in pttDeviceFields)
                stack.Children.Add(field);
            Control[] deviceRows = BuildDeviceRows();
            foreach (Control row in deviceRows)
                stack.Children.Add(row);
            stack.Children.Add(Field("Space and PTT", spaceRow));
            stack.Children.Add(Field("Time limit", limitRow));
            stack.Children.Add(Heading("Codeplug"));
            stack.Children.Add(codeplugRow);
            stack.Children.Add(Heading("Recordings and notifications"));
            stack.Children.Add(recordingsText);
            stack.Children.Add(recRow);
            stack.Children.Add(Field("Format", formatRow));
            stack.Children.Add(Field("Delete after", KeepDays));
            stack.Children.Add(Field("When paged", notifyRow));
            stack.Children.Add(Heading("Call history"));
            stack.Children.Add(historyText);
            stack.Children.Add(Field("Between runs", historyRow));
            stack.Children.Add(Field("Secure calls", secureRow));
            stack.Children.Add(Heading("Log"));
            stack.Children.Add(logText);
            stack.Children.Add(logRow);
            stack.Children.Add(Heading("Key file"));
            stack.Children.Add(keyFileText);
            stack.Children.Add(keyRow);
            Control launcherHeading = Heading("App launcher");
            stack.Children.Add(launcherHeading);
            stack.Children.Add(launcherRow);

            // a device (Android) has no display scale, input devices, app launcher or folders to open, and has its own PTT key
            bool device = GuiPlatform.Current != null;
            foreach (Control desktopOnly in new[] { displayHeading, scaleField, launcherHeading, launcherRow, recChoose.Root, recOpen.Root, logOpen.Root }.Concat(pttDeviceFields))
                desktopOnly.IsVisible = !device;
            foreach (Control row in deviceRows)
                row.IsVisible = device;
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
                         string keyFile, bool keyFileLocked, bool keyFileChosen, string recordings, bool recordingsChosen)
        {
            recordingsText.Text = $"Recordings go to {recordings}";
            RecordingFormat = current.RecordingFormat == DispatchConsole.FormatWav ? DispatchConsole.FormatWav : DispatchConsole.FormatOpus;
            KeepHistory = current.KeepHistory;
            HistorySecure = current.HistorySecure;
            filling = true;
            Fill(KeepDays, current.KeepDays?.ToString(System.Globalization.CultureInfo.InvariantCulture), "Keep them",
                 DispatchConsole.KeepDayChoices.Select(d => new Choice(d.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{d} days")), "days");
            filling = false;
            recDefault.Root.IsVisible = recordingsChosen;
            Notify = current.Notifications;
            (lowCut, autoLevel, gate, limiter, rxLevel) = (current.LowCut, current.AutoLevel, current.NoiseGate, current.Limiter, current.RxLevel);
            keyFileText.Text = keyFile;
            ShowDeviceValues(current);
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
            StopLearning();
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

        /// <summary>
        /// Sidetone was installed as a package, whose launcher entry is system-wide: say so, and don't
        /// offer to add a second one.
        /// </summary>
        public void SetSystemLauncher(bool system)
        {
            launcherTap.Root.IsVisible = !system;
            if (system)
                launcherState.Text = "Sidetone is in your app launcher (installed as a package).";
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
                SpaceToggle = SpaceToggle,
                Notifications = Notify,
                LowCut = lowCut,
                AutoLevel = autoLevel,
                NoiseGate = gate,
                Limiter = limiter,
                RxLevel = rxLevel,
                RecordingFormat = RecordingFormat,
                KeepHistory = KeepHistory,
                HistorySecure = HistorySecure,
                KeepDays = int.TryParse(Selected(KeepDays), out int days) && days > 0 ? days : null
            };
            CollectDeviceValues(v);
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
            recordingsText.Foreground = p.Dim;
            if (RecordingFormat == DispatchConsole.FormatWav)
            {
                formatWav.Paint(p.Accent, p.OnStrong);
                formatOpus.Paint(Brushes.Transparent, p.Text, p.Edge);
            }
            else
            {
                formatOpus.Paint(p.Accent, p.OnStrong);
                formatWav.Paint(Brushes.Transparent, p.Text, p.Edge);
            }
            logText.Foreground = p.Dim;
            historyText.Foreground = p.Dim;
            (KeepHistory ? historyOn : historyOff).Paint(p.Accent, p.OnStrong);
            (KeepHistory ? historyOff : historyOn).Paint(Brushes.Transparent, p.Text, p.Edge);
            (HistorySecure ? secureIn : secureOut).Paint(HistorySecure ? p.Warn : p.Accent, p.OnStrong);
            (HistorySecure ? secureOut : secureIn).Paint(Brushes.Transparent, p.Text, p.Edge);
            logResult.Foreground = p.Dim;
            logCopy.Paint(Brushes.Transparent, p.Text, p.Edge);
            foreach (Tap t in routingTaps)
                t.Paint(Brushes.Transparent, p.Text, p.Edge);
            logOpen.Paint(Brushes.Transparent, p.Text, p.Edge);
            recChoose.Paint(Brushes.Transparent, p.Text, p.Edge);
            recOpen.Paint(Brushes.Transparent, p.Text, p.Edge);
            recDefault.Paint(Brushes.Transparent, p.Text, p.Edge);
            Paint(notifyOn, Notify, p);
            Paint(lowCutTap, lowCut, p);
            Paint(autoLevelTap, autoLevel, p);
            Paint(gateTap, gate, p);
            Paint(limiterTap, limiter, p);
            Paint(rxLevelTap, rxLevel, p);
            Paint(notifyOff, !Notify, p);
            keyChoose.Paint(Brushes.Transparent, p.Text, p.Edge);
            keyNone.Paint(Brushes.Transparent, p.Text, p.Edge);
            keyUnlock.Paint(p.Accent, p.OnStrong);
            launcherTap.Text = launcherInstalled ? "Remove from app launcher" : "Add to app launcher";
            launcherTap.Paint(Brushes.Transparent, p.Text, p.Edge);
            cancel.Paint(Brushes.Transparent, p.Text, p.Edge);
            saveTap.Paint(p.Accent, p.OnStrong);
            RefreshDevice(p);
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
