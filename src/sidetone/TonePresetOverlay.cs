// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks. Graphical front end.
*
* The Tones box: the operator's tone presets and DTMF presets (TonePresets.cs), each with Send, Edit and
* Delete; New tone preset, New DTMF preset and Import MIDI…. Sending goes on the focused channel, as an
* alert tone. Presets live in the settings file, so the terminal console sends the same ones.
*/

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sidetone.Core;

namespace Sidetone.Gui
{
    public sealed class TonePresetOverlay
    {
        public readonly Grid Root = new Grid { IsVisible = false };

        private readonly Func<DispatchConsole> console;
        private readonly Func<AppSettings> settings;
        private readonly Action<Action<AppSettings>> change;
        private readonly Func<Task<TonePreset>> importMidi;
        private readonly Action changed, closed;
        private readonly Border scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
        private readonly Border card = new Border
        {
            Width = 660,
            Padding = new Thickness(22, 18, 22, 18),
            Margin = new Thickness(0, 10),
            CornerRadius = new CornerRadius(Ui.CornerRadius),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly TextBlock title = new TextBlock { Text = "Tones", FontSize = 19, FontWeight = FontWeight.Bold };
        private readonly TextBlock note = new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = "Your own tone sequences and DTMF strings. Send puts one on the focused channel, like an alert tone; Esc stops it."
        };
        private readonly TextBlock message = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel rows = new StackPanel { Spacing = 4 };
        private readonly List<TextBlock> headings = new List<TextBlock>(), lines = new List<TextBlock>();
        private readonly List<Tap> rowTaps = new List<Tap>();

        // the editor
        private readonly StackPanel editor = new StackPanel { Spacing = 8, IsVisible = false, Margin = new Thickness(0, 10, 0, 0) };
        private readonly TextBlock editorTitle = new TextBlock { FontSize = 14, FontWeight = FontWeight.Bold };
        private readonly TextBox nameBox = new TextBox { FontSize = 13, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, PlaceholderText = "a name" };
        private readonly TextBox stepsBox = new TextBox { FontSize = 13, Width = 600, HorizontalAlignment = HorizontalAlignment.Left, PlaceholderText = "1000:0.5, 0:0.2, 1500:1" };
        private readonly TextBox digitsBox = new TextBox { FontSize = 13, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, PlaceholderText = "like 911# or 1234,5678" };
        private readonly TextBox digitMsBox = new TextBox { FontSize = 13, Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox gapMsBox = new TextBox { FontSize = 13, Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel toneFields = new StackPanel { Spacing = 6 }, dtmfFields = new StackPanel { Spacing = 6 };
        private readonly List<TextBlock> labels = new List<TextBlock>();
        private readonly Tap save, cancel, newTone, newDtmf, import, done;
        private bool editingDtmf;
        private int editingIndex = -1;      // -1: a new one

        public bool IsOpen => Root.IsVisible;
        /// <summary>What the last action said (self-check hook).</summary>
        internal string Message => message.Text ?? string.Empty;
        internal bool Editing => editor.IsVisible;

        public TonePresetOverlay(Func<DispatchConsole> console, Func<AppSettings> settings, Action<Action<AppSettings>> change,
                                 Func<Task<TonePreset>> importMidi, Action changed, Action closed)
        {
            this.console = console;
            this.settings = settings;
            this.change = change;
            this.importMidi = importMidi;
            this.changed = changed;
            this.closed = closed;

            save = new Tap("Save", Save, fontSize: 13, minWidth: 90, padY: 6);
            cancel = new Tap("Cancel", CloseEditor, fontSize: 13, minWidth: 90, padY: 6);
            newTone = new Tap("New tone preset", () => Edit(false, -1), fontSize: 13, padY: 6);
            newDtmf = new Tap("New DTMF preset", () => Edit(true, -1), fontSize: 13, padY: 6);
            import = new Tap("Import MIDI…", () => _ = Import(), fontSize: 13, padY: 6);
            done = new Tap("Done", Close, fontSize: 13, minWidth: 110, padY: 8);

            toneFields.Children.Add(Label("Steps: Hz:seconds, separated by commas; 0 Hz is silence (281 to 2500 Hz)"));
            toneFields.Children.Add(stepsBox);
            dtmfFields.Children.Add(Label("Digits: 0-9, *, #, A-D; a comma pauses half a second"));
            dtmfFields.Children.Add(digitsBox);
            WrapPanel lengths = new WrapPanel { ItemSpacing = 8, LineSpacing = 4 };
            lengths.Children.Add(Label("Each digit (ms)"));
            lengths.Children.Add(digitMsBox);
            lengths.Children.Add(Label("Gap (ms)"));
            lengths.Children.Add(gapMsBox);
            dtmfFields.Children.Add(lengths);
            WrapPanel editButtons = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
            editButtons.Children.Add(save.Root);
            editButtons.Children.Add(cancel.Root);
            editor.Children.Add(editorTitle);
            editor.Children.Add(Label("Name"));
            editor.Children.Add(nameBox);
            editor.Children.Add(toneFields);
            editor.Children.Add(dtmfFields);
            editor.Children.Add(hint);
            editor.Children.Add(editButtons);

            StackPanel head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(title);
            head.Children.Add(note);
            head.Children.Add(message);
            WrapPanel adds = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
            adds.Children.Add(newTone.Root);
            adds.Children.Add(newDtmf.Root);
            adds.Children.Add(import.Root);
            Grid foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
            foot.Children.Add(adds);
            Grid.SetColumn(done.Root, 1);
            foot.Children.Add(done.Root);

            StackPanel body = new StackPanel();
            body.Children.Add(rows);
            body.Children.Add(editor);
            DockPanel dock = new DockPanel();
            DockPanel.SetDock(head, Dock.Top);
            DockPanel.SetDock(foot, Dock.Bottom);
            dock.Children.Add(head);
            dock.Children.Add(foot);
            dock.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            card.Child = dock;

            scrim.PointerPressed += (s, e) => { e.Handled = true; Close(); };
            card.PointerPressed += (s, e) => e.Handled = true;
            Root.Children.Add(scrim);
            Root.Children.Add(card);
        }

        private TextBlock Label(string text)
        {
            TextBlock t = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            labels.Add(t);
            return t;
        }

        private List<TonePreset> Tones => settings()?.TonePresets ?? new List<TonePreset>();
        private List<DtmfPreset> Dtmfs => settings()?.DtmfPresets ?? new List<DtmfPreset>();

        public void Show()
        {
            message.Text = string.Empty;
            CloseEditor();
            Root.IsVisible = true;
        }

        public void Close()
        {
            if (!Root.IsVisible)
                return;
            Root.IsVisible = false;
            closed();
        }

        /// <summary>Esc: closes the editor if it's open, else the box.</summary>
        public void Escape()
        {
            if (editor.IsVisible)
                CloseEditor();
            else
                Close();
        }

        private void Rebuild()
        {
            rows.Children.Clear();
            headings.Clear();
            lines.Clear();
            rowTaps.Clear();
            AddSection("Tone presets", Tones.Select((t, i) => (t.Name, t.Summary, false, i)).ToList(), "None yet: New tone preset, or Import MIDI…");
            AddSection("DTMF presets", Dtmfs.Select((d, i) => (d.Name, d.Digits, true, i)).ToList(), "None yet: New DTMF preset.");
        }

        private void AddSection(string heading, List<(string Name, string Summary, bool Dtmf, int Index)> items, string empty)
        {
            TextBlock h = new TextBlock { Text = heading, FontSize = 14, FontWeight = FontWeight.Bold, Margin = new Thickness(0, rows.Children.Count == 0 ? 0 : 12, 0, 2) };
            headings.Add(h);
            rows.Children.Add(h);
            if (items.Count == 0)
            {
                TextBlock none = new TextBlock { Text = empty, FontSize = 13 };
                lines.Add(none);
                rows.Children.Add(none);
                return;
            }
            foreach ((string name, string summary, bool dtmf, int index) in items)
            {
                TextBlock line = new TextBlock { Text = $"{name}   {summary}", FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                lines.Add(line);
                WrapPanel taps = new WrapPanel { ItemSpacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
                foreach (Tap t in new[]
                {
                    new Tap("Send", () => Send(dtmf, index), fontSize: 12, padX: 12, padY: 5),
                    new Tap("Edit", () => Edit(dtmf, index), fontSize: 12, padX: 10, padY: 5),
                    new Tap("Delete", () => Delete(dtmf, index), fontSize: 12, padX: 10, padY: 5)
                })
                {
                    rowTaps.Add(t);
                    taps.Children.Add(t.Root);
                }
                Grid row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 0, 0, 0) };
                row.Children.Add(line);
                Grid.SetColumn(taps, 1);
                row.Children.Add(taps);
                rows.Children.Add(row);
            }
        }

        /// <summary>Sends one preset (also the self-check's way in).</summary>
        internal void Send(bool dtmf, int index)
        {
            DispatchConsole c = console();
            if (c == null)
            {
                message.Text = "Open a codeplug and listen to a channel first.";
                return;
            }
            string why = dtmf ? c.SendDtmfPreset(Dtmfs[index]) : c.SendTonePreset(Tones[index]);
            message.Text = why ?? string.Empty;
            changed();
        }

        private void Edit(bool dtmf, int index)
        {
            editingDtmf = dtmf;
            editingIndex = index;
            message.Text = string.Empty;
            hint.Text = string.Empty;
            editorTitle.Text = index < 0 ? (dtmf ? "New DTMF preset" : "New tone preset") : $"Edit {(dtmf ? Dtmfs[index].Name : Tones[index].Name)}";
            if (dtmf)
            {
                DtmfPreset d = index < 0 ? new DtmfPreset() : Dtmfs[index];
                nameBox.Text = d.Name;
                digitsBox.Text = d.Digits;
                digitMsBox.Text = d.DigitMs.ToString(CultureInfo.InvariantCulture);
                gapMsBox.Text = d.GapMs.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                TonePreset t = index < 0 ? new TonePreset() : Tones[index];
                nameBox.Text = t.Name;
                stepsBox.Text = TonePresets.FormatSteps(t.Steps);
            }
            toneFields.IsVisible = !dtmf;
            dtmfFields.IsVisible = dtmf;
            rows.IsVisible = false;
            editor.IsVisible = true;
            changed();
        }

        private void CloseEditor()
        {
            editor.IsVisible = false;
            rows.IsVisible = true;
            Rebuild();
            changed();
        }

        /// <summary>Fills the editor (self-check hook).</summary>
        internal void Fill(string name, string steps = null, string digits = null, int digitMs = 100, int gapMs = 100)
        {
            nameBox.Text = name;
            stepsBox.Text = steps;
            digitsBox.Text = digits;
            digitMsBox.Text = digitMs.ToString(CultureInfo.InvariantCulture);
            gapMsBox.Text = gapMs.ToString(CultureInfo.InvariantCulture);
        }
        internal void StartNew(bool dtmf) => Edit(dtmf, -1);

        /// <summary>Checks the editor and saves the preset into Settings; the reason stays in the box when it can't.</summary>
        internal void Save()
        {
            string name = (nameBox.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                hint.Text = "Give it a name.";
                return;
            }
            try
            {
                if (editingDtmf)
                {
                    if (!int.TryParse(digitMsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int digitMs) ||
                        !int.TryParse(gapMsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gapMs))
                        throw new ArgumentException("the digit and gap lengths are whole milliseconds");
                    DtmfPreset d = new DtmfPreset { Name = name, Digits = TonePresets.CheckDtmf(digitsBox.Text, digitMs, gapMs), DigitMs = digitMs, GapMs = gapMs };
                    List<DtmfPreset> list = Dtmfs.ToList();
                    if (editingIndex < 0) list.Add(d); else list[editingIndex] = d;
                    change(s => s.DtmfPresets = list);
                }
                else
                {
                    TonePreset t = new TonePreset { Name = name, Steps = TonePresets.ParseSteps(stepsBox.Text) };
                    List<TonePreset> list = Tones.ToList();
                    if (editingIndex < 0) list.Add(t); else list[editingIndex] = t;
                    change(s => s.TonePresets = list);
                }
            }
            catch (ArgumentException ex)
            {
                hint.Text = char.ToUpperInvariant(ex.Message[0]) + ex.Message.Substring(1) + ".";
                return;
            }
            message.Text = $"Saved {name}.";
            CloseEditor();
        }

        private void Delete(bool dtmf, int index)
        {
            string name;
            if (dtmf)
            {
                List<DtmfPreset> list = Dtmfs.ToList();
                name = list[index].Name;
                list.RemoveAt(index);
                change(s => s.DtmfPresets = list.Count == 0 ? null : list);
            }
            else
            {
                List<TonePreset> list = Tones.ToList();
                name = list[index].Name;
                list.RemoveAt(index);
                change(s => s.TonePresets = list.Count == 0 ? null : list);
            }
            message.Text = $"Deleted {name}.";
            Rebuild();
            changed();
        }

        /// <summary>Import MIDI…: the picked file becomes a new tone preset, opened in the editor to check and save.</summary>
        private async Task Import()
        {
            try
            {
                TonePreset imported = await importMidi();
                if (imported == null)
                    return;
                Edit(false, -1);
                nameBox.Text = imported.Name;
                stepsBox.Text = TonePresets.FormatSteps(imported.Steps);
                hint.Text = $"Imported {imported.Summary}. Check it, then Save.";
            }
            catch (ArgumentException ex)
            {
                message.Text = $"Couldn't import it: {ex.Message}";
            }
            changed();
        }

        /// <summary>How many presets are listed (self-check hook).</summary>
        internal (int Tones, int Dtmf) Counts => (Tones.Count, Dtmfs.Count);
        internal string Hint => hint.Text ?? string.Empty;

        public void Refresh(Palette p)
        {
            if (!Root.IsVisible)
                return;
            card.Background = p.Panel;
            card.BorderBrush = p.Edge;
            title.Foreground = p.Text;
            note.Foreground = p.Dim;
            message.Foreground = p.Accent;
            hint.Foreground = p.Warn;
            editorTitle.Foreground = p.Text;
            foreach (TextBlock t in headings)
                t.Foreground = p.Text;
            foreach (TextBlock t in lines)
                t.Foreground = p.Text;
            foreach (TextBlock t in labels)
                t.Foreground = p.Dim;
            foreach (Tap t in rowTaps)
                t.Paint(Brushes.Transparent, p.Text, p.Edge);
            foreach (Tap t in new[] { newTone, newDtmf, import, cancel })
                t.Paint(Brushes.Transparent, p.Text, p.Edge);
            save.Paint(p.Accent, p.OnStrong);
            done.Paint(p.Accent, p.OnStrong);
        }
    } // public sealed class TonePresetOverlay
}
