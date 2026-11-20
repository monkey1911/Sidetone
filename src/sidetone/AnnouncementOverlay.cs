// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The Announcements box: WAV files and spoken texts to play onto talkgroups, on the focused channel
* or on every listening channel, and the time announcement (words before and after, 12- or 24-hour,
* a time zone, and a schedule). Spoken ones need a text-to-speech engine (Piper or espeak-ng),
* installed separately; "Hear it" plays them through the vocoder on the speakers without sending.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sidetone.Core;
using Choice = Sidetone.Gui.SettingsOverlay.Choice;

namespace Sidetone.Gui
{
    public sealed class AnnouncementOverlay
    {
        /// <summary>Covers the window below the header while the box is open.</summary>
        public readonly Grid Root = new Grid { IsVisible = false };

        private enum Kind { File, Spoken, Time }

        private sealed class Line
        {
            public Kind Kind;
            public string Value;
            public Border Row;
            public TextBlock Name, Length;
            public List<Tap> Strong = new List<Tap>(), Plain = new List<Tap>(), Quiet = new List<Tap>();
        }

        private readonly Func<DispatchConsole> console;
        private readonly Action addFiles, changed, closed;

        private readonly Border scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
        private readonly Border card = new Border
        {
            Width = 820,
            Padding = new Thickness(22, 18, 22, 18),
            Margin = new Thickness(0, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly TextBlock title = new TextBlock { Text = "Announcements", FontSize = 19, FontWeight = FontWeight.Bold };
        private readonly TextBlock note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock speech = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock message = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel files = new StackPanel { Spacing = 6 }, spoken = new StackPanel { Spacing = 6 };
        private readonly List<Line> lines = new List<Line>();
        private readonly List<TextBlock> headings = new List<TextBlock>(), labels = new List<TextBlock>();
        private readonly Tap add, done, lookAgain, addText, h12, h24, zoneYes, zoneNo, hearTime, playTime, playTimeAll;

        private readonly ComboBox engine = new ComboBox { Width = 200, FontSize = 13 }, voice = new ComboBox { Width = 260, FontSize = 13 };
        private readonly ComboBox speed = new ComboBox { Width = 200, FontSize = 13 };
        private readonly TextBox newText = new TextBox { FontSize = 13, Width = 560, PlaceholderText = "Type what to say, like: Shift change in ten minutes" };
        private readonly TextBox before = new TextBox { FontSize = 13, Width = 300 }, after = new TextBox { FontSize = 13, Width = 520, PlaceholderText = "(nothing)" };
        private readonly TextBox zoneName = new TextBox { FontSize = 13, Width = 200, PlaceholderText = "its abbreviation" };
        private readonly ComboBox zone = new ComboBox { Width = 300, FontSize = 13 }, every = new ComboBox { Width = 230, FontSize = 13 }, onChannel = new ComboBox { Width = 230, FontSize = 13 };
        private readonly TextBlock willSay = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock next = new TextBlock { FontSize = 12 };

        private bool filling;

        public bool IsOpen => Root.IsVisible;

        /// <summary>The highlighted row, the rows listed (files, spoken texts, the time), and the last message (self-check hooks).</summary>
        internal int Row { get; private set; }
        internal int Count => lines.Count;
        internal int FileCount => lines.Count(l => l.Kind == Kind.File);
        internal int SpokenCount => lines.Count(l => l.Kind == Kind.Spoken);
        internal string Message => message.Text ?? string.Empty;
        internal string WillSay => willSay.Text ?? string.Empty;
        internal TextBox NewText => newText;
        internal TextBox Before => before;
        internal void AddTypedText() => AddText();
        internal void Use24Hour(bool on) => SetClock(on);

        /// <summary>True when a text box in the box has the keyboard (so keys are typing, not shortcuts).</summary>
        public bool Typing =>
            newText.IsFocused || before.IsFocused || after.IsFocused || zoneName.IsFocused ||
            new[] { engine, voice, speed, zone, every, onChannel }.Any(c => c.IsFocused || c.IsDropDownOpen);

        public AnnouncementOverlay(Func<DispatchConsole> console, Action addFiles, Action changed, Action closed)
        {
            this.console = console;
            this.addFiles = addFiles;
            this.changed = changed;
            this.closed = closed;

            note.Text = "WAV files and spoken texts to play onto talkgroups, and the time announcement. They go out like an alert tone: Esc stops one. " +
                        "Enter plays the highlighted one on the focused channel, Shift+Enter on every listening channel.";
            add = new Tap("Add WAV files…", () => addFiles(), fontSize: 13, padY: 8);
            done = new Tap("Done", Close, fontSize: 13, minWidth: 110, padY: 8);
            lookAgain = new Tap("Look again", () => { console()?.RefreshSpeechEngine(); Show(); changed(); }, fontSize: 12, padX: 10, padY: 4);
            addText = new Tap("Add", AddText, fontSize: 12, padX: 14, padY: 5);
            h12 = new Tap("12-hour", () => SetClock(false), fontSize: 12, padX: 10, padY: 4);
            h24 = new Tap("24-hour", () => SetClock(true), fontSize: 12, padX: 10, padY: 4);
            zoneYes = new Tap("Say it", () => SetSayZone(true), fontSize: 12, padX: 10, padY: 4);
            zoneNo = new Tap("Don't", () => SetSayZone(false), fontSize: 12, padX: 10, padY: 4);
            hearTime = new Tap("Hear it", () => Hear(console()?.PreviewTimeAnnouncement()), fontSize: 12, padX: 10, padY: 4);
            playTime = new Tap("Play on focused", () => PlayTime(false), fontSize: 12, padX: 10, padY: 4);
            playTimeAll = new Tap("Play on all listening", () => PlayTime(true), fontSize: 12, padX: 10, padY: 4);

            engine.ItemsSource = new List<Choice> { new Choice(null, "Automatic"), new Choice(Speech.PiperSetting, "Piper"), new Choice(Speech.EspeakSetting, "espeak-ng") };
            engine.SelectionChanged += (s, e) => SaveEngine();
            voice.SelectionChanged += (s, e) => SaveEngine();
            speed.ItemsSource = Speech.Rates.Select(r => new Choice(r.ToString(System.Globalization.CultureInfo.InvariantCulture), Speech.RateName(r))).ToList();
            speed.SelectionChanged += (s, e) => SaveSpeed();
            newText.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    AddText();
                }
            };
            foreach (TextBox t in new[] { before, after, zoneName })
                t.TextChanged += (s, e) => SaveTime();
            foreach (ComboBox c in new[] { zone, every, onChannel })
                c.SelectionChanged += (s, e) => SaveTime();

            StackPanel content = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 18, 0) };

            content.Children.Add(Heading("Text to speech"));
            content.Children.Add(FieldRow(speech));
            content.Children.Add(FieldRow(Label("Engine"), engine, Label("Voice", false), voice, lookAgain.Root));
            content.Children.Add(FieldRow(Label("Speed"), speed, Label("(Hear it on a spoken announcement or the time to try it)", false)));

            content.Children.Add(Heading("WAV files"));
            content.Children.Add(files);

            content.Children.Add(Heading("Spoken"));
            content.Children.Add(spoken);
            content.Children.Add(FieldRow(newText, addText.Root));

            content.Children.Add(Heading("Time announcement"));
            content.Children.Add(FieldRow(Label("Before the time"), before, Label("Clock", false), h12.Root, h24.Root));
            content.Children.Add(FieldRow(Label("After the time"), after));
            content.Children.Add(FieldRow(Label("Time zone"), zone, Label("Say the zone", false), zoneYes.Root, zoneNo.Root));
            content.Children.Add(FieldRow(Label("Zone said as"), zoneName, Label("(empty: its abbreviation, letter by letter, like P S T)", false)));
            content.Children.Add(FieldRow(Label("By itself"), every, Label("On", false), onChannel));
            content.Children.Add(FieldRow(Label("Will say"), willSay));
            content.Children.Add(FieldRow(Label(string.Empty), hearTime.Root, playTime.Root, playTimeAll.Root, next));

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
            dock.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            card.Child = dock;

            scrim.PointerPressed += (s, e) => { e.Handled = true; Close(); };
            card.PointerPressed += (s, e) => e.Handled = true;
            Root.Children.Add(scrim);
            Root.Children.Add(card);
        }

        private TextBlock Heading(string text)
        {
            TextBlock t = new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) };
            headings.Add(t);
            return t;
        }

        /// <summary>A label: at the start of a row (a fixed width, so the fields line up) or between fields.</summary>
        private TextBlock Label(string text, bool first = true)
        {
            TextBlock t = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            if (first)
                t.Width = 110;
            labels.Add(t);
            return t;
        }

        private static StackPanel FieldRow(params Control[] items)
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (Control c in items)
                row.Children.Add(c);
            return row;
        }

        /// <summary>Opens the box (or refills it), keeping a message to show.</summary>
        public void Show(string say = null)
        {
            DispatchConsole c = console();
            if (c == null)
                return;
            filling = true;
            try
            {
                lines.Clear();
                files.Children.Clear();
                spoken.Children.Clear();
                string focused = c.Channels[c.FocusIndex].Cfg.Name;
                foreach (string file in c.Announcements)
                    files.Children.Add(AddLine(c, Kind.File, file, focused));
                if (c.Announcements.Count == 0)
                    files.Children.Add(Dim("None yet: Add WAV files… picks some."));
                foreach (string text in c.SpokenAnnouncements)
                    spoken.Children.Add(AddLine(c, Kind.Spoken, text, focused));
                if (c.SpokenAnnouncements.Count == 0)
                    spoken.Children.Add(Dim("None yet: type something below and Add."));
                lines.Add(new Line { Kind = Kind.Time });
                playTime.Text = $"Play on {focused}";

                // the engine
                speech.Text = c.SpeechStatus.Replace("Text to speech: ", string.Empty);
                engine.SelectedIndex = c.Settings?.SpeechEngine == Speech.PiperSetting ? 1 : c.Settings?.SpeechEngine == Speech.EspeakSetting ? 2 : 0;
                List<string> voices = Speech.PiperVoices();
                List<Choice> voiceChoices = new List<Choice> { new Choice(null, voices.Count == 0 ? "(no Piper voices found)" : $"First found ({Speech.VoiceName(voices[0])})") };
                voiceChoices.AddRange(voices.Select(v => new Choice(v, Speech.VoiceName(v))));
                voice.ItemsSource = voiceChoices;
                voice.SelectedIndex = Math.Max(0, voiceChoices.FindIndex(v => v.Value != null && v.Value == c.Settings?.SpeechVoice));
                speed.SelectedIndex = Math.Max(0, Array.IndexOf(Speech.Rates, c.SpeechRate));

                // the time announcement
                TimeAnnouncementSettings t = c.TimeAnnouncement;
                before.Text = t.Before ?? string.Empty;
                after.Text = t.After ?? string.Empty;
                zoneName.Text = t.ZoneName ?? string.Empty;
                List<Choice> zones = new List<Choice> { new Choice(null, $"This computer's ({TimeZoneInfo.Local.Id})") };
                zones.AddRange(Speech.ZoneIds().Select(id => new Choice(id, id)));
                zone.ItemsSource = zones;
                zone.SelectedIndex = Math.Max(0, zones.FindIndex(z => z.Value != null && z.Value == t.TimeZone));
                List<Choice> intervals = Speech.Intervals.Select(m => new Choice(m.ToString(), m == 0 ? "Off (only when played)" : $"Every {m} minutes")).ToList();
                every.ItemsSource = intervals;
                every.SelectedIndex = Math.Max(0, Array.IndexOf(Speech.Intervals, t.EveryMinutes));
                List<Choice> channels = new List<Choice> { new Choice(null, "Every listening channel") };
                channels.AddRange(c.Channels.Where(x => x.IsP25 && !x.Cfg.RxOnly).Select(x => x.Cfg.Name).Distinct().Select(n => new Choice(n, n)));
                if (t.Channel != null && channels.All(x => x.Value != t.Channel))
                    channels.Add(new Choice(t.Channel, $"{t.Channel} (not in this system)"));
                onChannel.ItemsSource = channels;
                onChannel.SelectedIndex = Math.Max(0, channels.FindIndex(x => x.Value != null && x.Value == t.Channel));
                clock24 = t.Hour24;
                sayZone = t.SayZone;
            }
            finally
            {
                filling = false;
            }
            Row = Math.Clamp(Row, 0, lines.Count - 1);
            UpdateTime();
            message.Text = say ?? string.Empty;
            Root.IsVisible = true;
        }

        private TextBlock Dim(string text)
        {
            TextBlock t = new TextBlock { Text = text, FontSize = 13 };
            labels.Add(t);
            return t;
        }

        private Border AddLine(DispatchConsole c, Kind kind, string value, string focused)
        {
            Line l = new Line { Kind = kind, Value = value };
            TimeSpan? length = kind == Kind.File ? c.AnnouncementLength(value) : c.SpokenLength(value);
            l.Name = new TextBlock
            {
                Text = kind == Kind.File ? DispatchConsole.AnnouncementName(value) : $"“{DispatchConsole.SpokenName(value)}”",
                FontSize = 14, FontWeight = FontWeight.SemiBold, Width = kind == Kind.File ? 220 : 260,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(l.Name, value);
            l.Length = new TextBlock
            {
                Text = kind == Kind.File ? (length == null ? "can't read it" : $"{length.Value.TotalSeconds:F0} s") : (length == null ? "spoken" : $"{length.Value.TotalSeconds:F0} s"),
                FontSize = 12, Width = 70, VerticalAlignment = VerticalAlignment.Center
            };
            Tap here = new Tap($"Play on {focused}", () => PlayLine(l, false), fontSize: 12, padX: 10, padY: 4);
            here.Root.MaxWidth = 170;
            Tap all = new Tap("Play on all listening", () => PlayLine(l, true), fontSize: 12, padX: 10, padY: 4);
            Tap remove = new Tap("Remove", () =>
            {
                DispatchConsole dc = console();
                if (kind == Kind.File) dc?.RemoveAnnouncement(value); else dc?.RemoveSpokenAnnouncement(value);
                Show();
                changed();
            }, fontSize: 12, padX: 10, padY: 4);
            l.Strong.Add(here);
            l.Plain.Add(all);
            StackPanel inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            inner.Children.Add(l.Name);
            inner.Children.Add(l.Length);
            inner.Children.Add(here.Root);
            inner.Children.Add(all.Root);
            if (kind == Kind.Spoken)
            {
                Tap hear = new Tap("Hear it", () => Hear(console()?.PreviewSpeech(value)), fontSize: 12, padX: 10, padY: 4);
                l.Plain.Add(hear);
                inner.Children.Add(hear.Root);
            }
            l.Quiet.Add(remove);
            inner.Children.Add(remove.Root);
            l.Row = new Border { Child = inner, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(6) };
            int index = lines.Count;
            l.Row.PointerPressed += (s, e) => { Row = index; changed(); };
            lines.Add(l);
            return l.Row;
        }

        /*
        ** Actions
        */

        private void Say(string why)
        {
            message.Text = why == null ? string.Empty : char.ToUpperInvariant(why[0]) + why.Substring(1) + (why.EndsWith('.') ? string.Empty : ".");
            changed();
        }

        private void Started(string why)
        {
            if (why == null)
            {
                Close();
                changed();
                return;
            }
            Say(why);
        }

        private void Hear(string why) => Say(why ?? "playing it on the speakers as a radio would hear it (nothing is sent)");

        private void PlayLine(Line l, bool allListening)
        {
            DispatchConsole c = console();
            if (c == null)
                return;
            Started(l.Kind == Kind.File ? c.PlayAnnouncement(l.Value, allListening)
                  : l.Kind == Kind.Spoken ? c.SpeakAnnouncement(l.Value, allListening)
                  : c.PlayTimeAnnouncement(allListening));
        }

        private void PlayTime(bool allListening)
        {
            DispatchConsole c = console();
            if (c != null)
                Started(c.PlayTimeAnnouncement(allListening));
        }

        private void AddText()
        {
            DispatchConsole c = console();
            if (c == null)
                return;
            string why = c.AddSpokenAnnouncement(newText.Text);
            if (why == null)
            {
                newText.Text = string.Empty;
                Show();
                Row = lines.FindLastIndex(l => l.Kind == Kind.Spoken);
            }
            Say(why);
        }

        private void SaveEngine()
        {
            DispatchConsole c = console();
            if (filling || c == null)
                return;
            c.SetSpeechEngine((engine.SelectedItem as Choice)?.Value, (voice.SelectedItem as Choice)?.Value);
            speech.Text = c.SpeechStatus.Replace("Text to speech: ", string.Empty);
            changed();
        }

        private void SaveSpeed()
        {
            DispatchConsole c = console();
            if (filling || c == null || speed.SelectedIndex < 0)
                return;
            Say(c.SetSpeechRate(Speech.Rates[speed.SelectedIndex]));
            Show(message.Text);
            changed();
        }

        /// <summary>Picks a speed, as the list would (self-check hook).</summary>
        internal void ChooseSpeed(double rate) => speed.SelectedIndex = Array.IndexOf(Speech.Rates, rate);

        private bool clock24, sayZone = true;

        private void SetClock(bool on)
        {
            clock24 = on;
            SaveTime();
        }

        private void SetSayZone(bool on)
        {
            sayZone = on;
            SaveTime();
        }

        private TimeAnnouncementSettings Edited()
        {
            int minutes = int.TryParse((every.SelectedItem as Choice)?.Value, out int m) ? m : 0;
            return new TimeAnnouncementSettings
            {
                Before = before.Text ?? string.Empty,
                After = string.IsNullOrWhiteSpace(after.Text) ? null : after.Text,
                TimeZone = (zone.SelectedItem as Choice)?.Value,
                Hour24 = clock24,
                SayZone = sayZone,
                ZoneName = zoneName.Text,
                EveryMinutes = minutes,
                Channel = (onChannel.SelectedItem as Choice)?.Value
            };
        }

        private void SaveTime()
        {
            DispatchConsole c = console();
            if (filling || c == null)
                return;
            string why = c.SetTimeAnnouncement(Edited());
            if (why != null)
                Say(why);
            UpdateTime();
            changed();
        }

        private void UpdateTime()
        {
            DispatchConsole c = console();
            if (c == null)
                return;
            willSay.Text = Speech.TimeAnnouncementPreview(Edited(), DateTime.UtcNow);
            DateTime? due = c.NextTimeAnnouncement;
            TimeZoneInfo tz = Speech.Zone(c.TimeAnnouncement.TimeZone);
            next.Text = due == null ? string.Empty : $"Next by itself: {TimeZoneInfo.ConvertTimeFromUtc(due.Value, tz):HH:mm}";
        }

        /// <summary>Plays the highlighted row (Enter, Shift+Enter).</summary>
        public void PlayRow(bool allListening)
        {
            if (Row >= 0 && Row < lines.Count)
                PlayLine(lines[Row], allListening);
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
            DispatchConsole c = console();
            speech.Foreground = c != null && c.CurrentSpeechEngine(out _) == null ? p.Warn : p.Dim;
            willSay.Foreground = p.Text;
            next.Foreground = p.Dim;
            foreach (TextBlock t in headings)
                t.Foreground = p.Text;
            foreach (TextBlock t in labels)
                t.Foreground = p.Dim;
            for (int i = 0; i < lines.Count; i++)
            {
                Line l = lines[i];
                if (l.Row == null)
                    continue;
                l.Row.Background = i == Row ? p.Hover : Brushes.Transparent;
                l.Name.Foreground = p.Text;
                l.Length.Foreground = p.Dim;
                foreach (Tap t in l.Strong) t.Paint(p.Accent, p.OnStrong);
                foreach (Tap t in l.Plain) t.Paint(Brushes.Transparent, p.Text, p.Edge);
                foreach (Tap t in l.Quiet) t.Paint(Brushes.Transparent, p.Dim, p.Edge);
            }
            bool timeRow = Row == lines.Count - 1;
            playTime.Paint(timeRow ? p.Accent : Brushes.Transparent, timeRow ? p.OnStrong : p.Text, p.Edge);
            playTimeAll.Paint(Brushes.Transparent, p.Text, p.Edge);
            hearTime.Paint(Brushes.Transparent, p.Text, p.Edge);
            PaintChoice(h12, !clock24, p);
            PaintChoice(h24, clock24, p);
            PaintChoice(zoneYes, sayZone, p);
            PaintChoice(zoneNo, !sayZone, p);
            addText.Paint(p.Accent, p.OnStrong);
            lookAgain.Paint(Brushes.Transparent, p.Dim, p.Edge);
            add.Paint(Brushes.Transparent, p.Text, p.Edge);
            done.Paint(p.Accent, p.OnStrong);
            UpdateTime();
        }

        private static void PaintChoice(Tap t, bool on, Palette p) =>
            t.Paint(on ? p.Accent : Brushes.Transparent, on ? p.OnStrong : p.Text, p.Edge);
    } // public sealed class AnnouncementOverlay
}
