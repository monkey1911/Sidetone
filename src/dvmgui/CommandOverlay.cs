// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmgui - the graphical front end of the Linux DVM console.
*
* The page and radio-command boxes. They open over the console inside the main window (not as
* separate windows, which tiling desktops place unpredictably): a page is either two QuickCall II
* tones on every listening channel or a digital call alert to one radio; a radio command is a
* check, inhibit or uninhibit for one radio. The rules are the console's (dvmcore), shared with
* the TUI; this class is the form.
*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace dvmgui
{
    public sealed class CommandOverlay
    {
        public enum Kind { None, Page, Radio }

        /// <summary>Covers the console while a box is open.</summary>
        public readonly Grid Root = new Grid { IsVisible = false };
        public readonly TextBox ToneA = Box(7), ToneB = Box(7), PageRid = Box(8), RadioRid = Box(8);

        private readonly Func<DispatchConsole> console;
        private readonly Action changed, closed;

        private readonly Border scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
        private readonly Border card = new Border
        {
            Width = 460,
            Padding = new Thickness(22, 18, 22, 18),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly TextBlock title = new TextBlock { FontSize = 19, FontWeight = FontWeight.Bold };
        private readonly TextBlock note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock alias = new TextBlock { FontSize = 12, MinHeight = 16 };
        private readonly TextBlock message = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MinHeight = 18 };
        private readonly List<TextBlock> labels = new List<TextBlock>();
        private readonly StackPanel modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        private readonly StackPanel tonePanel = new StackPanel { Spacing = 8 };
        private readonly StackPanel pageRidPanel = new StackPanel { Spacing = 8 };
        private readonly StackPanel radioPanel = new StackPanel { Spacing = 10 };
        private readonly StackPanel actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        private readonly Tap twoTone, digital, cancel, send;
        private readonly List<Tap> actions = new List<Tap>();

        private bool messageIsQuestion;
        private uint confirmRid;        // inhibit: the radio ID Send was pressed for once

        /// <summary>Which box is open.</summary>
        public Kind Open { get; private set; }
        /// <summary>The page box is on its digital (call alert) side.</summary>
        public bool Digital { get; private set; }
        /// <summary>The selected radio command (an index into DispatchConsole.RadioActions).</summary>
        public int RadioAction { get; private set; }
        /// <summary>The error or question being shown.</summary>
        public string Message => message.Text ?? string.Empty;

        public CommandOverlay(Func<DispatchConsole> console, Action changed, Action closed)
        {
            this.console = console;
            this.changed = changed;
            this.closed = closed;

            twoTone = new Tap("Two-tone (QuickCall II)", () => SetDigital(false), fontSize: 12, padX: 10, padY: 5);
            digital = new Tap("Digital (call alert)", () => SetDigital(true), fontSize: 12, padX: 10, padY: 5);
            modeRow.Children.Add(twoTone.Root);
            modeRow.Children.Add(digital.Root);

            tonePanel.Children.Add(Field("Tone A (Hz)", ToneA));
            tonePanel.Children.Add(Field("Tone B (Hz)", ToneB));
            pageRidPanel.Children.Add(Field("Radio ID", PageRid));

            for (int i = 0; i < DispatchConsole.RadioActions.Length; i++)
            {
                int index = i;
                Tap t = new Tap(DispatchConsole.RadioActions[i].Label, () => SelectRadioAction(index), fontSize: 12, minWidth: 84, padX: 10, padY: 5);
                actions.Add(t);
                actionRow.Children.Add(t.Root);
            }
            radioPanel.Children.Add(Field("Radio ID", RadioRid));
            radioPanel.Children.Add(Field("Command", actionRow));

            foreach (TextBox box in new[] { ToneA, ToneB, PageRid, RadioRid })
                box.TextChanged += (s, e) => ClearMessage();

            cancel = new Tap("Cancel", Close, fontSize: 13, minWidth: 90, padY: 8);
            send = new Tap("Send", Submit, fontSize: 13, minWidth: 110, padY: 8);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            buttons.Children.Add(cancel.Root);
            buttons.Children.Add(send.Root);

            StackPanel stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(title);
            stack.Children.Add(modeRow);
            stack.Children.Add(tonePanel);
            stack.Children.Add(pageRidPanel);
            stack.Children.Add(radioPanel);
            stack.Children.Add(alias);
            stack.Children.Add(note);
            stack.Children.Add(message);
            stack.Children.Add(buttons);
            card.Child = stack;

            scrim.PointerPressed += (s, e) => { e.Handled = true; Close(); };
            card.PointerPressed += (s, e) => e.Handled = true;      // not a click on a channel card underneath
            Root.Children.Add(scrim);
            Root.Children.Add(card);
        }

        private static TextBox Box(int maxLength) => new TextBox { Width = 170, MaxLength = maxLength, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Left };

        private Grid Field(string label, Control control)
        {
            TextBlock text = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            labels.Add(text);
            Grid g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*") };
            Grid.SetColumn(text, 0);
            Grid.SetColumn(control, 1);
            g.Children.Add(text);
            g.Children.Add(control);
            return g;
        }

        /*
        ** Opening and closing
        */

        /// <summary>Opens the page box with the last page's values filled in.</summary>
        public void ShowPage(bool digitalPage)
        {
            DispatchConsole c = console();
            if (c == null)
                return;
            ToneA.Text = c.LastPageA;
            ToneB.Text = c.LastPageB;
            PageRid.Text = c.LastPageRid;
            Open = Kind.Page;
            Digital = digitalPage;
            Show(digitalPage ? PageRid : ToneA);
        }

        /// <summary>Opens the radio command box with the last radio ID filled in.</summary>
        public void ShowRadio()
        {
            DispatchConsole c = console();
            if (c == null)
                return;
            RadioRid.Text = c.LastPageRid;
            RadioAction = 0;
            Open = Kind.Radio;
            Show(RadioRid);
        }

        private void Show(TextBox first)
        {
            confirmRid = 0;
            ClearMessage();
            Arrange();
            Root.IsVisible = true;
            changed();
            FocusLater(first);
        }

        // after the key or click that opened the box has finished, so it isn't typed into the field
        private static void FocusLater(TextBox box) =>
            Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            }, DispatcherPriority.Background);

        public void Close()
        {
            if (Open == Kind.None)
                return;
            Open = Kind.None;
            Root.IsVisible = false;
            closed();
            changed();
        }

        private void SetDigital(bool value)
        {
            Digital = value;
            ClearMessage();
            Arrange();
            changed();
            FocusLater(value ? PageRid : ToneA);
        }

        /// <summary>Chooses the radio command.</summary>
        public void SelectRadioAction(int action)
        {
            RadioAction = Math.Clamp(action, 0, DispatchConsole.RadioActions.Length - 1);
            confirmRid = 0;
            ClearMessage();
            Arrange();
            changed();
        }

        private void Arrange()
        {
            bool page = Open == Kind.Page;
            title.Text = page ? "Page" : "Radio command";
            modeRow.IsVisible = page;
            tonePanel.IsVisible = page && !Digital;
            pageRidPanel.IsVisible = page && Digital;
            radioPanel.IsVisible = Open == Kind.Radio;
            alias.IsVisible = !(page && !Digital);

            if (page && !Digital)
                note.Text = $"Sent on every listening channel: tone A for {TonePlan.PageAMs / 1000:0.#} s, then tone B for {TonePlan.PageBMs / 1000:0.#} s. " +
                            $"Tones can be {TonePlan.MinPageHz:F0} to {TonePlan.MaxPageHz:F0} Hz.";
            else if (page)
                note.Text = "A P25 call alert to one radio. It doesn't use a talkgroup.";
            else
                note.Text = RadioAction switch
                {
                    1 => "Inhibit disables the radio until it is uninhibited.",
                    2 => "Uninhibit re-enables an inhibited radio.",
                    _ => "Check asks the radio to answer. The reply shows in the log."
                };
            send.Text = Open == Kind.Radio ? $"Send {DispatchConsole.RadioActions[RadioAction].Label.ToLowerInvariant()}" : "Send page";
        }

        private void ClearMessage()
        {
            if (string.IsNullOrEmpty(message.Text) && confirmRid == 0)
                return;
            message.Text = string.Empty;
            messageIsQuestion = false;
            confirmRid = 0;
        }

        private void Say(string text, bool question = false)
        {
            message.Text = text;
            messageIsQuestion = question;
            changed();
        }

        /*
        ** Sending
        */

        /// <summary>Enter was pressed: in the two-tone box, with tone A typed and tone B empty, go on to tone B; otherwise send.</summary>
        public void Enter()
        {
            if (Open == Kind.Page && !Digital && ToneA.IsFocused && string.IsNullOrWhiteSpace(ToneB.Text))
            {
                ToneB.Focus();
                return;
            }
            Submit();
        }

        /// <summary>Checks the form and sends; on any problem the box stays open and says why.</summary>
        public void Submit()
        {
            DispatchConsole c = console();
            if (c == null || Open == Kind.None)
                return;

            if (Open == Kind.Page && !Digital)
            {
                string error = c.SendTonePage(ToneA.Text, ToneB.Text);
                if (error != null)
                    Say(Sentence(error));
                else
                    Close();
                return;
            }

            TextBox box = Open == Kind.Radio ? RadioRid : PageRid;
            if (!DispatchConsole.TryParseRid(box.Text, out uint rid))
            {
                Say("Enter a radio ID from 1 to 16777215.");
                return;
            }

            if (Open == Kind.Radio)
            {
                if (DispatchConsole.RadioActionNeedsConfirm(RadioAction) && confirmRid != rid)
                {
                    Say($"Press Send again to inhibit {c.Aliases.Describe(rid)}.", question: true);
                    confirmRid = rid;
                    return;
                }
                string error = c.SendRadioCommand(RadioAction, rid);
                if (error != null)
                {
                    confirmRid = 0;
                    Say(Sentence(error));
                    return;
                }
            }
            else
            {
                string error = c.SendDigitalPage(rid);
                if (error != null)
                {
                    Say(Sentence(error));
                    return;
                }
            }
            Close();
        }

        private static string Sentence(string text) =>
            string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1) + (text.EndsWith('.') ? string.Empty : ".");

        /*
        ** Painting
        */

        /// <summary>Paints the open box for the theme and shows the alias of the radio ID being typed.</summary>
        public void Refresh(Palette p)
        {
            if (Open == Kind.None)
                return;

            card.Background = p.Panel;
            card.BorderBrush = p.Edge;
            title.Foreground = p.Text;
            note.Foreground = p.Dim;
            alias.Foreground = p.Dim;
            message.Foreground = messageIsQuestion ? p.Warn : p.Tx;
            foreach (TextBlock label in labels)
                label.Foreground = p.Dim;

            Paint(twoTone, !Digital, p);
            Paint(digital, Digital, p);
            for (int i = 0; i < actions.Count; i++)
                Paint(actions[i], i == RadioAction, p);
            cancel.Paint(Brushes.Transparent, p.Text, p.Edge);
            if (Open == Kind.Radio && DispatchConsole.RadioActionNeedsConfirm(RadioAction))
                send.Paint(p.Tx, p.OnStrong);
            else
                send.Paint(p.Accent, p.OnStrong);

            DispatchConsole c = console();
            string typed = Open == Kind.Radio ? RadioRid.Text : PageRid.Text;
            string name = c != null && DispatchConsole.TryParseRid(typed, out uint rid) ? c.Aliases.Name(rid) : null;
            string shown = string.IsNullOrWhiteSpace(typed) ? string.Empty : name ?? "no alias for this radio ID";
            if (alias.Text != shown)
                alias.Text = shown;
        }

        private static void Paint(Tap tap, bool selected, Palette p)
        {
            if (selected)
                tap.Paint(p.Accent, p.OnStrong);
            else
                tap.Paint(Brushes.Transparent, p.Dim, p.Edge);
        }
    } // public sealed class CommandOverlay
}
